using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalRag.Infrastructure;

// Supplied by distribution configuration outside source control. A distributed key deters forgery;
// it cannot prove an unmodified client. This is never an analytics/admin access credential.
public sealed record UsageStatisticsTransportSettings(Uri Endpoint, string ReleaseKeyId, byte[] SigningKey);

public sealed class UsageStatisticsSender : IDisposable
{
    public const int MaximumBodyBytes = 32768;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _keyId;
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<UsageStatisticsBatch, Envelope> _envelopes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed class Envelope(byte[] body, string nonce, long timestamp)
    {
        public byte[] Body { get; } = body;
        public string Nonce { get; } = nonce;
        public long Timestamp { get; set; } = timestamp;
    }
    public UsageStatisticsSender(UsageStatisticsTransportSettings settings) : this(settings,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null }, TimeProvider.System) { }
    internal UsageStatisticsSender(UsageStatisticsTransportSettings settings, HttpMessageHandler handler, TimeProvider time)
    {
        if (!settings.Endpoint.IsAbsoluteUri || settings.Endpoint.Scheme != "https" || settings.Endpoint.IsLoopback ||
            settings.Endpoint.UserInfo.Length != 0 || settings.Endpoint.Query.Length != 0 || settings.Endpoint.Fragment.Length != 0 ||
            settings.Endpoint.AbsolutePath != "/api/v1/reports") throw new ArgumentException("Invalid statistics endpoint.", nameof(settings));
        if (settings.SigningKey.Length != 32 || !Regex.IsMatch(settings.ReleaseKeyId, "\\A[a-zA-Z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid statistics credentials.", nameof(settings));
        _endpoint = settings.Endpoint; _keyId = settings.ReleaseKeyId; _key = settings.SigningKey.ToArray(); _time = time;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 };
    }
    public async Task<bool> SendAsync(UsageStatisticsBatch batch, CancellationToken token = default)
    {
        if (batch.Json.Length is <= 0 or > MaximumBodyBytes) return false;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            if (IsExpired(batch)) return false;
            var envelope = _envelopes.GetValue(batch, b => new(b.Json.ToArray(), b.Nonce, now));
            // Keep report nonce/body across retries, including retries after a stale timestamp.
            if (now - envelope.Timestamp > 240 || now < envelope.Timestamp) envelope.Timestamp = now;
            var timestamp = envelope.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var hash = Convert.ToHexString(SHA256.HashData(envelope.Body)).ToLowerInvariant();
            var signed = Encoding.UTF8.GetBytes($"v1\nPOST\n/api/v1/reports\n{_keyId}\n{timestamp}\n{envelope.Nonce}\n{hash}");
            var signature = Convert.ToHexString(HMACSHA256.HashData(_key, signed)).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("X-LocalRag-Key-Id", _keyId);
            request.Headers.Add("X-LocalRag-Timestamp", timestamp);
            request.Headers.Add("X-LocalRag-Nonce", envelope.Nonce);
            request.Headers.Add("X-LocalRag-Signature", signature);
            request.Content = new ByteArrayContent(envelope.Body);
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { return false; }
        finally { _gate.Release(); }
    }
    public bool IsExpired(UsageStatisticsBatch batch) => _time.GetUtcNow().ToUnixTimeSeconds() - batch.CreatedAt >= 7 * 86400;
    public void Dispose() { _client.Dispose(); CryptographicOperations.ZeroMemory(_key); _gate.Dispose(); }
}
