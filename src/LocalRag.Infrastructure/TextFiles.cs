using System.Security.Cryptography;
using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record TextSnapshot(string Path, string Hash, string Text);
public sealed record TextScan(IReadOnlyList<string> Files, IReadOnlyList<WatchedFolder> CompletedRoots, int FailedRoots);

public static class TextFiles
{
    // Normalize . and .. first; a hidden ancestor also excludes explicitly selected descendants.
    public static bool IsDotHidden(string path) => Path.GetFullPath(path)
        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.StartsWith('.'));
    public static bool Supported(string path) => new[] { ".txt", ".md" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool Within(string path, string root) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool InFolder(string path, WatchedFolder folder) => folder.Recursive ? Within(path, folder.Path) :
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path)), StringComparison.OrdinalIgnoreCase);

    public static void RejectLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("リンク先のファイルは取り込みません。");
    }

    public static TextScan Scan(IReadOnlyList<WatchedFolder> folders, string dataDirectory, CancellationToken token,
        IReadOnlyList<string>? extensions = null, bool excludeDotHiddenEntries = true, IReadOnlyList<string>? globalIgnorePatterns = null)
    {
        var ignore = new IgnorePolicy(folders, globalIgnorePatterns ?? []);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = new List<WatchedFolder>();
        var failed = 0;
        foreach (var root in folders)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RejectLinks(root.Path);
                if (ignore.IsIgnored(root.Path, directory: true, traversal: true)) { complete.Add(root); continue; }
                var pending = new Stack<string>();
                pending.Push(Path.GetFullPath(root.Path));
                while (pending.TryPop(out var directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (Within(directory, dataDirectory)) continue;
                    // Explicit traversal: do not follow junctions and do not hide permission failures.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || Within(entry, dataDirectory)) continue;
                        if (ignore.IsIgnored(entry, (attributes & FileAttributes.Directory) != 0,
                            traversal: true, excludeDotHiddenEntries: excludeDotHiddenEntries)) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { if (root.Recursive) pending.Push(entry); }
                        else if ((extensions is null ? Supported(entry) : extensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)) &&
                            !Path.GetFileName(entry).StartsWith("~$", StringComparison.Ordinal)) files.Add(Path.GetFullPath(entry));
                    }
                }
                complete.Add(root);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { failed++; }
        }
        return new(files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), complete, failed);
    }

    public static async Task<TextSnapshot> ReadAsync(string path, int maxCharacters, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length > 256 * 1024) throw new InvalidDataException("テキストファイルが256KiBの上限を超えています。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.Position != stream.Length) throw new IOException("読み取り中にファイルが変更されました。");
        var start = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var text = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        if (text.Length > maxCharacters || text.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("空文書、バイナリ、またはテキスト文字数の上限超過です。");
        return new(Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), text);
    }

    public static List<TextParagraph> Paragraphs(string text, int maxChunkCharacters)
    {
        // Preserve the exact source substring. A long paragraph is held, never silently cut.
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(?:[^\r\n]+(?:\r?\n(?!\r?\n)[^\r\n]+)*)");
        var result = matches.Select((match, index) => new TextParagraph(index + 1, match.Value)).Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
        if (result.Count == 0 || result.Any(p => p.Text.Length > maxChunkCharacters))
            throw new InvalidDataException("1段落がチャンク上限を超えています。段落区切りまたは上限を調整してください。");
        return result;
    }
}
