using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalRag.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalRag.Infrastructure;

public static class SourceCodeParser
{
    public static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".py";
    public static bool Applies(TextRagSettings settings, string path) => settings.SourceMode == SourceCodeMode.Code || SourceLanguages.IsCode(settings.SourceMode) ||
        settings.SourceMode == SourceCodeMode.Auto && Supported(path);

    public static async Task<List<SourceCodeUnit>> ParseAsync(string code, string path, string toolsDirectory, CancellationToken token,
        SourceCodeMode mode = SourceCodeMode.Auto)
    {
        if (SourceLanguages.Find(mode)?.Grammar is { } grammar)
            return await TreeSitterSourceParser.ParseAsync(code, path, grammar, toolsDirectory, token).ConfigureAwait(false);
        if (mode is not (SourceCodeMode.CSharp or SourceCodeMode.Python) && !Supported(path))
            throw new PipelineValidationException("code_language", "適用する設定で対応する言語を選択してください。");
        return mode == SourceCodeMode.CSharp || mode != SourceCodeMode.Python && Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? ParseCSharp(code, path, token) :
            await ParsePythonAsync(code, path, toolsDirectory, token).ConfigureAwait(false);
    }

    public static List<SourceCodeUnit> ParseCSharp(string code, string path, CancellationToken token)
    {
        var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: token);
        if (tree.GetDiagnostics(token).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new PipelineValidationException("code_syntax", "C#の構文を解析できません。原文を修正するかテキスト処理を選択してください。");
        var source = tree.GetText(token);
        var units = new List<SourceCodeUnit> { new("file", null, "file", Path.GetFileName(path), 1, source.Lines.Count, code) };
        var ids = new Dictionary<SyntaxNode, string>();
        foreach (var node in tree.GetRoot(token).DescendantNodes())
        {
            token.ThrowIfCancellationRequested();
            var name = node switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
                DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.ValueText,
                LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
                ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
                AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
                _ => null
            };
            if (name is null) continue;
            if (units.Count >= 1000) throw new PipelineValidationException("code_units", "コード単位の数が上限を超えています。");
            var id = "u" + units.Count;
            var container = node.Ancestors().Select(n => ids.GetValueOrDefault(n)).FirstOrDefault(n => n is not null) ?? "file";
            var span = node.Span;
            var start = node.FullSpan.Start;
            // Include comments/attributes attached to a declaration; retain original text exactly.
            var end = span.End;
            var firstLine = source.Lines.GetLineFromPosition(start).LineNumber + 1;
            var lastLine = source.Lines.GetLineFromPosition(Math.Max(start, end - 1)).LineNumber + 1;
            units.Add(new(id, container, node is BaseTypeDeclarationSyntax ? "class" : "function", name, firstLine, lastLine, code[start..end]));
            ids.Add(node, id);
        }
        return units;
    }

    private const string PythonScript = """
        import ast, json, sys
        try:
            source = json.load(sys.stdin)["code"]
            tree = ast.parse(source)
            units = []
            def visit(node, parent):
                if isinstance(node, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
                    if len(units) >= 999:
                        raise ValueError("units")
                    ident = "u" + str(len(units) + 1)
                    start = min([node.lineno] + [d.lineno for d in node.decorator_list])
                    units.append({"Id": ident, "ContainerId": parent, "Kind": "class" if isinstance(node, ast.ClassDef) else "function", "Name": node.name, "StartLine": start, "EndLine": node.end_lineno})
                    parent = ident
                for child in ast.iter_child_nodes(node):
                    visit(child, parent)
            visit(tree, "file")
            print(json.dumps(units, ensure_ascii=True))
        except (SyntaxError, ValueError, RecursionError):
            sys.exit(2)
        """;

    private static async Task<List<SourceCodeUnit>> ParsePythonAsync(string code, string path, string toolsDirectory, CancellationToken token)
    {
        var executable = await new PythonParserRuntime(toolsDirectory).EnsureAsync(token).ConfigureAwait(false);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-I", "-S", "-X", "utf8", "-c", PythonScript }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Python解析を開始できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { code }).AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new PipelineValidationException("code_syntax", "Pythonの構文を解析できません。原文を修正するかテキスト処理を選択してください。");
            var definitions = JsonSerializer.Deserialize<List<PythonUnit>>(await output.ConfigureAwait(false)) ?? throw new InvalidDataException("Python解析の応答が不正です。");
            var offsets = new List<int> { 0 };
            for (var i = 0; i < code.Length; i++) if (code[i] == '\n') offsets.Add(i + 1);
            var units = new List<SourceCodeUnit> { new("file", null, "file", Path.GetFileName(path), 1, offsets.Count, code) };
            foreach (var unit in definitions)
            {
                if (unit.StartLine < 1 || unit.EndLine < unit.StartLine || unit.EndLine > offsets.Count || units.Any(u => u.Id == unit.Id) ||
                    !units.Any(u => u.Id == unit.ContainerId) || unit.Kind is not ("class" or "function")) throw new InvalidDataException("Pythonコード範囲が不正です。");
                units.Add(new(unit.Id, unit.ContainerId, unit.Kind, unit.Name, unit.StartLine, unit.EndLine,
                    code[offsets[unit.StartLine - 1]..(unit.EndLine < offsets.Count ? offsets[unit.EndLine] : code.Length)]));
            }
            return units;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
    }
    private sealed record PythonUnit(string Id, string ContainerId, string Kind, string Name, int StartLine, int EndLine);
}
