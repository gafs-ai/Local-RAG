using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Legacy XLS conversion through a bounded, separately owned Excel automation process.</summary>
public static class LegacyExcelConversion
{
    public static async Task<byte[]> ConvertAsync(byte[] input, string dataDirectory, CancellationToken token, string extension = ".xls")
    {
        token.ThrowIfCancellationRequested();
        // AutomationSecurity disables VBA, not Excel 4.0 macros. Reject unsafe BIFF before Office sees it.
        extension = extension.ToLowerInvariant();
        if (extension == ".xlsb") XlsbPreflight.Validate(input, token);
        else if (extension == ".xls") LegacyExcelPreflight.Validate(input);
        else throw Failure("excel_extension", "Excel変換の拡張子が不正です。");
        var output = await RunWorkerAsync(input, dataDirectory, Script(), "output.xlsx", token, extension).ConfigureAwait(false);
        try
        {
            using var stream = new MemoryStream(output, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
                throw Failure("excel_package_size", "Excel変換結果の展開後サイズが上限を超えています。");
            if (archive.GetEntry("xl/workbook.xml") is null || archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw Failure("excel_conversion", "Excel変換結果が通常のXLSX形式ではありません。");
        }
        catch (InvalidDataException) { throw Failure("excel_conversion", "Excel変換結果の形式が不正です。"); }
        return output;
    }

    internal static string Script()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalRag.Infrastructure.ConvertExcel.ps1")
            ?? throw Failure("excel_conversion", "Excel変換スクリプトがありません。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The internal script override is used only by the opt-in test to create a synthetic BIFF8 workbook.
    internal static async Task<byte[]> RunWorkerAsync(byte[] input, string dataDirectory, string scriptText,
        string outputName, CancellationToken token, string inputExtension = ".xls")
    {
        if (outputName is not ("output.xlsx" or "output.xls" or "output.xlsb" or "output.xlsm")) throw new ArgumentException("Unexpected output name.", nameof(outputName));
        if (inputExtension is not (".xls" or ".xlsb")) throw new ArgumentException("Unexpected input extension.", nameof(inputExtension));
        if (input.Length > SpreadsheetDocuments.MaxPackageBytes) throw Failure("excel_package_size", "Excelファイルが上限を超えています。");
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "excel-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? worker = null;
        try
        {
            var script = Path.Combine(directory, "Convert.ps1");
            await File.WriteAllTextAsync(script, scriptText.Replace("'input.xls'", "'input" + inputExtension + "'", StringComparison.Ordinal), token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(directory, "input" + inputExtension), input, token).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File", script }) start.ArgumentList.Add(argument);
            try { worker = Process.Start(start) ?? throw Failure("excel_conversion", "Excel変換を起動できません。"); }
            catch (Win32Exception) { throw Failure("excel_conversion", "Excel変換プロセスを起動できません。"); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw Failure("excel_conversion_timeout", "Excel変換が2分以内に終了しませんでした。"); }
            if (worker.ExitCode != 0)
                throw worker.ExitCode switch
                {
                    10 => Failure("excel_not_installed", ".xls・.xlsbの変換にはMicrosoft Excelのインストールが必要です。"),
                    12 => Failure("excel_conversion", "既存のExcelとは別の変換用プロセスを確認できませんでした。"),
                    13 => Failure("excel_package_size", "Excel変換出力が上限を超えています。"),
                    _ => Failure("excel_conversion", "Excelで読み取り専用の変換ができませんでした。" + FailureDetail(directory))
                };
            var output = Path.Combine(directory, outputName);
            if (!File.Exists(output)) throw Failure("excel_conversion", "Excel変換出力がありません。");
            var length = new FileInfo(output).Length;
            if (length is <= 0 or > SpreadsheetDocuments.MaxPackageBytes)
                throw Failure("excel_package_size", "Excel変換出力が上限を超えています。");
            return await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        }
        finally
        {
            // Never kill a process by name or a COM server that was present before this worker.
            var cleaned = worker is null || await StopAsync(worker).ConfigureAwait(false);
            worker?.Dispose();
            cleaned &= await StopOwnedExcelAsync(Path.Combine(directory, "owned.json")).ConfigureAwait(false);
            // If cleanup cannot prove the owned process ended, retain its identity for diagnosis.
            if (cleaned)
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> StopAsync(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            process.Kill();
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException) { return true; }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or NotSupportedException) { return false; }
    }

    private static string FailureDetail(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "failure.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return "";
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var stage = json.RootElement.GetProperty("Stage").GetString();
            if (stage is not ("starting" or "ownership" or "setup" or "open" or "export")) return "";
            var hresult = json.RootElement.GetProperty("HResult").GetInt32();
            var line = json.RootElement.GetProperty("Line").GetInt32();
            return $"（工程:{stage}、HRESULT:0x{hresult:X8}、スクリプト行:{line}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return ""; }
    }

    private static async Task<bool> StopOwnedExcelAsync(string path)
    {
        // The worker may be cancelled between writing the complete identity and its atomic rename.
        if (!File.Exists(path)) path = Path.ChangeExtension(path, ".tmp");
        // COM activation may stall before Hwnd/identity is available. Never guess which process to kill;
        // retain the job directory in that case so the interrupted startup can be diagnosed.
        if (!File.Exists(path)) return !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "stage.txt"));
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            var identity = json.RootElement;
            var id = identity.GetProperty("Id").GetInt32();
            var ticks = identity.GetProperty("StartUtcTicks").GetInt64();
            using var excel = Process.GetProcessById(id);
            if (!excel.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) || excel.StartTime.ToUniversalTime().Ticks != ticks)
                return true; // The recorded process has gone; a reused PID is not ours.
            return await StopAsync(excel).ConfigureAwait(false);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception or KeyNotFoundException or FormatException)
        { return false; } // Cleanup diagnostics must not replace the original conversion failure/cancellation.
    }
    private static PipelineValidationException Failure(string code, string message) => new(code, message);
}
