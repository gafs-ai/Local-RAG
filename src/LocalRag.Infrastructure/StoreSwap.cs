using System.Text.Json;

namespace LocalRag.Infrastructure;

// All paths in the journal are derived locally; no path from a journal is used as a deletion target.
internal sealed class StoreSwap(string serverDirectory)
{
    internal sealed record Journal(Guid Id, string Phase);
    private readonly string _root = Path.GetFullPath(serverDirectory);
    public string Data => Path.Combine(_root, "data");
    private string Marker => Path.Combine(_root, "storage-maintenance-v1.json");
    internal string Stage(Guid id) => Path.Combine(_root, "compact-new-" + id.ToString("N"));
    internal string Backup(Guid id) => Path.Combine(_root, "compact-old-" + id.ToString("N"));
    internal Journal? Read()
    {
        if (!File.Exists(Marker)) return null;
        CheckPath(Marker);
        var j = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(Marker));
        if (j is null || j.Id == Guid.Empty || j.Phase is not ("copy" or "swap" or "ready"))
            throw new InvalidDataException("DB整理の回復情報が不正です。ファイルは削除していません。");
        return j;
    }
    internal bool NeedsRecovery => Read() is { Phase: "copy" or "swap" };
    internal bool HasBackup => Read() is { Phase: "ready" };
    internal void CheckForUnjournaledCopy()
    {
        foreach (var entry in Directory.EnumerateDirectories(_root, "compact-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            if ((name.StartsWith("compact-old-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compact-new-", StringComparison.OrdinalIgnoreCase)) &&
                Guid.TryParseExact(name["compact-old-".Length..], "N", out _))
                throw new InvalidDataException("DB整理の作業フォルダが残っていますが、回復情報が見つかりません。DBと退避データを保護するため起動を停止しました。");
        }
    }
    private static string Witness(string directory) => Path.Combine(directory, ".localrag-compact-id");
    private static void CheckManifest(string directory)
    {
        var manifest = Path.Combine(directory, "manifest");
        CheckPath(manifest);
        if (!new DirectoryInfo(manifest).EnumerateFiles("*.manifest").Any(f => f.Length > 0))
            throw new InvalidDataException("DBのマニフェストを確認できません。退避データを保持して整理を停止しました。");
    }
    internal void VerifyActive(Guid id)
    {
        CheckManifest(Data);
        if (!MatchesWitness(Data, id))
            throw new InvalidDataException("整理後のDBの識別情報が一致しません。退避データを保持します。");
    }
    private static bool MatchesWitness(string dir, Guid id)
    {
        var path = Witness(dir);
        if (!File.Exists(path)) return false;
        CheckPath(path);
        return new FileInfo(path).Length == 16 && File.ReadAllBytes(path).AsSpan().SequenceEqual(id.ToByteArray());
    }
    private void Write(Journal j, bool replace)
    {
        CheckPath(_root);
        var tmp = Marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, j); f.Flush(true); }
            File.Move(tmp, Marker, overwrite: replace);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    internal static void CheckPath(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            CheckEntry(part);
    }
    private static void CheckEntry(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンクを含む保存先はDB整理の対象外です。");
    }
    internal static long Size(string directory)
    {
        CheckPath(directory);
        long size = 0;
        void Visit(string dir)
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                CheckEntry(f.FullName);
                if (f is DirectoryInfo child) Visit(child.FullName);
                else size = checked(size + ((FileInfo)f).Length);
            }
        }
        Visit(directory); return size;
    }
    internal Guid Begin()
    {
        CheckManifest(Data);
        if (Read() is not null) throw new InvalidOperationException("前回のDB整理がまだ完了していません。");
        var id = Guid.NewGuid();
        if (Path.Exists(Stage(id)) || Path.Exists(Backup(id))) throw new IOException("作業先がすでに存在します。");
        Write(new(id, "copy"), false);
        return id;
    }
    internal void Activate(Guid id)
    {
        Expect(id, "copy");
        CheckManifest(Data); CheckManifest(Stage(id));
        using (var f = new FileStream(Witness(Stage(id)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            f.Write(id.ToByteArray());
            f.Flush(true);
        }
        Write(new(id, "swap"), true);
        Directory.Move(Data, Backup(id));
        Directory.Move(Stage(id), Data);
    }
    internal void Accept(Guid id)
    {
        Expect(id, "swap");
        VerifyActive(id); CheckManifest(Backup(id));
        Write(new(id, "ready"), true);
    }
    // Called only with the application management lease and while no server owns these directories.
    internal void Recover()
    {
        var j = Read();
        if (j is null || j.Phase == "ready") return;
        var stage = Stage(j.Id); var old = Backup(j.Id);
        if (Directory.Exists(old))
        {
            if (j.Phase != "swap") throw new InvalidDataException("予期しない退避DBを検出しました。");
            CheckManifest(old);
            if (Directory.Exists(Data))
            {
                VerifyActive(j.Id);
                Directory.Move(Data, stage); // Fail closed if both names are already in use.
            }
            Directory.Move(old, Data);
        }
        else if (j.Phase == "swap" && MatchesWitness(Data, j.Id))
            throw new InvalidDataException("切替途中の元DBを特定できないため、回復を停止しました。");
        CheckManifest(Data); // Never launch an empty DB after an incomplete rename.
        DeleteOwnedTree(stage);
        File.Delete(Marker);
    }
    internal void ReleaseBackup(Guid expectedId)
    {
        Expect(expectedId, "ready");
        VerifyActive(expectedId);
        DeleteOwnedTree(Backup(expectedId));
        File.Delete(Marker);
    }
    private void Expect(Guid id, string phase)
    {
        if (Read() != new Journal(id, phase)) throw new InvalidOperationException("DB整理の状態が変わりました。");
    }
    private static void DeleteOwnedTree(string dir)
    {
        if (!Path.Exists(dir)) return;
        // Validate every descendant first, including links, before starting deletion.
        _ = Size(dir);
        Directory.Delete(dir, recursive: true);
    }
}
