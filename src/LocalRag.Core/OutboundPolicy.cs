namespace LocalRag.Core;

public static class OutboundPolicy
{
    public const string CleartextError = "外部への非暗号化通信は許可されていません。AIにはHTTPS、DBにはWSSまたはHTTPSを指定してください。";

    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 &&
        (uri.Scheme is "https" or "wss" || (uri.Scheme is "http" or "ws" && IsLocalHost(uri)));

    public static bool IsLocalHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0) return false;
        // Uri.Host normalizes alternative IPv4 spellings. Only accept the three explicit authority forms.
        var raw = uri.OriginalString.AsSpan();
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;
        var authority = raw[(scheme + 3)..];
        var end = authority.IndexOfAny('/', '?', '#');
        if (end >= 0) authority = authority[..end];
        var host = uri.Host;
        if (host is not ("localhost" or "127.0.0.1" or "[::1]")) return false;
        if (authority.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        if (!authority.StartsWith(host, StringComparison.OrdinalIgnoreCase) || authority.Length <= host.Length + 1 || authority[host.Length] != ':') return false;
        return authority[(host.Length + 1)..].IndexOfAnyExceptInRange('0', '9') < 0;
    }

    public static Uri RequireAllowed(Uri uri) => IsAllowed(uri) ? uri : throw new ArgumentException(CleartextError);
}
