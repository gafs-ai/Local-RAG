using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record ServerOwnership(int ProcessId, long StartUtcTicks, string ExecutablePath, int Port, bool StopRequested = false)
{
    public bool Matches(Process process)
    {
        try
        {
            return !process.HasExited && process.Id == ProcessId && process.StartTime.ToUniversalTime().Ticks == StartUtcTicks &&
                string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed record ServerStatus(string Version, bool ManagedByApplication, int Port);

public sealed class SurrealServerManager : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _ownershipPath;
    private readonly SurrealServerInstaller _installer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ServerCredentialStore _credentials;
    private ServerOwnership? _owned;
    private FileStream? _managementLease;
    public ServerConnection? Connection { get; private set; }
    public ServerStatus? Status { get; private set; }

    public SurrealServerManager(string dataDirectory, string? toolsDirectory = null)
    {
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "server");
        _ownershipPath = Path.Combine(_directory, "ownership.json");
        _installer = new SurrealServerInstaller(toolsDirectory ?? Path.Combine(Path.GetFullPath(dataDirectory), "tools"));
        _credentials = new ServerCredentialStore(dataDirectory);
    }

    public async Task<ServerStatus> EnsureRunningAsync(DatabaseSettings settings, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        if (settings.Validate().Count > 0) throw new InvalidDataException(settings.Validate()[0]);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Connection is not null)
            {
                if (Status!.Port != settings.Port) throw new InvalidOperationException("DBポートの変更はアプリの再起動後に適用されます。");
                await VerifyConnectionAsync(Connection, token).ConfigureAwait(false);
                return Status;
            }
            Directory.CreateDirectory(_directory);
            // Held while this application is managing the server. A second manager must not acquire stop ownership.
            _managementLease ??= new FileStream(Path.Combine(_directory, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var saved = await ReadOwnershipAsync(token).ConfigureAwait(false);
            var credentials = await _credentials.LoadAsync(token).ConfigureAwait(false);
            if (await IsPortOpenAsync(settings.Port, token).ConfigureAwait(false))
            {
                progress?.Report("起動済みのSurrealDBへの接続を確認しています…");
                if (credentials is null) throw new InvalidDataException("DBポートは使用中です。既存サーバーの認証情報を設定するか、別のポートを指定してください。");
                var connection = new ServerConnection(settings.Endpoint, credentials);
                var version = await VerifyConnectionAsync(connection, token).ConfigureAwait(false);
                if (saved is not null && saved.Port == settings.Port && IsOwnedProcessAlive(saved)) _owned = saved;
                Connection = connection;
                return Status = new(version, _owned is not null, settings.Port);
            }

            if (saved is not null && IsOwnedProcessAlive(saved))
                throw new InvalidOperationException("以前起動したDBが応答していません。重複起動せず、既存プロセスの状態確認を待ちます。");
            var executable = await _installer.EnsureInstalledAsync(progress, token).ConfigureAwait(false);
            credentials ??= ServerCredentialStore.Generate();
            if (!File.Exists(Path.Combine(_directory, "credentials.dpapi")))
                await _credentials.SaveAsync(credentials, token).ConfigureAwait(false);
            var storage = Path.Combine(_directory, "data");
            progress?.Report("SurrealDBを起動しています…");
            token.ThrowIfCancellationRequested();
            using var process = WindowsServerProcess.Start(executable,
                ["start", "--bind", $"127.0.0.1:{settings.Port}", "--user", credentials.Username,
                    "--log", "warn", "--no-banner", "surrealkv://" + storage.Replace('\\', '/')], credentials.Password);
            _owned = new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, settings.Port);
            // Persist ownership before cancellation can abandon startup.
            var proposed = new ServerConnection(settings.Endpoint, credentials);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await WriteOwnershipAsync(_owned).ConfigureAwait(false);
                while (!process.HasExited)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (await IsPortOpenAsync(settings.Port, startup.Token).ConfigureAwait(false))
                    {
                        var version = await VerifyConnectionAsync(proposed, startup.Token).ConfigureAwait(false);
                        // If another process won the port race, our child must not be recorded as ready.
                        if (process.HasExited) break;
                        Connection = proposed;
                        return Status = new(version, true, settings.Port);
                    }
                    await Task.Delay(200, startup.Token).ConfigureAwait(false);
                }
                throw new InvalidOperationException("SurrealDBが起動中に終了しました。ポートと保存先を確認してください。");
            }
            catch (Exception startupError)
            {
                // Leave ownership intact if stopping fails, so a later run can recover it safely.
                try { await StopOwnedCoreAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception stopError) { throw new AggregateException("DB起動と終了処理に失敗しました。", startupError, stopError); }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ShutdownAsync(bool stopOnExit, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (stopOnExit) await StopOwnedCoreAsync(token).ConfigureAwait(false);
            Connection = null;
            Status = null;
            _owned = null;
            _managementLease?.Dispose();
            _managementLease = null;
        }
        finally { _gate.Release(); }
    }

    private async Task StopOwnedCoreAsync(CancellationToken token)
    {
        if (_owned is null) return;
        var owned = _owned;
        if (IsOwnedProcessAlive(owned))
        {
            using var server = Process.GetProcessById(owned.ProcessId);
            if (!owned.Matches(server)) throw new InvalidOperationException("DBプロセスの所有権を再確認できないため停止しません。");
            if (!owned.StopRequested)
            {
                var helper = Path.Combine(AppContext.BaseDirectory, "LocalRag.ServerControl.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException("DB終了用の補助プログラムがありません。", helper);
                _owned = owned with { StopRequested = true };
                await WriteOwnershipAsync(_owned).ConfigureAwait(false);
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add(owned.ProcessId.ToString());
                start.ArgumentList.Add(owned.StartUtcTicks.ToString());
                using var signal = Process.Start(start) ?? throw new InvalidOperationException("DB終了処理を起動できません。");
                await signal.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
                if (signal.ExitCode != 0 && !server.HasExited)
                    throw new InvalidOperationException($"DBへの通常終了通知に失敗しました（補助プログラム: {signal.ExitCode}）。強制終了はしていません。");
            }
            try { await server.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("DBの保存・終了を待っています。強制終了はしていません。"); }
        }
        // Only the verified owned process is stopped. An unrelated server is never killed by name or port.
        if (File.Exists(_ownershipPath)) File.Delete(_ownershipPath);
        _owned = null;
    }

    public static async Task<string> VerifyConnectionAsync(ServerConnection connection, CancellationToken token)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var text = await http.GetStringAsync(new Uri(connection.Endpoint, "/version"), token).ConfigureAwait(false);
        var match = Regex.Match(text, @"\bsurrealdb-(\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version < new Version(3, 1))
            throw new InvalidDataException("接続先にはDiskANN対応のSurrealDB 3.1以降が必要です。");
        await using var client = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        (await client.RawQuery("RETURN 1;", cancellationToken: token).ConfigureAwait(false)).EnsureAllOks();
        return version.ToString();
    }

    private async Task<ServerOwnership?> ReadOwnershipAsync(CancellationToken token)
    {
        if (!File.Exists(_ownershipPath)) return null;
        return JsonSerializer.Deserialize<ServerOwnership>(await File.ReadAllTextAsync(_ownershipPath, token).ConfigureAwait(false))
            ?? throw new InvalidDataException("DBプロセスの管理情報を読み取れません。");
    }

    private async Task WriteOwnershipAsync(ServerOwnership ownership)
    {
        var temporary = _ownershipPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ownership)).ConfigureAwait(false);
        File.Move(temporary, _ownershipPath, true);
    }

    private static bool IsOwnedProcessAlive(ServerOwnership ownership)
    {
        try { using var process = Process.GetProcessById(ownership.ProcessId); return ownership.Matches(process); }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false); return true; }
        catch (SocketException) { return false; }
    }

    public ValueTask DisposeAsync()
    {
        // Disposal alone never kills a server. The caller must apply the user's shutdown preference explicitly.
        _managementLease?.Dispose();
        _managementLease = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
