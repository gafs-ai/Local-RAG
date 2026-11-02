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
            if (!text.EndsWith("```", StringComparison.Ordinal)) throw new InvalidDataException("JSON応答が完結していません。");
            text = text[(text.IndexOf('\n') + 1)..^3].Trim();
        }
        var parsed = JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            parsed.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parsed.RootElement.EnumerateObject().Count())
        { parsed.Dispose(); throw new InvalidDataException("JSON応答のオブジェクトが不正です。"); }
        return parsed;
    }
    public async Task<T> JsonAsync<T>(string prompt, object input, Func<JsonElement, T> validate, CancellationToken token)
    {
        // One retry for a malformed structured response. Do not echo arbitrary model output back as instructions.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var instruction = attempt == 0 ? prompt : prompt + "\nThe last response failed validation. Use exactly the requested JSON object schema. Check IDs against the input. No prose or Markdown.";
                var output = await ai.CompleteAsync(instruction, JsonSerializer.Serialize(input, InputJson), token).ConfigureAwait(false);
                using var json = ParseResponse(output);
                return validate(json.RootElement);
            }
            catch (Exception ex) when (attempt == 0 && ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { await Task.Delay(200, token).ConfigureAwait(false); }
        }
    }

    public Task<string> ClassifyAsync(string original, CancellationToken token)
    {
        if (!settings.ClassifyEnabled) return Task.FromResult(settings.DefaultCategory);
        return JsonAsync(settings.ClassifyPrompt, new { categories = settings.Categories.Select(c => new { c.Id, c.Description }), document = original },
            root =>
            {
                var id = root.GetProperty("category").GetString();
                return settings.Categories.Any(c => c.Id == id) ? id! : throw new InvalidDataException("分類応答のIDが不正です。");
            }, token);
    }

    public async Task<List<TextParagraph>> FormatAsync(List<TextParagraph> paragraphs, TextCategory category, CancellationToken token)
    {
        if (!(category.FormatEnabled ?? settings.FormatEnabled)) return paragraphs;
        return await JsonAsync(category.FormatPrompt ?? settings.FormatPrompt, new { category = category.Id, paragraphs }, root =>
        {
            var remove = root.GetProperty("remove").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (remove.Distinct().Count() != remove.Length || remove.Any(id => !paragraphs.Any(p => p.Id == id)))
                throw new InvalidDataException("整形対象の段落IDが不正です。");
            var remaining = paragraphs.Where(p => !remove.Contains(p.Id)).ToList();
            // Preserve original separately, and hold unusually destructive formatting for user review.
            if (remaining.Count == 0 || remaining.Sum(p => p.Text.Length) < paragraphs.Sum(p => p.Text.Length) * 0.5)
                throw new InvalidDataException("整形による削除量が大きいため保留しました。");
            return remaining;
        }, token).ConfigureAwait(false);
    }

    public async Task<List<TextChunk>> ChunkAsync(List<TextParagraph> paragraphs, TextCategory category, CancellationToken token)
    {
        if (!(category.ChunkEnabled ?? settings.ChunkEnabled))
        {
            var text = string.Join("\n\n", paragraphs.Select(p => p.Text));
            if (text.Length > settings.MaxChunkCharacters) throw new InvalidDataException("分割OFFの本文がチャンク上限を超えています。");
            return [new(paragraphs[0].Id, paragraphs[^1].Id, text)];
        }
        var prompt = (category.ChunkPrompt ?? settings.ChunkPrompt) +
            $"\nFor THIS input, the only valid paragraph IDs are [{string.Join(',', paragraphs.Select(p => p.Id))}]." +
            $" The first chunk must start at {paragraphs[0].Id}; the last must end at {paragraphs[^1].Id}." +
            " Use these actual IDs even when they differ from example IDs in the instructions.";
        return await JsonAsync(prompt,
            new { category = category.Id, maxCharacters = settings.MaxChunkCharacters, paragraphs },
            root => ValidateChunks(root, paragraphs, settings.MaxChunkCharacters), token).ConfigureAwait(false);
    }

    public static List<TextChunk> ValidateChunks(JsonElement root, IReadOnlyList<TextParagraph> paragraphs, int limit)
    {
        var chunks = new List<TextChunk>();
        var next = 0;
        foreach (var range in root.GetProperty("chunks").EnumerateArray())
        {
            var start = range.GetProperty("start").GetInt32();
            var end = range.GetProperty("end").GetInt32();
            if (next >= paragraphs.Count || start != paragraphs[next].Id) throw new InvalidDataException("チャンク範囲に欠落・重複・逆転があります。");
            var last = next;
            while (last < paragraphs.Count && paragraphs[last].Id != end) last++;
            if (last >= paragraphs.Count) throw new InvalidDataException("チャンクの終端が不正です。");
            var text = string.Join("\n\n", paragraphs.Skip(next).Take(last - next + 1).Select(p => p.Text));
            if (text.Length > limit) throw new InvalidDataException("チャンクが上限を超えています。");
            chunks.Add(new(start, end, text));
            next = last + 1;
        }
        if (next != paragraphs.Count || chunks.Count == 0) throw new InvalidDataException("未分割の段落があります。");
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
                    root.GetProperty("decision").GetString() switch
                    {
                        "allow" => DisclosureDecision.Allowed,
                        "withhold" => DisclosureDecision.Withheld,
                        _ => DisclosureDecision.Pending
                    }, cancellationToken).ConfigureAwait(false);
                if (decision != DisclosureDecision.Allowed) return decision;
            }
        }
        return DisclosureDecision.Allowed;
    }
}
