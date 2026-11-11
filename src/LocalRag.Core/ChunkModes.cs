using System.Text.Json.Serialization;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ChildChunkMode>))]
public enum ChildChunkMode { Inherit, Paragraphs, Mechanical, Llm }
[JsonConverter(typeof(JsonStringEnumConverter<ParentChunkMode>))]
public enum ParentChunkMode { Inherit, None, Structure, Llm }
[JsonConverter(typeof(JsonStringEnumConverter<ParentEmbeddingMode>))]
public enum ParentEmbeddingMode { None, TextWhenFits, TextOrSummary }
[JsonConverter(typeof(JsonStringEnumConverter<SourceCodeMode>))]
// Auto/Code are accepted only for migration of older settings.
public enum SourceCodeMode { Auto, Text, Code, CSharp, Python, TypeScript, JavaScript, Cpp, C, Rust, Java, Kotlin, Word, Pdf }

public sealed record SourceLanguage(SourceCodeMode Mode, string Name, string[] Extensions, string? Grammar = null);

public static class SourceLanguages
{
    public static IReadOnlyList<SourceLanguage> All { get; } = Array.AsReadOnly(new SourceLanguage[] {
        new(SourceCodeMode.Python, "Python", [".py"]), new(SourceCodeMode.CSharp, "C#", [".cs"]),
        new(SourceCodeMode.TypeScript, "TypeScript", [".ts", ".tsx", ".mts", ".cts"], "typescript"),
        new(SourceCodeMode.JavaScript, "JavaScript", [".js", ".jsx", ".mjs", ".cjs"], "javascript"),
        new(SourceCodeMode.Cpp, "C++", [".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx"], "cpp"),
        new(SourceCodeMode.C, "C", [".c", ".h"], "c"), new(SourceCodeMode.Rust, "Rust", [".rs"], "rust"),
        new(SourceCodeMode.Java, "Java", [".java"], "java"), new(SourceCodeMode.Kotlin, "Kotlin", [".kt", ".kts"], "kotlin") });
    public static SourceLanguage? Find(SourceCodeMode mode) => All.SingleOrDefault(l => l.Mode == mode);
    public static bool IsCode(SourceCodeMode mode) => Find(mode) is not null;
}
