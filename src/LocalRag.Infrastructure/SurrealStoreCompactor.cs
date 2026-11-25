using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Copies logical records, table/index definitions and the app-owned principal with pinned
/// embedded SurrealDB 3.3.5 sessions. A temporary loopback /version listener initializes the empty
/// candidate; document data is copied over anonymous pipes, never to a dump file.</summary>
internal static class SurrealStoreCompactor
{
    public static async Task CopyVerifiedAsync(string executable, string sourceDirectory, string emptyTargetDirectory,
        ServerCredentials credentials, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(emptyTargetDirectory);
        if (Path.GetDirectoryName(source) is not { } parent ||
            !string.Equals(parent, Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(source, "manifest")) ||
            (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new InvalidDataException("整理先は元のDBと同じ親フォルダ内の空の別フォルダである必要があります。");
        Directory.CreateDirectory(target);
        if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Unsupported();
        var scratch = Directory.CreateTempSubdirectory("LocalRAG-db-copy-");
        try
        {
            // rustyline always attempts to save "history.txt" on EOF. Never let it persist credentials or content.
            Directory.CreateDirectory(Path.Combine(scratch.FullName, "history.txt"));
            await using var reader = new Shell(executable, source, scratch.FullName);
            var original = await Catalog.ReadAsync(reader, credentials.Username, token);
            await BootstrapTargetAsync(executable, target, credentials, token);
            var digests = new Dictionary<(string, string, string), string>();
            await using (var writer = new Shell(executable, target, scratch.FullName, candidateWriter: true, token: token))
            {
                foreach (var ns in original.Namespaces)
                {
                    await writer.ExecuteAsync($"DEFINE NAMESPACE IF NOT EXISTS {Id(ns.Name)}", token);
                    await writer.ExecuteAsync($"USE NS {Id(ns.Name)}", token, isUse: true);
                    foreach (var db in ns.Databases)
                    {
                        await writer.ExecuteAsync($"DEFINE DATABASE IF NOT EXISTS {Id(db.Name)}", token);
                        await UseAsync(writer, ns.Name, db.Name, token);
                        await UseAsync(reader, ns.Name, db.Name, token);
                        foreach (var table in db.Tables)
                        {
                            progress?.Report($"DB整理: {ns.Name}/{db.Name}/{table.Name} をコピーしています。");
                            await writer.ExecuteAsync(table.Definition, token);
                            digests[(ns.Name, db.Name, table.Name)] = await WalkAsync(reader, table.Name, writer, target, progress, token);
                            foreach (var index in table.Indexes) await writer.ExecuteAsync(index, token);
                        }
                    }
                }
                await writer.CloseAsync(token); // Flush before a separate reader reopens the physical target.
            }
            await using var verification = new Shell(executable, target, scratch.FullName);
            progress?.Report("DB整理: 保存したすべての文書・ベクトルとテーブル定義を照合しています。");
            var copied = await Catalog.ReadAsync(verification, credentials.Username, token);
            if (JsonSerializer.Serialize(original) != JsonSerializer.Serialize(copied))
                throw new InvalidDataException("整理後のDB定義が元のDBと一致しません。");
            foreach (var ns in copied.Namespaces)
            foreach (var db in ns.Databases)
            {
                await UseAsync(verification, ns.Name, db.Name, token);
                foreach (var table in db.Tables)
                    if (digests[(ns.Name, db.Name, table.Name)] != await WalkAsync(verification, table.Name, null, target, progress, token))
                        throw new InvalidDataException("整理後の文書・ベクトルの照合に失敗しました。");
            }
            await verification.CloseAsync(token);
            await reader.CloseAsync(token);
        }
        finally { scratch.Delete(true); }
    }

    private static InvalidDataException Unsupported() => new("このDBの構成には自動整理で安全に再現できない設定があります。元のDBを使用します。");
    private static string Id(string name) => Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z_0-9]*\z", RegexOptions.CultureInvariant) ? $"`{name}`" : throw Unsupported();
    private static Task UseAsync(Shell shell, string ns, string db, CancellationToken token)
        => shell.ExecuteAsync($"USE NS {Id(ns)} DB {Id(db)}", token, isUse: true);

    private static async Task<string> WalkAsync(Shell reader, string table, Shell? writer, string target,
        IProgress<string>? progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? last = null;
        var page = 8;
        long recordsRead = 0;
        long nextUpdate = 1000;
        var space = new DriveInfo(Path.GetPathRoot(target)!);
        do
        {
            token.ThrowIfCancellationRequested();
            if (writer is not null && space.AvailableFreeSpace < 512L * 1024 * 1024)
                throw new IOException("DB整理用のディスク空き容量が不足しています。");
            // Primary-key range (not START/OFFSET); no scan of already copied rows.
            var from = last is null ? Id(table) : last + ">..";
            var rows = await reader.ResultAsync($"SELECT id, type::string(id) AS cursor_id, (in != NONE AND out != NONE) AS possible_edge, encoding::base64::encode(encoding::cbor::encode($this)) AS row_data FROM {from} ORDER BY id LIMIT {page}", token);
            if (rows.ValueKind != JsonValueKind.Array) throw Unsupported();
            if (rows.GetArrayLength() == 0) break;
            var records = new List<string>();
            long size = 0;
            foreach (var row in rows.EnumerateArray())
            {
                // TYPE ANY permits RELATE too. A plain INSERT cannot rebuild directional traversal keys.
                if (row.GetProperty("possible_edge").GetBoolean()) throw Unsupported();
                var cursor = row.GetProperty("cursor_id").GetString() ?? throw Unsupported();
                // Only plain scalar record IDs produced by the current app. Never interpret arbitrary ID text as SQL.
                if (!Regex.IsMatch(cursor, @"\A(?:" + Regex.Escape(table) + @"|`" + Regex.Escape(table) + @"`):(?:[A-Za-z_0-9-]+|⟨[A-Za-z_0-9.-]+⟩|`[A-Za-z_0-9.-]+`)\z", RegexOptions.CultureInvariant) || cursor == last)
                    throw Unsupported();
                last = cursor;
                var encoded = row.GetProperty("row_data").GetString() ?? throw Unsupported();
                // The pinned engine encodes CBOR record IDs, float vectors, datetimes and byte strings.
                // The CLI only ever parses a base64 alphabet, including when a document contains SQL text.
                if (encoded.Length == 0 || encoded.AsSpan().IndexOfAnyExcept(Base64) >= 0) throw Unsupported();
                hash.AppendData(Encoding.ASCII.GetBytes(encoded));
                hash.AppendData([0]);
                if (writer is not null) records.Add($"encoding::cbor::decode(encoding::base64::decode('{encoded}'))");
                size += encoded.Length;
            }
            if (writer is not null) await writer.ExecuteAsync("INSERT [" + string.Join(',', records) + "] RETURN NONE", token);
            recordsRead += rows.GetArrayLength();
            if (recordsRead >= nextUpdate)
            {
                progress?.Report($"DB整理: {table} を {recordsRead:N0} 件{(writer is null ? "照合" : "コピー")}しました。");
                nextUpdate += 1000;
            }
            page = (int)Math.Clamp(512 * 1024 / Math.Max(1, size / rows.GetArrayLength()), 1, 256);
        } while (true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static readonly System.Buffers.SearchValues<char> Base64 =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    private sealed record Ns(string Name, List<Db> Databases);
    private sealed record Db(string Name, List<Tbl> Tables);
    private sealed record Tbl(string Name, string Definition, List<string> Indexes);
    private sealed record Catalog(List<Ns> Namespaces)
    {
        private static IEnumerable<JsonProperty> Fields(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) throw Unsupported();
            return obj.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal);
        }
        private static void ExactKeys(JsonElement obj, params string[] keys)
        {
            if (!Fields(obj).Select(p => p.Name).SequenceEqual(keys.Order(StringComparer.Ordinal))) throw Unsupported();
        }
        private static void Empty(JsonElement obj, params string[] keys)
        {
            if (keys.Any(key => Fields(obj.GetProperty(key)).Any())) throw Unsupported();
        }
        public static async Task<Catalog> ReadAsync(Shell shell, string user, CancellationToken token)
        {
            var root = await shell.ResultAsync("INFO FOR ROOT", token);
            ExactKeys(root, "accesses", "config", "defaults", "namespaces", "nodes", "system", "users");
            Empty(root, "accesses");
            var defaults = root.GetProperty("defaults"); ExactKeys(defaults, "database", "namespace");
            if (defaults.GetProperty("database").GetString() != "main" || defaults.GetProperty("namespace").GetString() != "main") throw Unsupported();
            var config = root.GetProperty("config"); ExactKeys(config, "QUERY_TIMEOUT");
            if (config.GetProperty("QUERY_TIMEOUT").ValueKind != JsonValueKind.Null) throw Unsupported();
            var users = root.GetProperty("users"); ExactKeys(users, user);
            // An app-generated root account only; refuse rather than replacing extra users/grants or custom roles.
            if (user != "localrag" || users.GetProperty(user).GetString() != "DEFINE USER localrag ON ROOT PASSHASH '[REDACTED]' PASSSCRAM '[REDACTED]' ROLES OWNER DURATION FOR TOKEN 1h, FOR SESSION NONE") throw Unsupported();
            var catalog = new Catalog([]);
            foreach (var n in Fields(root.GetProperty("namespaces")))
            {
                ValidateScope("NAMESPACE", n, n.Name == "main");
                var ns = new Ns(n.Name, []); catalog.Namespaces.Add(ns);
                await shell.ExecuteAsync($"USE NS {Id(ns.Name)}", token, isUse: true);
                var nsInfo = await shell.ResultAsync("INFO FOR NS", token);
                ExactKeys(nsInfo, "accesses", "databases", "users"); Empty(nsInfo, "accesses", "users");
                foreach (var d in Fields(nsInfo.GetProperty("databases")))
                {
                    ValidateScope("DATABASE", d, ns.Name == "main" && d.Name == "main");
                    var db = new Db(d.Name, []); ns.Databases.Add(db);
                    await UseAsync(shell, ns.Name, db.Name, token);
                    var info = await shell.ResultAsync("INFO FOR DB", token);
                    ExactKeys(info, "accesses", "analyzers", "apis", "buckets", "configs", "functions", "models", "modules", "params", "sequences", "tables", "users");
                    Empty(info, "accesses", "analyzers", "apis", "buckets", "configs", "functions", "models", "modules", "params", "sequences", "users");
                    foreach (var tab in Fields(info.GetProperty("tables")))
                    {
                        var name = Id(tab.Name);
                        var definition = tab.Value.GetString() ?? throw Unsupported();
                        // Current app uses ordinary schemaless tables. Explicitly refuse changefeeds, relations,
                        // views, custom access policies, events, live queries and derived fields.
                        if (!new[]{tab.Name, name}.Any(t => definition == $"DEFINE TABLE {t} TYPE ANY SCHEMALESS PERMISSIONS NONE")) throw Unsupported();
                        var infoTb = await shell.ResultAsync($"INFO FOR TABLE {name}", token);
                        ExactKeys(infoTb, "events", "fields", "indexes", "lives", "tables");
                        Empty(infoTb, "events", "fields", "lives", "tables");
                        List<string> indexes = [];
                        foreach (var index in Fields(infoTb.GetProperty("indexes")))
                        {
                            _ = Id(index.Name);
                            var def = index.Value.GetString() ?? throw Unsupported();
                            // Accept only ordinary scalar/unique and the application's DISKANN index definitions.
                            var field = @"`?[A-Za-z_][A-Za-z_0-9]*`?";
                            var rx = $@"\ADEFINE INDEX `?{Regex.Escape(index.Name)}`? ON `?{Regex.Escape(tab.Name)}`? FIELDS {field}(?:, {field})*(?: UNIQUE| DISKANN DIMENSION [0-9]+ DIST COSINE TYPE F32 DEGREE [0-9]+ L_BUILD [0-9]+ ALPHA [0-9.]+f)?\z";
                            if (!Regex.IsMatch(def, rx, RegexOptions.CultureInvariant)) throw Unsupported();
                            indexes.Add(def);
                        }
                        db.Tables.Add(new(tab.Name, definition, indexes));
                    }
                }
            }
            return catalog;
        }
        private static void ValidateScope(string kind, JsonProperty entry, bool isDefault)
        {
            var name = Id(entry.Name);
            var suffix = isDefault ? $" COMMENT 'Default {kind.ToLowerInvariant()} generated by SurrealDB'" : "";
            var actual = entry.Value.GetString();
            if (actual != $"DEFINE {kind} {entry.Name}{suffix}" && actual != $"DEFINE {kind} {name}{suffix}") throw Unsupported();
        }
    }

    private static async Task BootstrapTargetAsync(string executable, string target, ServerCredentials credentials, CancellationToken token)
    {
        // Only the empty candidate needs the normal 3.3.5 server bootstrap (main/main + default IDs + root).
        // It has no user data. Allow only /version, never normal API or query access. The source is never served.
        var helper = Path.Combine(AppContext.BaseDirectory, "LocalRag.ServerControl.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("DB終了用の補助プログラムが見つかりません。", helper);
        int port;
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        { listener.Start(); port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        var bootstrap = WindowsServerProcess.StartForMaintenance(executable,
            ["start","--bind",$"127.0.0.1:{port}","--deny-all","--allow-http","version","--log","none","--no-banner",
                "--user", credentials.Username, "surrealkv://"+target.Replace('\\','/')], credentials.Password);
        using var lifetime = bootstrap.Lifetime;
        using var process = bootstrap.Process;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            while (!WindowsListenerOwner.IsOnlyOwner(port, process.Id))
            {
                cts.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("整理先のDB初期化に失敗しました。");
                await Task.Delay(100, cts.Token);
            }
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var signal = Process.Start(start) ?? throw new IOException("整理先DBを停止できませんでした。");
            await signal.WaitForExitAsync(cts.Token);
            if (signal.ExitCode != 0 && !process.HasExited) throw new IOException("整理先DBを停止できませんでした。");
            await process.WaitForExitAsync(cts.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    internal sealed class Shell : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task errorDrain;
        private readonly bool candidateWriter;
        private readonly CancellationTokenRegistration lifetimeCancellation;
        private bool closed;

        public Shell(string executable, string store, string scratch, bool candidateWriter = false, CancellationToken token = default)
        {
            this.candidateWriter = candidateWriter;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scratch,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("SURREAL_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["NO_COLOR"] = "1";
            start.Environment["SURREAL_TELEMETRY_PROVIDER"] = "none";
            foreach (var arg in new[] { "sql", "--endpoint", "surrealkv://" + store.Replace('\\', '/'), "--json", "--hide-welcome", "--log", "none" })
                start.ArgumentList.Add(arg);
            process = Process.Start(start) ?? throw new IOException("DB整理用のプロセスを開始できませんでした。");
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            // SurrealKV can flush between CLI commands or while we are copying from the original.
            lifetimeCancellation = StopCandidateOnCancellation(token);
        }

        public async Task<JsonElement> ResultAsync(string command, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var ct = timeout.Token;
            using var onTimeout = StopCandidateOnCancellation(ct);
            try
            {
                var result = await RunCommandAsync(command, ct);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                if (candidateWriter) await AbortCandidateAsync();
                throw new OperationCanceledException(ct);
            }
        }

        private async Task<JsonElement> RunCommandAsync(string command, CancellationToken ct)
        {
            var marker = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(command.AsMemory(), ct);
            await process.StandardInput.WriteLineAsync($"RETURN '{marker}'".AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            JsonElement? result = null;
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var start = line.IndexOf('[');
                if (start < 0) continue;
                using var json = JsonDocument.Parse(line[start..]);
                var values = json.RootElement;
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 1) throw Unsupported();
                var value = values[0];
                if (value.ValueKind == JsonValueKind.String && value.GetString() == marker)
                    return result ?? throw new InvalidDataException("DB整理用クエリが完了しませんでした。");
                if (result.HasValue) throw Unsupported();
                result = value.Clone();
            }
            throw new IOException("DB整理用のプロセスが応答を完了できませんでした。");
        }

        public async Task ExecuteAsync(string command, CancellationToken token, bool isUse = false)
        {
            var value = await ResultAsync(command, token);
            if ((isUse && value.ValueKind == JsonValueKind.Object) ||
                (!isUse && (value.ValueKind == JsonValueKind.Null ||
                            (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)))) return;
            throw new InvalidDataException("DB整理用の操作を完了できませんでした。");
        }

        public async Task CloseAsync(CancellationToken token)
        {
            if (closed) return;
            closed = true;
            // A source read or flush already in flight is allowed to close normally, even after cancellation.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(candidateWriter ? token : CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            using var onTimeout = StopCandidateOnCancellation(timeout.Token);
            try
            {
                process.StandardInput.Close();
                var drain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                await Task.WhenAll(drain, errorDrain, process.WaitForExitAsync()).WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new IOException("DB整理用のプロセスが正常終了しませんでした。");
            }
            catch (Exception) when (candidateWriter && timeout.IsCancellationRequested)
            {
                await AbortCandidateAsync();
                throw new OperationCanceledException(timeout.Token);
            }
            finally
            {
                if (!process.HasExited) { KillIfRunning(); await process.WaitForExitAsync(); }
            }
        }

        private CancellationTokenRegistration StopCandidateOnCancellation(CancellationToken token)
            => candidateWriter ? token.UnsafeRegister(static shell =>
            {
                // Keep cancellation callbacks nonthrowing. The awaited cleanup retries and confirms exit.
                try { ((Shell)shell!).KillIfRunning(); }
                catch (System.ComponentModel.Win32Exception) { }
            }, this) : default;

        private void KillIfRunning()
        {
            if (process.HasExited) return;
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        }

        private async Task AbortCandidateAsync()
        {
            KillIfRunning();
            await process.WaitForExitAsync();
            try { await errorDrain; }
            catch (IOException) when (process.HasExited) { } // The aborted child's stderr pipe closed.
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Only the explicit successful CloseAsync flushes a candidate. On any failed or cancelled
                // copy, stop the disposable process before recovery attempts to remove its staging directory.
                if (candidateWriter && !closed) await AbortCandidateAsync();
                else await CloseAsync(CancellationToken.None);
            }
            finally
            {
                await lifetimeCancellation.DisposeAsync();
                process.Dispose();
            }
        }
    }
}
