using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Legacy .doc conversion runs outside the app, with a bounded lifetime and an isolated Word instance.</summary>
public static class LegacyWordConversion
{
    public static async Task<byte[]> ConvertAsync(byte[] input, string dataDirectory, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "word-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "Convert.ps1");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalRag.Infrastructure.ConvertWord.ps1")!;
        using (var reader = new StreamReader(resource)) await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(directory, "input.doc"), input, token).ConfigureAwait(false);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File", script }) info.ArgumentList.Add(argument);
        Process? worker = null;
        try
        {
            worker = Process.Start(info) ?? throw new PipelineValidationException("word_conversion", "Word変換を起動できません。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new PipelineValidationException("word_conversion_timeout", "Word変換が時間内に終了しませんでした。"); }
            if (worker.ExitCode != 0)
                throw new PipelineValidationException(worker.ExitCode == 10 ? "word_not_installed" : "word_conversion", "Wordで変換できませんでした。");
            var xmlPath = Path.Combine(directory, "output.xml");
            if (new FileInfo(xmlPath).Length > 128 * 1024 * 1024) throw new PipelineValidationException("word_package_size", "変換出力が上限を超えています。");
            return PackageFlatXml(xmlPath);
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync().ConfigureAwait(false); }
                worker.Dispose();
            }
            // Never terminate Word by name. Only a newly-created, recorded PID with matching creation time is owned.
            var identity = Path.Combine(directory, "owned.json");
            if (File.Exists(identity))
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(identity).ConfigureAwait(false));
                try
                {
                    using var word = Process.GetProcessById(json.RootElement.GetProperty("Id").GetInt32());
                    if (word.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase) &&
                        word.StartTime.ToUniversalTime().Ticks == json.RootElement.GetProperty("StartUtcTicks").GetInt64())
                    { word.Kill(); await word.WaitForExitAsync().ConfigureAwait(false); }
                }
                catch (ArgumentException) { }
            }
            // This exact per-call directory was created above; no source path is used for removal.
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Retain inaccessible temporary files; never hide the conversion error. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] PackageFlatXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        XNamespace pkg = "http://schemas.microsoft.com/office/2006/xmlPackage";
        if (document.Root?.Name != pkg + "package") throw new PipelineValidationException("word_invalid", "変換出力の形式が不正です。");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var part in document.Root.Elements(pkg + "part"))
            {
                var name = ((string?)part.Attribute(pkg + "name") ?? "").TrimStart('/');
                if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Invalid part name.");
                using var entry = archive.CreateEntry(name).Open();
                if (part.Element(pkg + "xmlData") is { } xml) xml.Elements().Single().Save(entry);
                else if (part.Element(pkg + "binaryData") is { } data) entry.Write(Convert.FromBase64String(data.Value));
                else throw new InvalidDataException("Missing part data.");
            }
        }
        return stream.ToArray();
    }
}
