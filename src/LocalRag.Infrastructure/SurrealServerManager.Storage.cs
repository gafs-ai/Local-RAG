using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record StorageMaintenanceResult(long PreviousBytes, long CurrentBytes, bool BackupPending);

public sealed partial class SurrealServerManager
{
    private const long FreeReserve = 1024L * 1024 * 1024;
    public bool HasCompactBackup => new StoreSwap(_directory).HasBackup;

    public async Task<StorageMaintenanceResult> CompactStorageAsync(DatabaseSettings settings, string source,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireOwnedStorage(settings);
            var swap = new StoreSwap(_directory);
            if (swap.Read() is not null) throw new InvalidOperationException("前回の整理で退避した旧DBが残っています。先に検索結果を確認してください。");
            var drive = new DriveInfo(Path.GetPathRoot(_directory)!);
            if (drive.AvailableFreeSpace < 2 * FreeReserve) throw new IOException("DB整理を開始するには2 GiB以上の空きが必要です。");
            var exe = await _installer.EnsureInstalledAsync(progress, token).ConfigureAwait(false);
            var credentials = Connection!.Credentials;
            progress?.Report("使用していない版とベクトルを整理しています…");
            await using (var repo = await TextRagRepository.OpenAsync(Connection, token, source, deferAutomaticRetention: true).ConfigureAwait(false))
                await repo.ReclaimUnusedDataAsync(token).ConfigureAwait(false);
            await StopForMaintenanceAsync().ConfigureAwait(false);
            var id = Guid.Empty;
            try
            {
                var originalSize = StoreSwap.Size(swap.Data);
                id = swap.Begin();
                using var copyToken = CancellationTokenSource.CreateLinkedTokenSource(token);
                var guard = GuardFreeSpaceAsync(drive, copyToken);
                try
                {
                    await SurrealStoreCompactor.CopyVerifiedAsync(exe, swap.Data, swap.Stage(id), credentials, progress, copyToken.Token).ConfigureAwait(false);
                    copyToken.Token.ThrowIfCancellationRequested();
                }
                finally { await copyToken.CancelAsync().ConfigureAwait(false); await guard.ConfigureAwait(false); }
                var newSize = StoreSwap.Size(swap.Stage(id));
                if (newSize <= 0) throw new InvalidDataException("整理後のDBが空です。");
                if (newSize >= originalSize)
                {
                    swap.Recover(); // No gain; keep serving the original datastore.
                    await EnsureRunningCoreAsync(settings with { AutoStartServer = true }, progress, token).ConfigureAwait(false);
                    return new(originalSize, originalSize, false);
                }
                token.ThrowIfCancellationRequested();
                swap.Activate(id);
                var status = await EnsureRunningCoreAsync(settings with { AutoStartServer = true }, progress, token, false).ConfigureAwait(false);
                if (!status.ManagedByApplication) throw new InvalidOperationException("切替後のDBの所有権を確認できません。");
                progress?.Report("整理後のDBでベクトル検索を確認しています…");
                await VerifyRestoredIndexesAsync(Connection!, token).ConfigureAwait(false);
                await WriteMaintenanceIdentityAsync(Connection!, id, token).ConfigureAwait(false);
                await VerifyMaintenanceIdentityAsync(Connection!, id, token).ConfigureAwait(false);
                swap.Accept(id);
                return new(originalSize, newSize, true);
            }
            catch
            {
                if (id != Guid.Empty && swap.NeedsRecovery)
                    await RecoverInterruptedCompactionAsync(settings.Port, await ReadOwnershipAsync(default).ConfigureAwait(false), default).ConfigureAwait(false);
                if (Connection is null)
                    await EnsureRunningCoreAsync(settings with { AutoStartServer = true }, progress, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseCompactBackupAsync(DatabaseSettings settings, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireOwnedStorage(settings);
            var swap = new StoreSwap(_directory);
            if (swap.Read() is not { Phase: "ready" } journal)
                throw new InvalidOperationException("確認済みの旧DBはありません。");
            await VerifyMaintenanceIdentityAsync(Connection!, journal.Id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => swap.ReleaseBackup(journal.Id), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void RequireOwnedStorage(DatabaseSettings settings)
    {
        if (settings.UsesExternalEndpoint || _managementLease is null || _owned is null || Connection?.Endpoint != settings.Endpoint ||
            _owned.Port != settings.Port || !IsOwnedProcessAlive(_owned) || !WindowsListenerOwner.IsOnlyOwner(settings.Port, _owned.ProcessId))
            throw new InvalidOperationException("このアプリが管理するローカルDBに接続しているときだけ実行できます。");
    }

    private static async Task WriteMaintenanceIdentityAsync(ServerConnection connection, Guid id, CancellationToken token)
    {
        await using var db = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        var r = await db.RawQuery("UPSERT text_source_metadata:storage_maintenance_identity SET maintenance_id=$identity RETURN NONE;",
            new Dictionary<string, object?> { ["identity"] = id.ToString("N") }, token).ConfigureAwait(false);
        r.EnsureAllOks();
    }

    internal static async Task VerifyMaintenanceIdentityAsync(ServerConnection connection, Guid expectedId, CancellationToken token)
    {
        await using var db = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        var r = await db.RawQuery("SELECT VALUE maintenance_id FROM text_source_metadata:storage_maintenance_identity;",
            cancellationToken: token).ConfigureAwait(false);
        r.EnsureAllOks();
        if (expectedId == Guid.Empty || r.GetValue<string[]>(0) is not [var actual] || actual != expectedId.ToString("N"))
            throw new InvalidDataException("接続中のDBと整理時に確認したDBの識別子が一致しません。退避した旧DBを保護します。");
    }

    private static async Task VerifyRestoredIndexesAsync(ServerConnection connection, CancellationToken token)
    {
        await using var db = await SurrealConnection.OpenAsync(connection, "application", token).ConfigureAwait(false);
        var spaces = await db.RawQuery("SELECT VALUE record::id(id) FROM text_space;", cancellationToken: token).ConfigureAwait(false);
        spaces.EnsureAllOks();
        foreach (var space in spaces.GetValue<string[]>(0) ?? [])
        {
            var table = TextRagRepository.Table(space);
            var sample = await db.RawQuery($"SELECT VALUE embedding FROM {table} WHERE active=true LIMIT 1;", cancellationToken: token).ConfigureAwait(false);
            sample.EnsureAllOks();
            if (sample.GetValue<float[][]>(0) is not { Length: > 0 } vectors) continue;
            var nearest = await db.RawQuery($"SELECT VALUE type::string(id) FROM {table} WHERE embedding <|1,64|> $query;",
                new Dictionary<string, object?> { ["query"] = vectors[0] }, token).ConfigureAwait(false);
            nearest.EnsureAllOks();
            if (nearest.GetValue<string[]>(0) is not { Length: 1 })
                throw new InvalidDataException("整理後のベクトル索引で検索結果を取得できませんでした。");
        }
    }
    private async Task StopForMaintenanceAsync()
    {
        await StopOwnedCoreAsync(CancellationToken.None).ConfigureAwait(false);
        Connection?.Dispose(); Connection = null; Status = null;
    }
    private async Task RecoverInterruptedCompactionAsync(int port, ServerOwnership? saved, CancellationToken token)
    {
        if (saved is not null && IsOwnedProcessAlive(saved))
        {
            if (saved.Port != port || !WindowsListenerOwner.IsOnlyOwner(port, saved.ProcessId))
                throw new InvalidOperationException("DB整理を回復するには、以前のDBの起動状態を確認してください。");
            _owned = saved;
            await StopForMaintenanceAsync().ConfigureAwait(false);
        }
        if (await IsPortOpenAsync(port, token).ConfigureAwait(false))
            throw new InvalidOperationException("別のDBが実行中のため整理の回復を延期します。");
        new StoreSwap(_directory).Recover();
    }
    private static async Task GuardFreeSpaceAsync(DriveInfo drive, CancellationTokenSource copy)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, copy.Token).ConfigureAwait(false);
                if (drive.AvailableFreeSpace >= FreeReserve) continue;
                throw new IOException("空き容量が1 GiBを下回ったためDB整理を中止しました。");
            }
        }
        catch (OperationCanceledException) when (copy.IsCancellationRequested) { }
        catch { await copy.CancelAsync().ConfigureAwait(false); throw; }
    }
}
