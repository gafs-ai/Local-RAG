using System.Diagnostics;
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Diagnostics;

/// <summary>Offline, explicitly supplied public/synthetic samples. Never reads an existing index.</summary>
internal static class CodeDescriptionEvaluation
{
    private sealed record Sample(string Name, string Language, string Kind, string Code);
    private sealed record Variant(string Name, string Prompt);
    private sealed record Manifest(Sample[] Samples, Variant[] Variants);

    public static async Task<int> RunAsync(string manifestPath, string outputPath, string settingsDirectory, CancellationToken token)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, token))!;
        if (manifest.Samples.Length is < 1 or > 12 || manifest.Variants.Length is < 1 or > 4)
            throw new ArgumentException("Use 1–12 explicit samples and 1–4 prompt variants.");
        if (File.Exists(outputPath)) throw new IOException("Choose a new evaluation output file.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var settings = await new SettingsStore(settingsDirectory).LoadAsync(token);
        using var ai = new LemonadeTextAi(settings);
        var recording = new RecordingAi(ai);
        var pipeline = new TextAiPipeline(recording, settings.TextPipeline);
        var outputTokens = Math.Min(settings.TextPipeline.MaxOutputTokens, 512);
        foreach (var sample in manifest.Samples)
        foreach (var variant in manifest.Variants)
        {
            var budget = await pipeline.InputBudgetAsync(variant.Prompt, token, outputTokens);
            object Input(string part) => new { language = sample.Language, sample.Kind,
                name = sample.Name[..Math.Min(200, sample.Name.Length)], fragment = part };
            var offset = 0;
            foreach (var part in TextWindows.Split(sample.Code, s => TextAiPipeline.InputBytes(Input(s)) <= budget))
            {
                recording.Attempts.Clear();
                var watch = Stopwatch.StartNew();
                string? description = null;
                string? error = null;
                try
                {
                    description = await pipeline.JsonAsync(variant.Prompt, Input(part), root =>
                    {
                        var value = TextAiPipeline.StringProperty(root, "description");
                        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000)
                            throw new PipelineValidationException("response_schema", "Invalid description.");
                        return value;
                    }, token, outputTokens);
                }
                catch (Exception ex) when (ex is JsonException or PipelineValidationException)
                { error = PipelineErrors.DetailCode(ex); }
                // Stored inputs/outputs are evidence for human assessment, not an automated semantic score.
                await File.AppendAllTextAsync(outputPath, JsonSerializer.Serialize(new
                {
                    sample.Name, Variant = variant.Name, variant.Prompt, settings.Ai.ChatModel,
                    Offset = offset, Fragment = part, Description = description, Error = error,
                    OutputTokens = outputTokens, Seconds = watch.Elapsed.TotalSeconds,
                    Attempts = recording.Attempts.ToArray()
                }) + "\n", token);
                Console.WriteLine($"{sample.Name} / {variant.Name}: {(error ?? "valid JSON")} ({watch.Elapsed.TotalSeconds:F1}s)");
                offset += part.Length;
            }
        }
        return 0;
    }

    private sealed record Attempt(string? Output, string? Error);
    private sealed class RecordingAi(ITextRagAi inner) : ITextRagAi
    {
        public List<Attempt> Attempts { get; } = [];
        public Task<int> ChatInputBudgetAsync(string system, int outputTokens, CancellationToken token) =>
            inner.ChatInputBudgetAsync(system, outputTokens, token);
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => inner.EmbedAsync(inputs, token);
        public Task<string> CompleteAsync(string system, string input, CancellationToken token) => CompleteBoundedAsync(system, input, 512, token);
        public async Task<string> CompleteBoundedAsync(string system, string input, int outputTokens, CancellationToken token)
        {
            try
            {
                var output = await inner.CompleteBoundedAsync(system, input, outputTokens, token);
                Attempts.Add(new(output, null));
                return output;
            }
            catch (Exception ex)
            {
                Attempts.Add(new(null, PipelineErrors.DetailCode(ex)));
                throw;
            }
        }
    }
}
