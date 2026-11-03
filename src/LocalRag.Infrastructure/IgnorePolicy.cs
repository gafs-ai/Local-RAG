using System.Text;
using System.Text.RegularExpressions;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>A short-lived policy snapshot. Create a fresh instance at every disclosure check.</summary>
public sealed class IgnorePolicy(IReadOnlyList<WatchedFolder> folders, IReadOnlyList<string> globalPatterns)
{
    private static readonly string[] Names = [".gitignore", ".npmignore", ".ragignore", ".dockerignore"];
    private readonly IReadOnlyList<IgnorePattern> _global = IgnorePattern.Parse(globalPatterns);
    private readonly Dictionary<string, IReadOnlyList<IgnorePattern>[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsIgnored(string path, bool directory, bool traversal = false, bool excludeDotHiddenEntries = false)
    {
        try { return Evaluate(Path.GetFullPath(path), directory, traversal, excludeDotHiddenEntries); }
        catch (RegexMatchTimeoutException ex) { throw new InvalidDataException("ignoreパターンの判定が完了しませんでした。", ex); }
    }

    private bool Evaluate(string path, bool directory, bool traversal, bool excludeDotHiddenEntries)
    {
        // The outermost configured root supplies the scope even when a nested root is also selected.
        var boundary = folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)))
            .Where(root => TextFiles.Within(path, root)).OrderBy(root => root.Length).FirstOrDefault();
        var ignored = excludeDotHiddenEntries && TextFiles.IsDotHidden(path);
        if (boundary is null) return ignored;
        var relative = Path.GetRelativePath(boundary, path).Replace('\\', '/');
        if (relative == ".") { _ = Load(boundary); return directory && traversal ? false : ignored; }
        var parts = relative.Split('/');
        var counts = new int[Names.Length];
        var included = false;
        void Apply(IReadOnlyList<IgnorePattern> rules, string local)
        {
            foreach (var rule in rules)
            {
                if (!MatchesPathOrAncestor(rule, local, directory)) continue;
                if (rule.Negated) included = true;
                else ignored = true;
            }
        }
        Apply(_global, relative);
        var current = boundary;
        for (var i = 0; i < parts.Length; i++)
        {
            var rules = Load(current);
            for (var family = 0; family < Names.Length; family++)
            {
                counts[family] += rules[family].Count;
                if (counts[family] > 8192) throw new InvalidDataException("継承するignoreパターンが多すぎます。");
                Apply(rules[family], Relative(current, path));
            }
            current = Path.Combine(current, parts[i]);
        }
        // A descendant ignore file can contain an exception even when none is known yet.
        // Always explore directories; only document eligibility is filtered here.
        return !(directory && traversal) && ignored && !included;
    }

    private static string Relative(string scope, string path) => Path.GetRelativePath(scope, path).Replace('\\', '/');
    private static bool MatchesPathOrAncestor(IgnorePattern rule, string path, bool directory)
    {
        if (rule.Matches(path, directory)) return true;
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
            if (rule.Matches(path[..index], true)) return true;
        return false;
    }

    private IReadOnlyList<IgnorePattern>[] Load(string directory)
    {
        if (_cache.TryGetValue(directory, out var cached)) return cached;
        var result = new IReadOnlyList<IgnorePattern>[Names.Length];
        for (var i = 0; i < Names.Length; i++)
        {
            var path = Path.Combine(directory, Names[i]);
            try
            {
                // File.Exists would hide permission failures. Only absence means an empty rule set.
                TextFiles.RejectLinks(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 65536) throw new InvalidDataException("ignoreファイルは64KiB以内で指定してください。");
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new InvalidDataException("ignoreファイルが読み取り中に変化しました。");
                var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                result[i] = IgnorePattern.Parse(text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray(), docker: i == 3);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { result[i] = []; }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("ignoreファイルはUTF-8で保存してください。", ex); }
        }
        _cache[directory] = result;
        return result;
    }
}
