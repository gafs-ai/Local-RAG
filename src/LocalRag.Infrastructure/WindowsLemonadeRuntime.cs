using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

internal sealed class WindowsLemonadeRuntime(string? isolatedProfile = null) : ILemonadeRuntime
{
    public async Task<IReadOnlyList<LemonadeModel>?> ProbeAsync(AiConnectionSettings settings, CancellationToken token)
    {
        using var client = new LemonadeClient(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await client.GetModelsAsync(timeout.Token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError &&
            ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { return null; }
        // HTTP errors, TLS errors, malformed responses and timeouts are not evidence of an absent server.
    }

    public Task<bool> PortOpenAsync(Uri endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!endpoint.IsLoopback) throw new ArgumentException("Lemonadeの自動起動はこのPCの接続先に限定します。", nameof(endpoint));
        // Connecting to localhost can spend several seconds refusing IPv6 then IPv4.
        // This is a local-only service: inspect both listener tables instead of interpreting
        // a short connection timeout as absence (or blocking a legitimate automatic start).
        return Task.FromResult(WindowsListenerOwner.HasListener(endpoint.Port));
    }

    public IOwnedLemonade Start(AiConnectionSettings settings)
    {
        var endpoint = settings.GetBaseUri();
        if (endpoint.Scheme != "http" || endpoint.Port < 1024 ||
            !new[] { "/api/v1/", "/api/v0/", "/v1/", "/v0/" }.Contains(endpoint.AbsolutePath))
            throw new InvalidOperationException("自動起動にはHTTPのローカルAPI接続先（例：http://localhost:13305/api/v1/）を指定してください。HTTPS等は起動済みサーバーへの接続のみ対応します。");
        var executable = FindExecutable(settings.ServerExecutablePath);
        var name = Path.GetFileName(executable);
        if (!name.Equals("LemonadeServer.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("lemond.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LemonadeServer.exeまたはlemond.exeを指定してください。lemonade.exeはサーバー起動用ではありません。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        // Integration tests use a private cache/config, without touching the user's installed models.
        if (isolatedProfile is not null)
        {
            info.ArgumentList.Add(Path.GetFullPath(isolatedProfile));
            info.ArgumentList.Add(Path.GetFullPath(isolatedProfile));
        }
        info.ArgumentList.Add("--host"); info.ArgumentList.Add(endpoint.Host == "localhost" ? "127.0.0.1" : endpoint.DnsSafeHost);
        info.ArgumentList.Add("--port"); info.ArgumentList.Add(endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (name.Equals("LemonadeServer.exe", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add("--silent");
        info.Environment.TryGetValue("LEMONADE_ADMIN_API_KEY", out var admin);
        if (string.IsNullOrEmpty(admin)) info.Environment.TryGetValue("LEMONADE_API_KEY", out admin);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Lemonadeを起動できません。");
        return new OwnedLemonade(process, executable, endpoint, admin);
    }

    internal static string FindExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new InvalidOperationException("指定されたLemonade実行ファイルが見つかりません。");
            return Path.GetFullPath(configured);
        }
        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lemonade_server", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lemonade Server", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "lemonade_server", "bin")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => p.Trim().Trim('"')))
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
            foreach (var name in new[] { "LemonadeServer.exe", "lemond.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        throw new InvalidOperationException("Lemonadeが見つかりません。インストール済みの場合は実行ファイルを選択してください。未導入の場合はLemonadeをインストールしてください。");
    }

    private sealed class OwnedLemonade : IOwnedLemonade
    {
        private readonly Process _process;
        private readonly ServerOwnership _identity;
        private readonly string? _admin;
        public Uri Endpoint { get; }
        public OwnedLemonade(Process process, string executable, Uri endpoint, string? admin)
        {
            _process = process; Endpoint = endpoint; _admin = admin;
            _identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, endpoint.Port);
        }
        public bool IsAlive => _identity.Matches(_process);
        public bool OwnsListener => IsAlive && WindowsListenerOwner.IsOnlyOwner(Endpoint.Port, _process.Id);
        public async Task StopAsync(CancellationToken token)
        {
            if (!IsAlive) return;
            if (!OwnsListener) throw new InvalidOperationException("起動したLemonadeの待受プロセスを確認できないため停止しません。Lemonadeの状態を確認してください。");
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(35) };
            if (!string.IsNullOrEmpty(_admin)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _admin);
            try
            {
                using var response = await http.PostAsync(new Uri(Endpoint, "/internal/shutdown"), new StringContent("", System.Text.Encoding.UTF8, "application/json"), token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException) when (!IsAlive) { /* Server may exit just before the response is received. */ }
            try { await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("Lemonadeの終了を待っています。強制終了はしていません。"); }
        }
        public void Dispose() => _process.Dispose();
    }
}

internal static class WindowsListenerOwner
{
    internal static bool HasListener(int port) =>
        Owners(port, 2, 24, 8, 20).Count > 0 || Owners(port, 23, 56, 20, 52).Count > 0;

    internal static bool IsOnlyOwner(int port, int processId)
    {
        var owners = Owners(port, 2, 24, 8, 20).Concat(Owners(port, 23, 56, 20, 52)).ToArray();
        return owners.Length > 0 && owners.All(id => id == processId);
    }

    private static IReadOnlyList<int> Owners(int port, uint family, int rowSize, int portOffset, int pidOffset)
    {
        var size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (error != 122) throw new Win32Exception((int)error);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception((int)error);
                var result = new List<int>();
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * rowSize);
                    if ((Marshal.ReadByte(row, portOffset) << 8 | Marshal.ReadByte(row, portOffset + 1)) == port)
                        result.Add(Marshal.ReadInt32(row, pidOffset));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Lemonadeの待受プロセスを確認できません。");
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);
}
