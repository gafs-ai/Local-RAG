using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Explicitly configured OVMS/LM Studio endpoints, including user-approved remote servers.</summary>
public sealed class OpenAiModelClient : IAiModelClient
{
    private readonly AiEndpointSettings _endpoint;
    private readonly HttpClient _http;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _wireModels = new();
    private bool _nativeLmModels;

    public OpenAiModelClient(AiEndpointSettings endpoint, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        if (endpoint.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join("\n", errors));
        _endpoint = endpoint;
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = endpoint.GetBaseUri(), Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(apiKey)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private Uri LmApi(string path)
    {
        var address = _endpoint.GetBaseUri();
        // Preserve reverse-proxy prefixes and the configured authority; never follow server-supplied URLs.
        var prefix = address.AbsolutePath.EndsWith("/v1/", StringComparison.Ordinal) ? address.AbsolutePath[..^3] : address.AbsolutePath;
        return new UriBuilder(address) { Path = prefix + "api/v1/" + path }.Uri;
    }

    public async Task<IReadOnlyList<LemonadeModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint.Provider == AiProvider.LmStudio)
        {
            using var native = await _http.GetAsync(LmApi("models"), cancellationToken).ConfigureAwait(false);
            if (native.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NotImplemented))
            {
                native.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await native.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var models = new List<LemonadeModel>();
                foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
                {
                    var type = String(model, "type");
                    if (type is not ("llm" or "embedding")) continue;
                    var id = String(model, "key");
                    if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("モデルIDがありません。");
                    int? context = null;
                    _wireModels.TryRemove(id, out _);
                    if (model.TryGetProperty("loaded_instances", out var instances))
                        foreach (var instance in instances.EnumerateArray())
                        {
                            var current = instance.TryGetProperty("config", out var config) ? Positive(config, "context_length") : null;
                            if (current is not null) context = context is null ? current : Math.Min(current.Value, context.Value);
                            if (String(instance, "id") is { Length: > 0 } wire) _wireModels[id] = wire;
                        }
                    models.Add(new(id, "lmstudio", [type == "embedding" ? "embeddings" : "chat"], true, context));
                }
                _nativeLmModels = true;
                return Unique(models);
            }
        }
        _nativeLmModels = false;
        using var response = await _http.GetAsync("models", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return Unique(json.RootElement.GetProperty("data").EnumerateArray().Select(item =>
        {
            var id = String(item, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("モデルIDがありません。");
            var type = String(item, "type");
            string[] labels = type is "embedding" or "embeddings" ? ["embeddings"] : type is "llm" or "vlm" ? ["chat"] : ["chat", "embeddings"];
            return new LemonadeModel(id, _endpoint.Provider.ToString(), labels, true, Positive(item, "context_length"));
        }).ToList());
    }

    private static IReadOnlyList<LemonadeModel> Unique(List<LemonadeModel> models) => models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == models.Count
        ? models : throw new InvalidDataException("モデルIDが重複しています。");
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int? Positive(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number > 0 ? number : null;

    private async Task<int> ContextAsync(string model, string purpose, CancellationToken token)
    {
        var selected = (await GetModelsAsync(token).ConfigureAwait(false)).SingleOrDefault(x => x.Id == model && x.Labels.Contains(purpose))
            ?? throw new InvalidOperationException("選択した接続先でモデルを確認できません。モデル名と用途を確認してください。");
        var configured = purpose == "chat" ? _endpoint.ChatContextTokens : _endpoint.EmbeddingContextTokens;
        var actual = selected.ContextLength;
        if (actual is null && _endpoint.Provider == AiProvider.LmStudio && _nativeLmModels &&
            (configured == 0 || !_wireModels.ContainsKey(model)))
        {
            var load = new Dictionary<string, object> { ["model"] = model, ["echo_load_config"] = true };
            if (configured > 0) load["context_length"] = configured;
            using var response = await _http.PostAsJsonAsync(LmApi("models/load"), load, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("load_config", out var config)) actual = Positive(config, "context_length");
            if (String(json.RootElement, "instance_id") is { Length: > 0 } wire) _wireModels[model] = wire;
        }
        var limit = actual is > 0 ? configured > 0 ? Math.Min(actual.Value, configured) : actual.Value : configured;
        if (limit <= 0) throw new InvalidDataException("モデルの実行時入力上限を取得できません。接続先の入力上限を設定してください。");
        return limit;
    }

    public async Task<int> ChatInputBudgetAsync(string model, string system, int outputTokens, CancellationToken token) =>
        Math.Max(0, await ContextAsync(model, "chat", token).ConfigureAwait(false) - Encoding.UTF8.GetByteCount(system) - outputTokens - 256);
    public async Task<int> EmbeddingInputBudgetAsync(string model, CancellationToken token) =>
        Math.Max(0, EmbeddingInput.ContextLimit(model, await ContextAsync(model, "embeddings", token).ConfigureAwait(false)) - 256);

    public async Task<string> CompleteAsync(string model, string system, string input, int maxTokens = 128, CancellationToken cancellationToken = default, bool jsonObject = false)
    {
        var budget = await ChatInputBudgetAsync(model, system, maxTokens, cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(input) > budget) throw new PipelineValidationException("chat_context", "LLMの入力上限を超えるため保留しました。");
        var request = new Dictionary<string, object>
        {
            ["model"] = _wireModels.GetValueOrDefault(model, model), ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
            ["max_tokens"] = maxTokens, ["temperature"] = 0, ["stream"] = false
        };
        if (jsonObject && _endpoint.JsonOutput)
            request["response_format"] = new { type = "json_schema", json_schema = new { name = "pipeline_result", schema = new { type = "object" } } };
        using var response = await _http.PostAsJsonAsync("chat/completions", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var choice = json.RootElement.GetProperty("choices")[0];
        if (String(choice, "finish_reason") != "stop") throw new PipelineValidationException("output_incomplete", "モデル出力が正常に完了しませんでした。");
        return String(choice.GetProperty("message"), "content") ?? throw new PipelineValidationException("response_schema", "モデル応答が空です。");
    }

    public async Task<EmbeddingBatch> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("埋め込み対象がありません。");
        var budget = await EmbeddingInputBudgetAsync(model, cancellationToken).ConfigureAwait(false);
        if (inputs.Any(input => Encoding.UTF8.GetByteCount(input) > budget)) throw new PipelineValidationException("embedding_context", "Embedding入力がモデルの上限を超えるため保留しました。");
        using var response = await _http.PostAsJsonAsync("embeddings", new { model = _wireModels.GetValueOrDefault(model, model), input = inputs, encoding_format = "float" }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return LemonadeClient.ReadEmbeddingBatch(model, inputs.Count, json.RootElement.GetProperty("data"));
    }
    public void Dispose() => _http.Dispose();
}
