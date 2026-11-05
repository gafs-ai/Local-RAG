using System.Text;
using System.Text.RegularExpressions;

namespace LocalRag.Core;

/// <summary>Bounded shell-style path patterns, never arbitrary regular expressions.</summary>
public sealed record IgnorePattern(bool Negated, bool DirectoryOnly, Regex Expression)
{
    public bool Matches(string relativePath, bool directory) => (!DirectoryOnly || directory) && Expression.IsMatch(relativePath);

    public static IReadOnlyList<IgnorePattern> Parse(IReadOnlyList<string> lines, bool docker = false)
    {
        if (lines.Count > 2048 || lines.Any(l => l is null) || lines.Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1) > 65536)
            throw new InvalidDataException("ignore設定は2048行・64KiB以内で指定してください。");
        var result = new List<IgnorePattern>();
        for (var lineNumber = 0; lineNumber < lines.Count; lineNumber++)
        {
            try
            {
                var line = lines[lineNumber];
                if (line.Length > 1024 || line.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new FormatException();
                if (line.StartsWith('#')) continue;
                if (docker) line = line.Trim();
                else
                {
                    while (line.EndsWith(' '))
                    {
                        var slashes = 0;
                        for (var k = line.Length - 2; k >= 0 && line[k] == '\\'; k--) slashes++;
                        if (slashes % 2 == 1) break;
                        line = line[..^1];
                    }
                }
                if (line.Length == 0) continue;
                var negated = line.StartsWith('!');
                if (negated) line = line[1..];
                if (line.Length == 0) throw new FormatException();
                var directoryOnly = !docker && line.EndsWith('/');
                var anchored = docker || line.StartsWith('/');
                line = line.Trim('/');
                if (docker)
                {
                    var parts = new List<string>();
                    foreach (var part in line.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part == ".") continue;
                        if (part == "..") { if (parts.Count == 0) throw new FormatException(); parts.RemoveAt(parts.Count - 1); }
                        else parts.Add(part);
                    }
                    line = string.Join('/', parts);
                    if (line.Length == 0) continue;
                }
                // Git-style patterns containing literal ./ or ../ components simply do not
                // match our normalized relative paths. They must not invalidate every other
                // rule in the file. Docker's path cleaning is handled separately above.
                if (line.Length == 0) throw new FormatException();
                anchored |= line.Contains('/');
                var regex = (anchored ? "^" : "^(?:[^/]+/)*") + Glob(line) + "$";
                result.Add(new(negated, directoryOnly, new Regex(regex,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
            {
                throw new InvalidDataException($"ignore設定の{lineNumber + 1}行目の構文または長さが不正です。", ex);
            }
        }
        return result;
    }

    private static string Glob(string pattern)
    {
        var result = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (++i == pattern.Length) throw new FormatException();
                result.Append(Regex.Escape(pattern[i].ToString()));
            }
            else if (ch == '*')
            {
                var first = i;
                while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                if (i > first && (first == 0 || pattern[first - 1] == '/') && (i + 1 == pattern.Length || pattern[i + 1] == '/'))
                {
                    if (i + 1 < pattern.Length) { result.Append("(?:[^/]+/)*"); i++; }
                    else result.Append(".*");
                }
                else result.Append("[^/]*");
            }
            else if (ch == '?') result.Append("[^/]");
            else if (ch == '[')
            {
                result.Append('[');
                var start = ++i;
                if (i < pattern.Length && pattern[i] is '!' or '^') { result.Append("^/"); i++; }
                var characters = 0;
                while (i < pattern.Length && (pattern[i] != ']' || characters == 0))
                {
                    var value = pattern[i];
                    if (value is '/' or '[') throw new FormatException();
                    if (value == '\\')
                    {
                        if (++i == pattern.Length || pattern[i] == '/') throw new FormatException();
                        if (pattern[i] is '-' or ']' or '[' or '^' or '\\') result.Append('\\');
                        result.Append(pattern[i]);
                    }
                    else if (value is ']' or '^') result.Append('\\').Append(value);
                    else result.Append(value);
                    characters++; i++;
                }
                if (i == pattern.Length || i == start || characters == 0) throw new FormatException();
                result.Append(']');
            }
            else result.Append(Regex.Escape(ch.ToString()));
        }
        return result.ToString();
    }
}
