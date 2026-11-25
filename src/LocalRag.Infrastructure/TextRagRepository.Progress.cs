using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    // Store high-frequency progress in separate records. Updating even one field of the
    // large text_document record still appends its whole value to SurrealKV's value log.
    // A full Save/Publish folds these records back into the authoritative document and
    // deletes them in the same transaction. Legacy snapshots need no conversion to resume.
    private const string WriteWorkProgress = " UPSERT type::record('text_work_progress', $work_key) SET document_key=$document, data_source_name=$source, revision=$revision, kind=$work_kind, slot=$work_slot, part_no=$work_part, payload=$work_payload RETURN NONE;";
    private sealed record VectorProgress(int Count, int Dimension, string Space);
    private sealed record CodeProgress(string Id, int Keep, List<string> Descriptions, int Characters, bool Skipped, bool Completed, bool Fallback);
    private sealed record ParentProgress(ParentEmbeddingProgress Value, int Keep, bool SameInput, bool InputIsParent, bool EmbedsParent);
    private sealed record WordProgress(int Start, int Next, int BlockStart, List<string> Blocks, List<string> Notices);
    private sealed record WorkRow(string document_key, string revision, string kind, int slot, int part_no, string payload);
    private sealed record WorkSnapshots(string[] documents, WorkRow[] progress);

    private void AddProgressParameters<T>(Dictionary<string, object?> parameters, TextDocumentState state, string kind, int slot, T value, int part = 0)
    {
        if (state.DataSourceName != DataSourceName || state.Status != "processing") throw new InvalidDataException("処理中の文書の進捗のみ保存できます。");
        parameters["work_key"] = TextRagSettings.Hash(JsonSerializer.Serialize(new { DataSourceName, state.Key, state.RevisionId, kind, slot, part }));
        parameters["work_kind"] = kind;
        parameters["work_slot"] = slot;
        parameters["work_part"] = part;
        parameters["work_payload"] = JsonSerializer.Serialize(value);
    }

    private async Task WriteProgressAsync<T>(TextDocumentState state, string kind, int slot, T delta, CancellationToken token, int part = 0)
    {
        var parameters = new Dictionary<string, object?> { ["document"] = state.Key, ["source"] = DataSourceName, ["revision"] = state.RevisionId };
        AddProgressParameters(parameters, state, kind, slot, delta, part);
        (await _db.RawQuery(WriteWorkProgress, parameters, token).ConfigureAwait(false)).EnsureAllOks();
    }

    internal Task SaveCodeProgressAsync(TextDocumentState state, int position, int part, SourceCodeUnit previous, SourceCodeUnit unit, CancellationToken token)
    {
        var keep = unit.Kind == "function" ? previous.Descriptions.Count : 0;
        return WriteProgressAsync(state, "code", position, new CodeProgress(unit.Id, keep,
            unit.Descriptions.GetRange(keep, unit.Descriptions.Count - keep), unit.DescribedCharacters,
            unit.DescriptionSkipped, unit.DescriptionCompleted, unit.DescriptionFallbackUsed), token, part);
    }

    internal Task SaveParentProgressAsync(TextDocumentState state, int position, int part, TextParentChunk parent,
        ParentEmbeddingProgress previous, ParentEmbeddingProgress value, CancellationToken token)
    {
        var sameInput = value.ReductionInput == previous.ReductionInput;
        var inputIsParent = !sameInput && value.ReductionInput == parent.Text;
        var embedsParent = value.Kind == "parent_text" && value.EmbeddingText == parent.Text;
        var keep = previous.Round == value.Round && value.Reduced.Count >= previous.Reduced.Count ? previous.Reduced.Count : 0;
        return WriteProgressAsync(state, "parent", position, new ParentProgress(value with
            { ReductionInput = sameInput || inputIsParent ? null : value.ReductionInput, EmbeddingText = embedsParent ? null : value.EmbeddingText,
                Reduced = value.Reduced.GetRange(keep, value.Reduced.Count - keep) }, keep, sameInput, inputIsParent, embedsParent), token, part);
    }

    internal Task SaveWordProgressAsync(TextDocumentState state, WordRepairProgress? previous, WordRepairProgress next, CancellationToken token)
    {
        var start = previous?.NextBlock ?? 0;
        var blockStart = previous?.Blocks.Count ?? 0;
        return WriteProgressAsync(state, "word", start, new WordProgress(start, next.NextBlock, blockStart,
            next.Blocks.GetRange(blockStart, next.Blocks.Count - blockStart), next.Notices), token);
    }

    private async Task<TextDocumentState[]> ReadWorkSnapshotsAsync(string? key, CancellationToken token)
    {
        // One read transaction: never combine an old snapshot with new deltas during publish or a phase change.
        var documents = key is null ? "text_document" : "type::record('text_document',$key)";
        var filter = key is null ? "" : " AND document_key=$key";
        var response = await _db.RawQuery($"RETURN {{ documents:(SELECT VALUE payload FROM {documents} WHERE data_source_name=$source), progress:(SELECT document_key, revision, kind, slot, part_no, payload FROM text_work_progress WHERE data_source_name=$source{filter}) }};",
            new Dictionary<string, object?> { ["source"] = DataSourceName, ["key"] = key }, token).ConfigureAwait(false);
        response.EnsureAllOks();
        var snapshots = response.GetValue<WorkSnapshots>(0) ?? throw new InvalidDataException("文書状態を取得できません。");
        var byRevision = snapshots.progress.ToLookup(row => (row.document_key, row.revision));
        return snapshots.documents.Select(json =>
        {
            var state = JsonSerializer.Deserialize<TextDocumentState>(json) ?? throw new InvalidDataException("文書状態が不正です。");
            return state.Status == "processing" ? Restore(state, byRevision[(state.Key, state.RevisionId)]) : state;
        }).ToArray();
    }

    private static TextDocumentState Restore(TextDocumentState state, IEnumerable<WorkRow> rows)
    {
        static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("処理進捗が空です。");
        static void Require(bool condition) { if (!condition) throw new InvalidDataException("文書と処理進捗が一致しません。"); }
        var codeChanged = false;
        var wordChanged = false;
        var notices = state.ProcessingNotices.ToHashSet(StringComparer.Ordinal);
        static List<string> Append(List<string> values, int keep, List<string> tail)
        {
            Require(keep >= 0 && keep <= values.Count);
            if (keep < values.Count) values.RemoveRange(keep, values.Count - keep);
            values.AddRange(tail);
            return values;
        }
        foreach (var row in rows.OrderBy(r => r.slot).ThenBy(r => r.part_no))
        {
            switch (row.kind)
            {
                case "embedding":
                    var vector = Read<VectorProgress>(row.payload);
                    Require(vector.Count >= state.EmbeddedChunks && vector.Count <= state.Chunks?.Count
                        && vector.Dimension is >= 1 and <= 8192 && (state.SpaceId is null || state.SpaceId == vector.Space));
                    state = state with { EmbeddedChunks = vector.Count, Dimension = vector.Dimension, SpaceId = vector.Space };
                    break;
                case "code":
                    var code = Read<CodeProgress>(row.payload);
                    Require(row.slot >= 0 && row.slot < state.CodeUnits?.Count && state.CodeUnits[row.slot].Id == code.Id);
                    var unit = state.CodeUnits![row.slot];
                    state.CodeUnits[row.slot] = unit with { Descriptions = Append(unit.Descriptions, code.Keep, code.Descriptions), DescribedCharacters = code.Characters,
                        DescriptionSkipped = code.Skipped, DescriptionCompleted = code.Completed, DescriptionFallbackUsed = code.Fallback };
                    if (code.Fallback) notices.Add("code_description_structural");
                    if (code.Skipped) notices.Add("code_" + unit.Kind + "_description_skipped");
                    codeChanged = true;
                    break;
                case "parent":
                    var data = Read<ParentProgress>(row.payload);
                    Require(row.slot >= 0 && row.slot < state.ParentEmbeddings.Count && state.ParentEmbeddings[row.slot].ParentId == data.Value.ParentId);
                    var parent = state.Parents?.FirstOrDefault(p => p.Id == data.Value.ParentId) ?? throw new InvalidDataException("親チャンクがありません。");
                    var previous = state.ParentEmbeddings[row.slot];
                    state.ParentEmbeddings[row.slot] = data.Value with { ReductionInput = data.SameInput ? previous.ReductionInput : data.InputIsParent ? parent.Text : data.Value.ReductionInput,
                        Reduced = Append(previous.Reduced, data.Keep, data.Value.Reduced),
                        EmbeddingText = data.EmbedsParent ? parent.Text : data.Value.EmbeddingText };
                    if (data.Value.Skipped) notices.Add("parent_embedding_skipped");
                    break;
                case "word":
                    var word = Read<WordProgress>(row.payload);
                    var output = state.WordRepair?.Blocks ?? [];
                    Require(word.Start == (state.WordRepair?.NextBlock ?? 0) && word.Next > word.Start && word.BlockStart == output.Count);
                    output.AddRange(word.Blocks);
                    state = state with { WordRepair = new(word.Next, output, word.Notices) };
                    wordChanged = true;
                    break;
                default: throw new InvalidDataException("処理進捗の種類が不正です。");
            }
        }
        if (state.Processing is { } processing)
        {
            // Stage inputs/recipes are unchanged since the snapshot. Only the partial output grew.
            if (codeChanged && processing.Stages.TryGetValue("code-description", out var code))
                processing.Stages["code-description"] = code with { OutputHash = ProcessingPlan.Hash(state.CodeUnits) };
            if (wordChanged && processing.Stages.TryGetValue("word-repair", out var word))
                processing.Stages["word-repair"] = word with { OutputHash = ProcessingPlan.Hash(state.WordRepair) };
        }
        return state with { ProcessingNotices = notices.ToList() };
    }
}
