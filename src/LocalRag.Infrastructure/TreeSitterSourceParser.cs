using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public static class TreeSitterSourceParser
{
    public static async Task<List<SourceCodeUnit>> ParseAsync(string code, string path, string grammar, string toolsDirectory, CancellationToken token)
    {
        var executable = await new PythonParserRuntime(toolsDirectory).EnsureAsync(token).ConfigureAwait(false);
        var paths = await TreeSitterRuntime.EnsureAsync(toolsDirectory, grammar, token).ConfigureAwait(false);
        using var resource = typeof(TreeSitterSourceParser).Assembly.GetManifestResourceStream("LocalRag.Infrastructure.TreeSitterParse.py")!;
        using var reader = new StreamReader(resource);
        var script = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-I", "-S", "-X", "utf8", "-c", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("構文解析を開始できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var input = JsonSerializer.Serialize(new { code, grammar, paths, tsx = Path.GetExtension(path).Equals(".tsx", StringComparison.OrdinalIgnoreCase) });
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new PipelineValidationException(process.ExitCode == 2 ? "code_syntax" : "code_parser",
                "選択された言語で構文解析できませんでした。");
            var definitions = JsonSerializer.Deserialize<List<Unit>>(await output.ConfigureAwait(false)) ?? throw new InvalidDataException("構文解析の応答が不正です。");
            var bytes = Encoding.UTF8.GetBytes(code);
            var lines = code.Count(c => c == '\n') + 1;
            var units = new List<SourceCodeUnit> { new("file", null, "file", Path.GetFileName(path), 1, lines, code) };
            foreach (var unit in definitions)
            {
                if (unit.StartByte < 0 || unit.EndByte <= unit.StartByte || unit.EndByte > bytes.Length || unit.StartLine < 1 ||
                    unit.EndLine < unit.StartLine || unit.EndLine > lines || units.Any(u => u.Id == unit.Id) ||
                    !units.Any(u => u.Id == unit.ContainerId) || unit.Kind is not ("class" or "function"))
                    throw new InvalidDataException("ソースコードの範囲が不正です。");
                units.Add(new(unit.Id, unit.ContainerId, unit.Kind, unit.Name, unit.StartLine, unit.EndLine,
                    new UTF8Encoding(false, true).GetString(bytes, unit.StartByte, unit.EndByte - unit.StartByte)));
            }
            return units;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
    }
    private sealed record Unit(string Id, string ContainerId, string Kind, string Name, int StartByte, int EndByte, int StartLine, int EndLine);
}
