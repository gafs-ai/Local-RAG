using System.Net;
using System.Net.Sockets;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public static class OutboundHttp
{
    public static HttpClient ForLocalMcp(Uri uri) => OutboundPolicy.IsLocalHost(uri) && uri.Scheme == "http"
        ? new(ForEndpoint(uri, true)) : throw new ArgumentException(OutboundPolicy.CleartextError);

    internal static HttpMessageHandler ForEndpoint(Uri endpoint, bool verifyCertificate, HttpMessageHandler? inner = null)
    {
        OutboundPolicy.RequireAllowed(endpoint);
        return new OriginGuard(endpoint) { InnerHandler = inner ?? Transport(endpoint, verifyCertificate) };
    }

    private sealed class OriginGuard(Uri endpoint) : DelegatingHandler
    {
        private void CheckRequest(HttpRequestMessage request)
        {
            var uri = request.RequestUri;
            if (uri is null || !OutboundPolicy.IsAllowed(uri) || uri.Scheme != endpoint.Scheme || uri.IdnHost != endpoint.IdnHost || uri.Port != endpoint.Port)
                throw new HttpRequestException("設定した接続先と異なる宛先への送信を拒否しました。");
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            CheckRequest(request);
            return base.Send(request, token);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            CheckRequest(request);
            return base.SendAsync(request, token);
        }
    }

    private static SocketsHttpHandler Transport(Uri endpoint, bool verifyCertificate)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!verifyCertificate && endpoint.Scheme == "https") handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        if (endpoint.Scheme == "http")
        {
            IPAddress[] addresses = endpoint.Host switch { "127.0.0.1" => [IPAddress.Loopback], "[::1]" => [IPAddress.IPv6Loopback], _ => [IPAddress.IPv6Loopback, IPAddress.Loopback] };
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
        }
        return handler;
    }

    internal static async Task<HttpResponseMessage> DownloadAsync(HttpClient http, string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new HttpRequestException("ダウンロード先にはHTTPSが必要です。");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not { } next) throw new HttpRequestException("ダウンロードの転送先がありません。");
                uri = new Uri(uri, next);
            }
        }
        throw new HttpRequestException("ダウンロードの転送回数が上限を超えました。");
    }

    internal static HttpClient DownloadClient(TimeSpan timeout) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = timeout };
}
