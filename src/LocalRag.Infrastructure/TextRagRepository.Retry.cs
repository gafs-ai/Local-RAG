using LocalRag.Core;
using System.Text.Json;

namespace LocalRag.Infrastructure;

public sealed partial class TextRagRepository
{
    // Small projection: periodic discovery must not read original documents or wait for the AI lane.
    public async Task<IReadOnlyDictionary<string, IngestionFailure>> FailuresAsync(CancellationToken token)
    {
        var result = await _db.RawQuery("SELECT VALUE encoding::json::encode({Path:Path, Failure:Failure}) FROM (SELECT VALUE encoding::json::decode(payload) FROM text_document WHERE data_source_name=$source) WHERE Status='failed';",
            new Dictionary<string, object?> { ["source"] = DataSourceName }, token).ConfigureAwait(false);
        result.EnsureAllOks();
        return (result.GetValue<string[]>(0) ?? []).Select(json => JsonSerializer.Deserialize<FailureRow>(json)!)
            .Where(r => r.Failure is not null)
            .ToDictionary(r => r.Path, r => r.Failure!, StringComparer.OrdinalIgnoreCase);
    }
    private sealed record FailureRow(string Path, IngestionFailure? Failure);
}
