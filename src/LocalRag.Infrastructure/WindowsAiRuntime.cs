using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

internal sealed class WindowsAiRuntime(AiEndpointSettings endpoint, string? apiKey) : ILemonadeRuntime
{
    internal AiEndpointSettings Endpoint { get; set; } = endpoint;
    internal string? ApiKey { get; set; } = apiKey;
    public async Task<IReadOnlyList<LemonadeModel>?> ProbeAsync(AiConnectionSettings settings, CancellationToken token)
    {
        using var client = AiModelClient.Create(Endpoint, ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await client.GetModelsAsync(timeout.Token).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError &&
            ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { return null; }
    }
    public Task<bool> PortOpenAsync(Uri endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!endpoint.IsLoopback) throw new InvalidOperationException("外部サーバーは自動起動できません。");
        return Task.FromResult(WindowsListenerOwner.HasListener(endpoint.Port));
    }
    public IOwnedLemonade Start(AiConnectionSettings settings)
    {
        var uri = Endpoint.GetBaseUri();
        if (!uri.IsLoopback || uri.Scheme != "http" || uri.Port < 1024) throw new InvalidOperationException("自動起動はこのPCのHTTP接続先と1024以上のポートに限定します。");
        if (Endpoint.Provider == AiProvider.Lemonade) return new WindowsLemonadeRuntime(shutdownApiKey: ApiKey).Start(settings);
        if (Endpoint.Provider == AiProvider.LmStudio)
        {
            if (uri.AbsolutePath != "/v1/") throw new InvalidOperationException("LM Studioの自動起動では接続先のパスを/v1/にしてください。");
            return new OwnedLmStudio(FindLms(Endpoint.ServerExecutablePath), uri);
        }
        if (uri.AbsolutePath is not ("/v3/" or "/v1/") || !File.Exists(Endpoint.ServerExecutablePath) || !File.Exists(Endpoint.ModelConfigPath))
            throw new InvalidOperationException("OVMSの自動起動には/v3/または/v1/の接続先と、実在するovms.exe・モデル設定ファイルを指定してください。");
        if (!Path.GetFileName(Endpoint.ServerExecutablePath).Equals("ovms.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("OVMS実行ファイルにはovms.exeを指定してください。");
        return new OwnedOvms(WindowsServerProcess.StartIsolated(Endpoint.ServerExecutablePath,
            ["--config_path", Endpoint.ModelConfigPath, "--port", "0", "--rest_port", uri.Port.ToString(), "--rest_bind_address", uri.Host == "localhost" ? "127.0.0.1" : uri.DnsSafeHost],
            OvmsEnvironment(Endpoint.ServerExecutablePath)), Endpoint.ServerExecutablePath, uri);
    }

    // Match the documented Windows binary-package layout, without running an arbitrary setup script
    // or changing this application's environment (which also hosts Python-based parsers).
    internal static IReadOnlyDictionary<string, string> OvmsEnvironment(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OVMS_DIR"] = directory };
        var paths = new List<string> { directory };
        var python = Path.Combine(directory, "python");
        if (Directory.Exists(python))
        {
            values["PYTHONHOME"] = python; values["SCRIPTS"] = Path.Combine(python, "Scripts");
            paths.Add(python); paths.Add(values["SCRIPTS"]);
        }
        var speech = Path.Combine(directory, "espeak-ng-data");
        if (Directory.Exists(speech)) values["ESPEAK_DATA_PATH"] = speech;
        values["PATH"] = string.Join(Path.PathSeparator, paths.Append(Environment.GetEnvironmentVariable("PATH") ?? ""));
        return values;
    }
    internal static string FindLms(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured) || !Path.GetFileName(configured).Equals("lms.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LM Studioには実在するlms.exeの絶対パスを指定してください。");
            return configured;
        }
        var directories = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => p.Trim(' ', '"'))).Where(Path.IsPathFullyQualified);
        return directories.Select(p => Path.Combine(p, "lms.exe")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("lms.exeが見つかりません。LM Studioまたはllmsterとlms CLIをインストールしてください。");
    }

    private sealed class OwnedOvms : IOwnedLemonade
    {
        private readonly Process _process;
        private readonly ServerOwnership _identity;
        public Uri Endpoint { get; }
        public OwnedOvms(Process process, string executable, Uri endpoint)
        { _process = process; Endpoint = endpoint; _identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, endpoint.Port); }
        public bool IsAlive => _identity.Matches(_process);
        public bool OwnsListener => IsAlive && WindowsListenerOwner.IsOnlyOwner(Endpoint.Port, _process.Id);
        public async Task StopAsync(CancellationToken token)
        {
            if (!IsAlive) return;
            var helper = Path.Combine(AppContext.BaseDirectory, "LocalRag.ServerControl.exe");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(_identity.ProcessId.ToString()); start.ArgumentList.Add(_identity.StartUtcTicks.ToString());
            using var signal = Process.Start(start) ?? throw new InvalidOperationException("OVMS終了通知を起動できません。");
            await signal.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
            if (signal.ExitCode != 0 && IsAlive) throw new InvalidOperationException("OVMSを通常終了できませんでした。強制終了はしていません。");
            await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
        public void Dispose() => _process.Dispose();
    }

    // lms is a short-lived controller, not the HTTP listener. Track the actual listener's identity.
    private sealed class OwnedLmStudio : IOwnedLemonade
    {
        private readonly string _lms;
        private readonly Task _startup;
        private Process? _listener;
        private ServerOwnership? _identity;
        public Uri Endpoint { get; }
        public OwnedLmStudio(string lms, Uri endpoint) { _lms = lms; Endpoint = endpoint; _startup = StartAsync(); }
        public bool IsAlive => !_startup.IsCompleted || _identity is not null && _listener is not null && _identity.Matches(_listener) && WindowsListenerOwner.IsOnlyOwner(Endpoint.Port, _identity.ProcessId);
        public bool StartupPending => !_startup.IsCompleted;
        public bool OwnsListener => _startup.IsCompletedSuccessfully && IsAlive && WindowsListenerOwner.IsOnlyOwner(Endpoint.Port, _identity!.ProcessId);
        private async Task StartAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            try
            {
                using var status = JsonDocument.Parse(await CommandAsync(["server", "status", "--json", "--quiet"], timeout.Token).ConfigureAwait(false));
                if (!status.RootElement.TryGetProperty("running", out var running) || running.ValueKind != JsonValueKind.False) return; // Never redirect an existing server from another port.
                if (WindowsListenerOwner.HasListener(Endpoint.Port)) return;
                await CommandAsync(["server", "start", "--port", Endpoint.Port.ToString(), "--bind", Endpoint.Host == "localhost" ? "127.0.0.1" : Endpoint.DnsSafeHost], timeout.Token).ConfigureAwait(false);
                var ids = WindowsListenerOwner.ProcessIds(Endpoint.Port);
                if (ids.Length != 1 || !await MatchesStatusAsync(timeout.Token).ConfigureAwait(false)) return;
                _listener = Process.GetProcessById(ids[0]);
                var executable = _listener.MainModule?.FileName;
                if (executable is not null) _identity = new(ids[0], _listener.StartTime.ToUniversalTime().Ticks, executable, Endpoint.Port);
            }
            catch (Exception) { /* Readiness fails closed; never acquire an unrelated listener or kill a controller's children. */ }
        }
        private async Task<bool> MatchesStatusAsync(CancellationToken token)
        {
            using var status = JsonDocument.Parse(await CommandAsync(["server", "status", "--json", "--quiet"], token).ConfigureAwait(false));
            return status.RootElement.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True &&
                status.RootElement.TryGetProperty("port", out var port) && port.TryGetInt32(out var value) && value == Endpoint.Port;
        }
        private async Task<string> CommandAsync(string[] arguments, CancellationToken token)
        {
            var info = new ProcessStartInfo(_lms) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("lmsを起動できません。");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await error.ConfigureAwait(false); // Never expose CLI output: it can contain private model paths.
            if (process.ExitCode != 0) throw new InvalidOperationException("LM Studioの操作に失敗しました。lmsの設定を確認してください。");
            return await output.ConfigureAwait(false);
        }
        public async Task StopAsync(CancellationToken token)
        {
            await _startup.WaitAsync(token).ConfigureAwait(false);
            if (_identity is null) return;
            if (!OwnsListener || !await MatchesStatusAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("LM Studioの待受プロセスを確認できないため停止しません。");
            await CommandAsync(["server", "stop"], token).WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            // Shared LM Studio GUI/daemon stays alive; only the HTTP server started by this app stops.
        }
        public void Dispose() => _listener?.Dispose();
    }
}
