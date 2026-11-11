using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record WordDocument(string Markdown, string DisclosureText, List<WordBlock> Blocks, List<string> Notices);
public sealed record DocumentSnapshot(string Hash, string Text, List<string> Notices, WordDocument? Word = null, PdfAssessment? Pdf = null);

/// <summary>Read-only OOXML conversion. No macros, fields, external relationships or embedded objects are executed.</summary>
public static class WordDocuments
{
    public const int MaxPackageBytes = 64 * 1024 * 1024;
    private const string Transitional = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Strict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private static readonly Regex Label = new(@"極秘|丸秘|マル秘|社外秘|機密|CONFIDENTIAL|\bSECRET\b|秘密|보안|대외비|기밀|机密|保密", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool HasLabel(string text) => Label.IsMatch(text);

    public static WordDocument Convert(byte[] bytes, WordSettings options, int maxCharacters, CancellationToken token = default)
    {
        try { return ConvertCore(bytes, options, maxCharacters, token); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or ArgumentException or OverflowException)
        { throw new PipelineValidationException("word_invalid", "Wordファイルの構造を読み取れません。"); }
    }

    private static WordDocument ConvertCore(byte[] bytes, WordSettings options, int maxCharacters, CancellationToken token)
    {
        if (bytes.Length > MaxPackageBytes) throw new PipelineValidationException("word_package_size", "Wordファイルの上限を超えています。");
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
            throw new PipelineValidationException("word_package_size", "展開後のWordファイルが上限を超えています。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Duplicate part.");
        XDocument? Xml(string name)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(name, out var entry)) return null;
            if (entry.Length > 16 * 1024 * 1024) throw new PipelineValidationException("word_package_size", "XML部分が上限を超えています。");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024, MaxCharactersFromEntities = 0 });
            return XDocument.Load(reader);
        }
        var doc = Xml("word/document.xml") ?? throw new InvalidDataException("Missing document.");
        XNamespace w = doc.Root?.Name.Namespace ?? XNamespace.None;
        if (w.NamespaceName is not (Transitional or Strict)) throw new InvalidDataException("Unsupported namespace.");
        var body = doc.Root!.Element(w + "body") ?? throw new InvalidDataException("Missing body.");
        if (doc.Descendants().Any(e => e.Name.LocalName is "altChunk" or "object" or "oMath" or "oMathPara" or "sym"))
            throw new PipelineValidationException("word_content_unsupported", "埋め込み文書・数式・特殊記号を含むため保留しました。");
        var notices = new HashSet<string> { "word_converted" };
        if (doc.Descendants(w + "del").Any() || doc.Descendants(w + "ins").Any() || doc.Descendants(w + "moveFrom").Any()) notices.Add("word_revisions");
        if (doc.Descendants(w + "commentRangeStart").Any()) notices.Add("word_comments");
        var styles = Xml("word/styles.xml")?.Root?.Elements(w + "style").ToDictionary(e => (string?)e.Attribute(w + "styleId") ?? "", StringComparer.Ordinal) ?? [];
        XElement? StyleProperty(string? id, string container, string property)
        {
            var seen = new HashSet<string>();
            while (id is not null && seen.Add(id) && styles.TryGetValue(id, out var style))
            {
                if (style.Element(w + container)?.Element(w + property) is { } value) return value;
                id = (string?)style.Element(w + "basedOn")?.Attribute(w + "val");
            }
            return null;
        }
        string? Value(XElement? element) => (string?)element?.Attribute(w + "val");
        bool Enabled(XElement? element) => element is not null && Value(element) is not ("0" or "false" or "off");
        var numbering = Xml("word/numbering.xml");
        var counters = new Dictionary<(string, int), int>();
        var notes = new Dictionary<(string, string), XElement>();
        foreach (var part in new[] { "footnote", "endnote" })
            foreach (var note in Xml($"word/{part}s.xml")?.Root?.Elements(w + part) ?? [])
                if (note.Attribute(w + "id") is { } id) notes.TryAdd((part, id.Value), note);
        var referencedNotes = new HashSet<(string, string)>();
        string Inline(XElement element, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64) throw new InvalidDataException("Nested content.");
            if (element.Name == w + "del" || element.Name == w + "moveFrom" || element.Name == w + "instrText" || element.Name == w + "txbxContent") return "";
            if (element.Name == w + "t") return element.Value;
            if (element.Name == w + "tab") return "\t";
            if (element.Name == w + "noBreakHyphen") return "‑";
            if (element.Name == w + "softHyphen") return "";
            if (element.Name == w + "br" || element.Name == w + "cr") return "\n";
            if (element.Name == w + "footnoteReference" || element.Name == w + "endnoteReference")
            {
                var kind = element.Name.LocalName.Replace("Reference", "", StringComparison.Ordinal);
                var id = (string?)element.Attribute(w + "id") ?? "";
                referencedNotes.Add((kind, id)); return $" [{kind}:{id}]";
            }
            if (element.Name == w + "drawing" || element.Name == w + "pict")
            {
                notices.Add("word_images");
                var alt = element.Descendants().SelectMany(e => e.Attributes()).FirstOrDefault(a => a.Name.LocalName == "descr")?.Value;
                return "［画像" + (string.IsNullOrWhiteSpace(alt) ? "" : ": " + alt) + "］";
            }
            var builder = new StringBuilder();
            foreach (var child in element.Elements())
            {
                builder.Append(Inline(child, depth + 1));
                if (builder.Length > maxCharacters) throw new PipelineValidationException("document_size", "文書文字数上限を超えています。");
            }
            return builder.ToString();
        }
        string Clean(string value)
        {
            var lines = value.Replace('\u00a0', ' ').Replace("\r", "", StringComparison.Ordinal).Split('\n');
            return options.JoinSoftLineBreaks ? JoinLines(lines) : string.Join("\n", lines).Trim();
        }
        int total = 0;
        var blocks = new List<WordBlock>();
        void Add(string text, int level = 0, bool bold = false, bool protect = false)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            total += text.Length + 10;
            if (total > maxCharacters) throw new PipelineValidationException("document_size", "変換後の文書文字数上限を超えています。");
            blocks.Add(new(blocks.Count + 1, text, level, bold, protect || HasLabel(text)));
        }
        string Escape(string text) => Regex.Replace(text, @"([\\`*{}\[\]<>#|])", @"\$1", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        void Paragraph(XElement p, bool protect = false)
        {
            var plain = Clean(Inline(p));
            if (plain.Length == 0) return;
            var props = p.Element(w + "pPr");
            var styleId = Value(props?.Element(w + "pStyle"));
            var outline = Value(props?.Element(w + "outlineLvl") ?? StyleProperty(styleId, "pPr", "outlineLvl"));
            var level = int.TryParse(outline, out var n) && n is >= 0 and <= 8 ? Math.Min(6, n + 1) : 0;
            if (outline is null && styleId is not null && Regex.Match(styleId, @"^(?:Heading|見出し)\s*([1-6])$", RegexOptions.IgnoreCase) is { Success: true } match)
                level = int.Parse(match.Groups[1].Value);
            var runs = p.Descendants(w + "r").Where(r => !r.Ancestors(w + "del").Any() && r.Descendants(w + "t").Any(t => !string.IsNullOrWhiteSpace(t.Value))).ToArray();
            var bold = runs.Length > 0 && runs.All(r => Enabled(r.Element(w + "rPr")?.Element(w + "b") ??
                StyleProperty(Value(r.Element(w + "rPr")?.Element(w + "rStyle")), "rPr", "b") ?? StyleProperty(styleId, "rPr", "b")));
            var num = props?.Element(w + "numPr") ?? StyleProperty(styleId, "pPr", "numPr");
            var numId = Value(num?.Element(w + "numId"));
            if (numId is not null && numId != "0")
            {
                var indent = int.TryParse(Value(num?.Element(w + "ilvl")), out var ilvl) ? Math.Clamp(ilvl, 0, 8) : 0;
                var abstractId = Value(numbering?.Root?.Elements(w + "num").FirstOrDefault(e => (string?)e.Attribute(w + "numId") == numId)?.Element(w + "abstractNumId"));
                var definition = numbering?.Root?.Elements(w + "abstractNum").FirstOrDefault(e => (string?)e.Attribute(w + "abstractNumId") == abstractId)?
                    .Elements(w + "lvl").FirstOrDefault(e => (string?)e.Attribute(w + "ilvl") == indent.ToString());
                var bullet = Value(definition?.Element(w + "numFmt")) == "bullet";
                var start = int.TryParse(Value(definition?.Element(w + "start")), out var initial) ? initial : 1;
                var count = counters.TryGetValue((numId, indent), out var current) ? current + 1 : start;
                counters[(numId, indent)] = count;
                foreach (var key in counters.Keys.Where(k => k.Item1 == numId && k.Item2 > indent).ToArray()) counters.Remove(key);
                Add(new string(' ', indent * 2) + (bullet ? "- " : count + ". ") + Escape(plain), level, protect: true);
            }
            else
            {
                if (level == 0 && options.InferHeadings && bold && plain.Length <= 100 && !Regex.IsMatch(plain, @"[。！？.!?：:;；]$"))
                { level = 2; notices.Add("word_heading_inferred"); }
                Add(Escape(plain), level, bold, protect);
            }
            foreach (var textBox in p.Descendants(w + "txbxContent")) Walk(textBox, true);
        }
        void Walk(XElement container, bool protect = false, int depth = 0)
        {
            if (depth > 64) throw new InvalidDataException("Nested blocks.");
            foreach (var element in container.Elements())
            {
                token.ThrowIfCancellationRequested();
                if (element.Name == w + "p") Paragraph(element, protect);
                else if (element.Name == w + "tbl")
                {
                    var rows = element.Elements(w + "tr").Select(row => row.Elements(w + "tc").Select(cell =>
                        string.Join("<br>", cell.Descendants(w + "p").Select(p => Escape(Clean(Inline(p))).Replace("\n", "<br>", StringComparison.Ordinal)))).ToArray()).ToArray();
                    if (rows.Length == 0) continue;
                    var columns = rows.Max(r => r.Length);
                    var table = new StringBuilder();
                    for (var i = 0; i < rows.Length; i++)
                    {
                        table.AppendLine("| " + string.Join(" | ", rows[i].Concat(Enumerable.Repeat("", columns - rows[i].Length))) + " |");
                        if (i == 0) table.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |");
                    }
                    if (element.Descendants(w + "gridSpan").Any() || element.Descendants(w + "vMerge").Any()) notices.Add("word_table_merged");
                    Add(table.ToString(), protect: true);
                }
                else if (element.Name == w + "sdt")
                { if (element.Element(w + "sdtContent") is { } content) Walk(content, protect, depth + 1); }
                else if (element.Name.LocalName is "ins" or "moveTo" or "customXml") Walk(element, protect, depth + 1);
                else if (element.Name.LocalName is "AlternateContent")
                    throw new PipelineValidationException("word_content_unsupported", "代替コンテンツを含むため保留しました。");
            }
        }
        // Include all header/footer text in the independent disclosure source, even when it is not a known label.
        var relationships = Xml("word/_rels/document.xml.rels")?.Root?.Elements().ToArray() ?? [];
        var headers = new List<string>(); var footers = new List<string>();
        var readBoundaryParts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "header", "footer" })
            foreach (var reference in body.Descendants(w + kind + "Reference"))
            {
                var id = reference.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                var rel = relationships.SingleOrDefault(r => (string?)r.Attribute("Id") == id);
                if (rel is null || (string?)rel.Attribute("TargetMode") == "External") throw new InvalidDataException("Invalid header reference.");
                var target = (string?)rel.Attribute("Target") ?? "";
                var resolved = new Uri(new Uri("https://package.invalid/word/document.xml"), target);
                if (resolved.Host != "package.invalid") throw new InvalidDataException("Invalid part URI.");
                var partName = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
                if (!readBoundaryParts.Add(kind + ":" + partName)) continue;
                var part = Xml(partName) ?? throw new InvalidDataException("Missing header.");
                var text = string.Join("\n", part.Descendants(w + "p").Select(p => Clean(Inline(p))).Where(t => t.Length > 0));
                var list = kind == "header" ? headers : footers;
                if (text.Length > 0 && !list.Contains(text, StringComparer.Ordinal)) list.Add(text);
            }
        var labels = headers.Concat(footers).SelectMany(t => t.Split('\n')).Where(HasLabel).Distinct(StringComparer.Ordinal).ToArray();
        // One label per line, at the very beginning, without extra Markdown lines before it.
        if (labels.Length > 0) Add(string.Join("\n", labels.Select(Escape)), protect: true);
        Walk(body);
        foreach (var key in referencedNotes.ToArray())
            if (notes.TryGetValue(key, out var note)) { Add($"[{key.Item1}:{key.Item2}]", protect: true); Walk(note, true); }
            else throw new InvalidDataException("Missing note.");
        var markdown = string.Join("\n\n", blocks.Select(Render));
        var rawBody = string.Join("\n\n", body.Descendants(w + "p").Where(p => !p.Ancestors(w + "del").Any() && !p.Ancestors(w + "moveFrom").Any())
            .Select(p => Clean(Inline(p))).Where(t => t.Length > 0));
        // A normal multi-line header must not push a leading body label outside the default inspection window.
        var disclosureLabels = labels.Concat(rawBody.Split('\n').Take(5).Where(HasLabel)).Distinct(StringComparer.Ordinal);
        var disclosure = string.Join("\n", disclosureLabels) + "\n" + string.Join("\n", headers) + "\n" + rawBody + "\n" +
            string.Join("\n", referencedNotes.Where(notes.ContainsKey).Select(k => Clean(Inline(notes[k])))) + "\n" + string.Join("\n", footers);
        if (markdown.Length > maxCharacters || disclosure.Length > maxCharacters)
            throw new PipelineValidationException("document_size", "変換後の文書文字数上限を超えています。");
        return new(markdown, disclosure.Trim('\n'), blocks, notices.ToList());
    }

    public static string Render(WordBlock block) => (block.HeadingLevel > 0 ? new string('#', block.HeadingLevel) + " " : "") + block.Text;
    public static string JoinLines(IEnumerable<string> lines)
    {
        var output = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            if (output.Length > 0 && !IsCjk(output[^1]) && !IsCjk(line[0])) output.Append(' ');
            output.Append(line);
        }
        return output.ToString();
    }
    private static bool IsCjk(char c) => c is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken token)
    {
        TextFiles.RejectLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (file.Length > MaxPackageBytes) throw new PipelineValidationException("word_package_size", "Wordファイルは64MiB以内にしてください。");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
}

public static class DocumentReader
{
    public static async Task<DocumentSnapshot> ReadAsync(string path, TextRagSettings settings, string dataDirectory, CancellationToken token,
        TextDocumentState? previous = null, ProcessingStep? readStep = null)
    {
        if (!DocumentInputs.IsConverted(settings.SourceMode))
        {
            var text = await TextFiles.ReadAsync(path, settings.MaxDocumentCharacters, token, settings.EncodingMode, allowEmpty: true).ConfigureAwait(false);
            return new(text.Hash, text.Text, [$"encoding_{(text.EncodingAutomatic ? "auto" : "manual")}_{text.EncodingMode}"]);
        }
        if (settings.SourceMode == SourceCodeMode.Pdf && !DocumentInputs.IsPdf(path)) throw new PipelineValidationException("pdf_extension", "PDFの拡張子を指定してください。");
        if (settings.SourceMode == SourceCodeMode.Word && !DocumentInputs.IsWord(path)) throw new PipelineValidationException("word_extension", "Wordの拡張子を指定してください。");
        var bytes = settings.SourceMode == SourceCodeMode.Pdf ? await PdfDocuments.ReadBytesAsync(path, token).ConfigureAwait(false)
            : await WordDocuments.ReadBytesAsync(path, token).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (previous is { WordDisclosureText: not null, WordSourceBlocks: not null } && previous.SourceHash == hash &&
            previous.Processing?.Stages.TryGetValue("read", out var receipt) == true && receipt.Step.Id == readStep?.Id &&
            previous.Original.Length <= settings.MaxDocumentCharacters && previous.WordDisclosureText.Length <= settings.MaxDocumentCharacters)
        {
            var cached = new WordDocument(previous.Original, previous.WordDisclosureText, previous.WordSourceBlocks,
                previous.ProcessingNotices.Where(n => n.StartsWith(settings.SourceMode == SourceCodeMode.Pdf ? "pdf_" : "word_", StringComparison.Ordinal) &&
                    !n.Contains("_repair_", StringComparison.Ordinal)).ToList());
            return new(hash, cached.Markdown, cached.Notices, cached, previous.PdfAssessment);
        }
        if (settings.SourceMode == SourceCodeMode.Pdf) return PdfDocuments.Convert(bytes, settings, token);
        var converted = Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase)
            ? await LegacyWordConversion.ConvertAsync(bytes, dataDirectory, token).ConfigureAwait(false) : bytes;
        var word = WordDocuments.Convert(converted, settings.Word, settings.MaxDocumentCharacters, token);
        return new(hash, word.Markdown, word.Notices, word);
    }

    public static async Task<string> HashAsync(string path, TextRagSettings settings, CancellationToken token)
    {
        if (!DocumentInputs.IsConverted(settings.SourceMode))
            return (await TextFiles.ReadAsync(path, settings.MaxDocumentCharacters, token, settings.EncodingMode, allowEmpty: true).ConfigureAwait(false)).Hash;
        var bytes = settings.SourceMode == SourceCodeMode.Pdf ? await PdfDocuments.ReadBytesAsync(path, token).ConfigureAwait(false)
            : await WordDocuments.ReadBytesAsync(path, token).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
