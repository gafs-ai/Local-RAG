using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    public async Task<float[]?> ReusableVectorAsync(string space, string documentKey, string text, CancellationToken token)
    {
        var result = await _db.RawQuery($"SELECT payload, embedding FROM {Table(space)} WHERE document_key=$document AND input_hash=$input LIMIT 1;",
            new Dictionary<string, object?> { ["document"] = documentKey, ["input"] = TextRagSettings.Hash(text) }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        var match = ParseSearchVectors(result.GetValue<SearchVectorRow[]>(0) ?? []).SingleOrDefault();
        return match?.Chunk.Text == text && match.Chunk.DocumentKey == documentKey ? match.Embedding : null;
    }

    public async Task BackfillVectorHashesAsync(TextDocumentState state, CancellationToken token)
    {
        if (state.SpaceId is null || state.Chunks is null) return;
        foreach (var batch in Enumerable.Range(0, state.Chunks.Count).Chunk(128))
        {
            var statements = new List<string>();
            var parameters = new Dictionary<string, object?>();
            for (var i = 0; i < batch.Length; i++)
            {
                parameters["id" + i] = state.RevisionId + "_" + batch[i];
                parameters["hash" + i] = TextRagSettings.Hash(state.Chunks[batch[i]].Text);
                statements.Add($"UPDATE type::record('{Table(state.SpaceId)}', $id{i}) SET input_hash=$hash{i};");
            }
            (await _db.RawQuery(string.Join(" ", statements), parameters, token).ConfigureAwait(false)).EnsureAllOks();
        }
    }
}
