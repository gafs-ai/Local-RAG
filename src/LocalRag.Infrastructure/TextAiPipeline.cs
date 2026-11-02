using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed class LemonadeTextAi(AppSettings settings, HttpMessageHandler? handler = null) : ITextRagAi, IDisposable
{
    private readonly LemonadeClient _client = new(settings.Ai, handler);
    public Task<string> CompleteAsync(string system, string input, CancellationToken token) =>
        _client.CompleteAsync(settings.Ai.ChatModel, system + "\nReturn exactly one JSON object matching the requested schema. No explanations or Markdown. /no_think",
            input, settings.TextPipeline.MaxOutputTokens, token, jsonObject: true);
    public async Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token)
    {
        var result = await _client.EmbedAsync(settings.Ai.EmbeddingModel, inputs, token).ConfigureAwait(false);
        return new(result.Dimension, result.Vectors);
    }
    public void Dispose() => _client.Dispose();
}

public sealed class TextAiPipeline(ITextRagAi ai, TextRagSettings settings)
{
    private static readonly JsonSerializerOptions InputJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonDocument ParseResponse(string output)
    {
        var text = output.Trim();
        // Some local models wrap otherwise valid JSON despite response_format. Accept only one complete fence.
        if (text.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) || text.StartsWith("```json\r\n", StringComparison.OrdinalIgnoreCase) || text.StartsWith("```\n", StringComparison.Ordinal))
        {
            if (!text.EndsWith("```", StringComparison.Ordinal)) throw new PipelineValidationException("response_schema", "JSON応答が完結していません。");
            text = text[(text.IndexOf('\n') + 1)..^3].Trim();
        }
        var parsed = JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(parsed.RootElement))
        { parsed.Dispose(); throw new PipelineValidationException("response_schema", "JSON応答のオブジェクトが不正です。"); }
        return parsed;
    }

    private static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != element.EnumerateObject().Count()
            || element.EnumerateObject().Any(p => HasDuplicateProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        _ => false
    };

    private static JsonElement Property(JsonElement root, string name, JsonValueKind kind)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new PipelineValidationException("response_schema", "応答の必須項目または型が不正です。");
        return value;
    }

    internal static string StringProperty(JsonElement root, string name) => Property(root, name, JsonValueKind.String).GetString()!;

    private static int ParagraphId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        // A decimal integer string has exactly the same meaning as its JSON numeric representation.
        // Never extract digits from prose, round a fraction, or clamp an unknown ID into the input range.
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 10 } text &&
            text.All(char.IsAsciiDigit) && int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
        throw new PipelineValidationException("response_schema", "段落IDは整数で指定してください。");
    }

    public async Task<T> JsonAsync<T>(string prompt, object input, Func<JsonElement, T> validate, CancellationToken token)
    {
        // One correction request, with a trusted, specific reason. Never echo provider text as instructions.
        string? feedback = null;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var instruction = feedback is null ? prompt : prompt + "\nVALIDATION ERROR: " + feedback +
                    "\nGenerate a corrected complete JSON object from the original input. Do not change source text. No prose or Markdown.";
                var output = await ai.CompleteAsync(instruction, JsonSerializer.Serialize(input, InputJson), token).ConfigureAwait(false);
                using var json = ParseResponse(output);
                try { return validate(json.RootElement); }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
                {
                    // Schema access errors in a validator are repairable; provider/configuration errors are not.
                    throw new PipelineValidationException("response_schema", "モデル応答の必須項目または型が不正です。");
                }
            }
            catch (Exception ex) when (attempt == 0 && CorrectionFeedback(ex) is not null)
            {
                feedback = CorrectionFeedback(ex);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    private static string? CorrectionFeedback(Exception error) => error switch
    {
        JsonException => "The response was not valid JSON. Return exactly one syntactically complete JSON object.",
        PipelineValidationException validation => validation.Code switch
        {
            "response_schema" => "The response has missing keys, wrong types, or duplicate keys. Use the requested schema: arrays for chunks/remove, integer paragraph IDs for start/end/remove entries, and strings for category/decision. Each property must occur exactly once.",
            "category_unknown" => "The category is not one of the supplied category IDs. Select exactly one ID from categories; do not invent or rename it.",
            "format_ids" => "The remove list contains an ID absent from the supplied paragraphs. Use only existing paragraph IDs; do not renumber paragraphs.",
            "format_destructive" => "The removal would discard all paragraphs or more than half the text. Remove only standalone noise and preserve meaningful text. If unsure, return an empty remove list.",
            "chunk_order" => "Chunk starts skip, duplicate or reverse input paragraphs. Cover the supplied paragraphs in input order, exactly once, with no gaps or overlaps.",
            "chunk_end" => "A chunk end does not identify an existing paragraph at or after its start. Use only the actual paragraph IDs supplied with this input.",
            "chunk_size" => "A chunk exceeds maxCharacters, including two characters between paragraphs. Choose smaller paragraph groups; never truncate text.",
            "chunk_coverage" => "Some paragraphs are not covered, or chunks is empty. Include every supplied paragraph exactly once.",
            "decision_invalid" => "decision must be exactly allow, withhold, or pending. Reassess the original fragment under the inspection policy; use pending if uncertain.",
            "output_incomplete" => "The output did not finish normally. Return a compact complete JSON object within the output budget.",
            _ => null // Input limits, connectivity and unknown failures cannot be fixed by regenerating JSON.
        },
        _ => null
    };

    public Task<string> ClassifyAsync(string original, CancellationToken token)
    {
        if (!settings.ClassifyEnabled) return Task.FromResult(settings.DefaultCategory);
        return JsonAsync(settings.ClassifyPrompt, new { categories = settings.Categories.Select(c => new { c.Id, c.Description }), document = original },
            root =>
            {
                var id = StringProperty(root, "category");
                return settings.Categories.Any(c => c.Id == id) ? id : throw new PipelineValidationException("category_unknown", "分類応答のIDが不正です。");
            }, token);
    }

    public async Task<List<TextParagraph>> FormatAsync(List<TextParagraph> paragraphs, TextCategory category, CancellationToken token)
    {
        if (!(category.FormatEnabled ?? settings.FormatEnabled)) return paragraphs;
        return await JsonAsync(category.FormatPrompt ?? settings.FormatPrompt, new { category = category.Id, paragraphs }, root =>
        {
            // Removal is a set operation: duplicate entries do not change its meaning.
            var remove = Property(root, "remove", JsonValueKind.Array).EnumerateArray().Select(ParagraphId).Distinct().ToArray();
            if (remove.Any(id => !paragraphs.Any(p => p.Id == id)))
                throw new PipelineValidationException("format_ids", "整形対象の段落IDが不正です。");
            var remaining = paragraphs.Where(p => !remove.Contains(p.Id)).ToList();
            // Preserve original separately, and hold unusually destructive formatting for user review.
            if (remaining.Count == 0 || remaining.Sum(p => p.Text.Length) < paragraphs.Sum(p => p.Text.Length) * 0.5)
                throw new PipelineValidationException("format_destructive", "整形による削除量が大きいため保留しました。");
            return remaining;
        }, token).ConfigureAwait(false);
    }

    public async Task<List<TextChunk>> ChunkAsync(List<TextParagraph> paragraphs, TextCategory category, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (paragraphs.Count == 0 || paragraphs.Any(p => string.IsNullOrEmpty(p.Text) || p.Text.Length > settings.MaxChunkCharacters))
            throw new PipelineValidationException("chunk_size", "段落が空、または文字数上限を超えています。");
        // Under the paragraph-boundary/complete-coverage contract, a single paragraph has one answer.
        if (paragraphs.Count == 1) return [new(paragraphs[0].Id, paragraphs[0].Id, paragraphs[0].Text)];
        if (!(category.ChunkEnabled ?? settings.ChunkEnabled))
        {
            var text = string.Join("\n\n", paragraphs.Select(p => p.Text));
            if (text.Length > settings.MaxChunkCharacters) throw new InvalidDataException("分割OFFの本文がチャンク上限を超えています。");
            return [new(paragraphs[0].Id, paragraphs[^1].Id, text)];
        }
        var configuredPrompt = category.ChunkPrompt ?? settings.ChunkPrompt;
        // Upgrade only the exact former built-in prompt. Keep user-authored prompts intact.
        if (configuredPrompt == TextRagSettings.LegacyChunkPrompt) configuredPrompt = TextRagSettings.DefaultChunkPrompt;
        var example = new List<object>();
        var start = paragraphs[0].Id;
        var end = start;
        var length = 0;
        foreach (var paragraph in paragraphs)
        {
            if (length > 0 && length + 2 + paragraph.Text.Length > settings.MaxChunkCharacters)
            {
                example.Add(new { start, end });
                start = paragraph.Id;
                length = 0;
            }
            length += (length == 0 ? 0 : 2) + paragraph.Text.Length;
            end = paragraph.Id;
        }
        example.Add(new { start, end });
        var prompt = configuredPrompt +
            $"\nFor THIS input, the only valid paragraph IDs are [{string.Join(',', paragraphs.Select(p => p.Id))}]." +
            $" The first chunk must start at {paragraphs[0].Id}; the last must end at {paragraphs[^1].Id}." +
            " Use these actual IDs even when they differ from example IDs in the instructions." +
            "\nA complete valid response example for THIS input is: " + JsonSerializer.Serialize(new { chunks = example }) +
            "\nYou may choose different boundaries to follow the category instructions, but all IDs, coverage and length constraints still apply.";
        return await JsonAsync(prompt,
            new { category = category.Id, maxCharacters = settings.MaxChunkCharacters, paragraphs },
            root => ValidateChunks(root, paragraphs, settings.MaxChunkCharacters), token).ConfigureAwait(false);
    }

    public static List<TextChunk> ValidateChunks(JsonElement root, IReadOnlyList<TextParagraph> paragraphs, int limit)
    {
        var chunks = new List<TextChunk>();
        var next = 0;
        foreach (var range in Property(root, "chunks", JsonValueKind.Array).EnumerateArray())
        {
            if (range.ValueKind != JsonValueKind.Object || !range.TryGetProperty("start", out var startValue) || !range.TryGetProperty("end", out var endValue))
                throw new PipelineValidationException("response_schema", "分割範囲の必須項目がありません。");
            var start = ParagraphId(startValue);
            var end = ParagraphId(endValue);
            if (next >= paragraphs.Count || start != paragraphs[next].Id) throw new PipelineValidationException("chunk_order", "チャンク範囲に欠落・重複・逆転があります。");
            var last = next;
            while (last < paragraphs.Count && paragraphs[last].Id != end) last++;
            if (last >= paragraphs.Count) throw new PipelineValidationException("chunk_end", "チャンクの終端が不正です。");
            var text = string.Join("\n\n", paragraphs.Skip(next).Take(last - next + 1).Select(p => p.Text));
            if (text.Length > limit) throw new PipelineValidationException("chunk_size", "チャンクが上限を超えています。");
            chunks.Add(new(start, end, text));
            next = last + 1;
        }
        if (next != paragraphs.Count || chunks.Count == 0) throw new PipelineValidationException("chunk_coverage", "未分割の段落があります。");
        return chunks;
    }
}

public sealed class TextConfidentialityClassifier(ITextRagAi ai, TextRagSettings settings) : IConfidentialityClassifier
{
    public async Task<DisclosureDecision> InspectAsync(string original, string candidate, CancellationToken cancellationToken)
    {
        var pipeline = new TextAiPipeline(ai, settings);
        foreach (var content in new[] { original, candidate })
        {
            for (var offset = 0; offset < content.Length; offset += 500)
            {
                // Overlap helps preserve a marking straddling a boundary; every character is inspected.
                var fragment = content.Substring(offset, Math.Min(600, content.Length - offset));
                var decision = await pipeline.JsonAsync(settings.ConfidentialityPrompt, new { fragment }, root =>
                    TextAiPipeline.StringProperty(root, "decision") switch
                    {
                        "allow" => DisclosureDecision.Allowed,
                        "withhold" => DisclosureDecision.Withheld,
                        "pending" => DisclosureDecision.Pending,
                        _ => throw new PipelineValidationException("decision_invalid", "機密検査の判定値が不正です。")
                    }, cancellationToken).ConfigureAwait(false);
                if (decision != DisclosureDecision.Allowed) return decision;
            }
        }
        return DisclosureDecision.Allowed;
    }
}
