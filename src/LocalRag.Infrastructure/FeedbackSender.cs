using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>One immutable manual submission. Retain it for an explicit retry after an uncertain result.</summary>
public sealed class FeedbackBatch
{
    private readonly byte[] _body;
    private FeedbackBatch(byte[] body)
    {
        _body = body;
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public FeedbackReport Report => JsonSerializer.Deserialize<FeedbackReport>(_body, UsageStatisticsPrivacy.JsonOptions)!;
    public byte[] Json => _body.ToArray();
    internal ReadOnlyMemory<byte> Body => _body;
    internal string Nonce { get; }
    internal long CreatedAt { get; }

    public static FeedbackBatch Create(string category, string message, UsageStatisticsReport? usage = null)
    {
        if (category is not ("improvement" or "bug" or "other"))
            throw new ArgumentException("Unknown feedback category.", nameof(category));
        var text = message?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > FeedbackSender.MaximumMessageCharacters)
            throw new ArgumentException("Feedback must contain 1 to 8000 characters.", nameof(message));
        if (text.Any(c => c is <= '\u0008' or '\u000B' or '\u000C' or (>= '\u000E' and <= '\u001F') or '\u007F'))
            throw new ArgumentException("Feedback contains unsupported control characters.", nameof(message));
        var report = new FeedbackReport(1, ProductInfo.Version, category, text, usage);
        var body = JsonSerializer.SerializeToUtf8Bytes(report, UsageStatisticsPrivacy.JsonOptions);
        if (body.Length > FeedbackSender.MaximumBodyBytes)
            throw new ArgumentException("Feedback is too large.", nameof(message));
        // Validate the serialized snapshot, not caller-owned mutable collections.
        var snapshot = JsonSerializer.Deserialize<FeedbackReport>(body, UsageStatisticsPrivacy.JsonOptions)!;
        if (snapshot.Usage is { } attached && (attached.AppVersion != snapshot.AppVersion ||
            !UsageStatisticsPrivacy.ValidReport(attached, allowEmptyProcessing: true)))
            throw new ArgumentException("Invalid usage statistics attachment.", nameof(usage));
        return new(body);
    }
}

/// <summary>Manual, bounded submissions independent of the automatic statistics sender and timer.</summary>
public sealed class FeedbackSender : IDisposable
{
    public const int MaximumMessageCharacters = 8000;
    public const int MaximumBodyBytes = 98304;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _keyId;
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<FeedbackBatch, Envelope> _envelopes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed class Envelope(long timestamp) { public long Timestamp { get; set; } = timestamp; }

    public FeedbackSender(UsageStatisticsTransportSettings settings) : this(settings,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null },
        TimeProvider.System) { }

    internal FeedbackSender(UsageStatisticsTransportSettings settings, HttpMessageHandler handler, TimeProvider time)
    {
        if (!settings.Endpoint.IsAbsoluteUri || settings.Endpoint.Scheme != "https" || settings.Endpoint.IsLoopback ||
            settings.Endpoint.UserInfo.Length != 0 || settings.Endpoint.Query.Length != 0 || settings.Endpoint.Fragment.Length != 0 ||
            settings.Endpoint.AbsolutePath != "/api/v1/reports")
            throw new ArgumentException("Invalid feedback transport configuration.", nameof(settings));
        if (settings.SigningKey.Length != 32 || !Regex.IsMatch(settings.ReleaseKeyId, "\\A[a-zA-Z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid feedback credentials.", nameof(settings));
        _endpoint = new Uri(settings.Endpoint, "/api/v1/feedback");
        _keyId = settings.ReleaseKeyId;
        _key = settings.SigningKey.ToArray();
        _time = time;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 };
    }

    public async Task<bool> SendAsync(FeedbackBatch batch, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsExpired(batch)) return false;
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var envelope = _envelopes.GetValue(batch, _ => new(now));
            if (now - envelope.Timestamp > 240 || now < envelope.Timestamp) envelope.Timestamp = now;
            var timestamp = envelope.Timestamp.ToString(CultureInfo.InvariantCulture);
            var body = batch.Body.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var signed = Encoding.UTF8.GetBytes($"v1\nPOST\n/api/v1/feedback\n{_keyId}\n{timestamp}\n{batch.Nonce}\n{hash}");
            var signature = Convert.ToHexString(HMACSHA256.HashData(_key, signed)).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("X-LocalRag-Key-Id", _keyId);
            request.Headers.Add("X-LocalRag-Timestamp", timestamp);
            request.Headers.Add("X-LocalRag-Nonce", batch.Nonce);
            request.Headers.Add("X-LocalRag-Signature", signature);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        finally { _gate.Release(); }
    }

    public bool IsExpired(FeedbackBatch batch) => _time.GetUtcNow().ToUnixTimeSeconds() - batch.CreatedAt >= 7 * 86400;
    public void Dispose() { _client.Dispose(); CryptographicOperations.ZeroMemory(_key); _gate.Dispose(); }
}
