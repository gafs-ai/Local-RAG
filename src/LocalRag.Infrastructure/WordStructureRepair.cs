using System.Text.Json;
using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public static class WordStructureRepair
{
    public static async Task<WordRepairProgress> RunAsync(TextAiPipeline pipeline, TextRagSettings settings, IReadOnlyList<WordBlock> blocks,
        WordRepairProgress? previous, Func<WordRepairProgress, Task> checkpoint, CancellationToken token)
    {
        var output = previous?.Blocks.ToList() ?? [];
        var notices = previous?.Notices.ToHashSet(StringComparer.Ordinal) ?? [];
        var offset = previous?.NextBlock ?? 0;
        if (!settings.Word.RepairWithLlm)
        {
            output.AddRange(blocks.Skip(offset).Select(WordDocuments.Render));
            var unchanged = new WordRepairProgress(blocks.Count, output, notices.ToList());
            await checkpoint(unchanged).ConfigureAwait(false);
            return unchanged;
        }
        var budget = settings.Word.RepairWithLlm ? await pipeline.InputBudgetAsync(settings.Word.RepairPrompt, token, 256).ConfigureAwait(false) : 0;
        object Input(IEnumerable<WordBlock> batch) => new { paragraphs = batch.Select(b => new { id = b.Id, text = b.Text,
            headingLevel = b.HeadingLevel, bold = b.Bold, protect = b.Protected }), responseExample = new { headings = Array.Empty<object>(), join = Array.Empty<object>() } };
        while (offset < blocks.Count)
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<WordBlock>();
            for (var i = offset; i < blocks.Count && batch.Count < 6; i++)
            {
                if (TextAiPipeline.InputBytes(Input(batch.Append(blocks[i]))) > budget) break;
                batch.Add(blocks[i]);
            }
            if (!settings.Word.RepairWithLlm || batch.Count == 0)
            {
                output.Add(WordDocuments.Render(blocks[offset++]));
                if (settings.Word.RepairWithLlm) notices.Add("word_repair_preserved_long");
            }
            else
            {
                try
                {
                    var result = await pipeline.JsonAsync(settings.Word.RepairPrompt, Input(batch), root => Apply(root, batch), token, 256).ConfigureAwait(false);
                    output.AddRange(result);
                }
                catch (Exception ex) when (ex is JsonException || ex is PipelineValidationException v &&
                    v.Code is "response_schema" or "output_incomplete" or "chat_context" or "context_capacity")
                {
                    notices.Add("word_repair_preserved_invalid");
                    output.AddRange(batch.Select(WordDocuments.Render));
                }
                offset += batch.Count;
            }
            await checkpoint(new(offset, output.ToList(), notices.ToList())).ConfigureAwait(false);
        }
        return new(offset, output, notices.ToList());
    }

    internal static List<string> Apply(JsonElement root, IReadOnlyList<WordBlock> input)
    {
        void Require(bool value) { if (!value) throw new PipelineValidationException("response_schema", "Word構造補正のID・範囲・種類が不正です。"); }
        Require(root.TryGetProperty("headings", out var headings) && headings.ValueKind == JsonValueKind.Array &&
            root.TryGetProperty("join", out var _) && root.GetProperty("join").ValueKind == JsonValueKind.Array && root.EnumerateObject().Count() == 2);
        var blocks = input.ToArray();
        var changed = new HashSet<int>();
        int Number(JsonElement item, string name)
        {
            Require(item.TryGetProperty(name, out var property));
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)) return value;
            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value)) return value;
            throw new PipelineValidationException("response_schema", "整数が必要です。");
        }
        foreach (var heading in headings.EnumerateArray())
        {
            var id = Number(heading, "id"); var level = Number(heading, "level");
            var index = Array.FindIndex(blocks, b => b.Id == id);
            Require(index >= 0 && level is >= 1 and <= 6 && changed.Add(id));
            var block = blocks[index];
            Require(!block.Protected && block.Text.Length <= 160 && !block.Text.Contains('\n'));
            blocks[index] = block with { HeadingLevel = level };
        }
        var groups = new Dictionary<int, int>(); var used = new HashSet<int>();
        foreach (var join in root.GetProperty("join").EnumerateArray())
        {
            var first = Array.FindIndex(blocks, b => b.Id == Number(join, "start"));
            var last = Array.FindIndex(blocks, b => b.Id == Number(join, "end"));
            Require(first >= 0 && last > first && last - first <= 2);
            for (var i = first; i <= last; i++)
            {
                Require(!blocks[i].Protected && blocks[i].HeadingLevel == 0 && used.Add(i) &&
                    !Regex.IsMatch(blocks[i].Text, @"^(?:[-+*] |\d+[.)] |\|)"));
                if (i < last) Require(!Regex.IsMatch(blocks[i].Text, @"[。！？.!?：:;；]$"));
            }
            Require(groups.TryAdd(first, last));
        }
        var output = new List<string>();
        for (var i = 0; i < blocks.Length; i++)
        {
            if (groups.TryGetValue(i, out var end)) { output.Add(WordDocuments.JoinLines(blocks[i..(end + 1)].Select(b => b.Text))); i = end; }
            else output.Add(WordDocuments.Render(blocks[i]));
        }
        return output;
    }
}
