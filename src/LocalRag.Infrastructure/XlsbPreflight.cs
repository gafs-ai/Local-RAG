using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>Package-level gate before opening XLSB with Excel. Does not execute or interpret BIFF12.</summary>
public static class XlsbPreflight
{
    public static void Validate(byte[] bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > SpreadsheetDocuments.MaxPackageBytes) throw Error("excel_package_size");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) throw Error("excel_encrypted");
        try { ValidatePackage(bytes, token); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or ArgumentException or OverflowException)
        { throw Error("excel_invalid"); }
    }

    private static void ValidatePackage(byte[] bytes, CancellationToken token)
    {
        using var memory = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024) throw Error("excel_package_size");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Contains('%') ||
                name.Split('/').Any(p => p is ".." or ".") || !entries.TryAdd(name, entry)) throw Error("excel_invalid");
            InspectType(name);
        }
        XDocument Xml(string path)
        {
            if (!entries.TryGetValue(path, out var entry)) throw Error("excel_invalid");
            if (entry.Length > 4 * 1024 * 1024) throw Error("excel_package_size");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        var contentTypes = Xml("[Content_Types].xml");
        if (contentTypes.Root?.Name != types + "Types") throw Error("excel_invalid");
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in contentTypes.Root.Elements())
        {
            var contentType = (string?)entry.Attribute("ContentType") ?? "";
            InspectType(contentType);
            if (entry.Name == types + "Override")
            { if (!overrides.TryAdd((string?)entry.Attribute("PartName") ?? "", contentType)) throw Error("excel_invalid"); }
            else if (entry.Name == types + "Default")
            { if (!defaults.TryAdd((string?)entry.Attribute("Extension") ?? "", contentType)) throw Error("excel_invalid"); }
            else throw Error("excel_invalid");
        }
        var workbookType = overrides.GetValueOrDefault("/xl/workbook.bin") ?? defaults.GetValueOrDefault("bin");
        if (workbookType != "application/vnd.ms-excel.sheet.binary.macroEnabled.main" || !entries.ContainsKey("xl/workbook.bin") || !entries.ContainsKey("xl/_rels/workbook.bin.rels")) throw Error("excel_invalid");
        XNamespace rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        var officeDocument = false;
        foreach (var part in entries.Keys.Where(p => p.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var document = Xml(part);
            if (document.Root?.Name != rels + "Relationships") throw Error("excel_invalid");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relation in document.Root.Elements(rels + "Relationship"))
            {
                var type = (string?)relation.Attribute("Type") ?? "";
                var target = (string?)relation.Attribute("Target") ?? "";
                if (!ids.Add((string?)relation.Attribute("Id") ?? "") || target.Length == 0) throw Error("excel_invalid");
                InspectType(type);
                if (string.Equals((string?)relation.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                {
                    // A hyperlink is not followed by the converter. Other external relationships may refresh on open.
                    if (!type.EndsWith("/hyperlink", StringComparison.Ordinal)) throw Error("excel_external_data");
                    continue;
                }
                if (target.Contains(':') || target.Contains('\\') || target.Contains('%') || target.Contains('#') || target.Contains('?')) throw Error("excel_invalid");
                var marker = part.LastIndexOf("/_rels/", StringComparison.Ordinal);
                var parent = marker < 0 ? "" : part[..(marker + 1)];
                var resolved = new List<string>();
                foreach (var segment in (target.StartsWith('/') ? target[1..] : parent + target).Split('/'))
                {
                    if (segment is "" or ".") continue;
                    if (segment == "..") { if (resolved.Count == 0) throw Error("excel_invalid"); resolved.RemoveAt(resolved.Count - 1); }
                    else resolved.Add(segment);
                }
                var destination = string.Join('/', resolved);
                if (!entries.ContainsKey(destination)) throw Error("excel_invalid");
                if (part == "_rels/.rels" && type.EndsWith("/officeDocument", StringComparison.Ordinal))
                {
                    if (officeDocument || destination != "xl/workbook.bin") throw Error("excel_invalid");
                    officeDocument = true;
                }
            }
        }
        if (!officeDocument) throw Error("excel_invalid");
    }

    private static void InspectType(string value)
    {
        value = value.ToLowerInvariant();
        // AutomationSecurity=3 covers VBA, but not XLM; inspect both part types and relationships.
        // https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xlsb/13d6b358-8805-428f-a839-dc968d9d102c
        if (value.Contains("macrosheet") || value.Contains("intlmacrosheet")) throw Error("excel_macro_sheet");
        if (value.EndsWith("/package", StringComparison.Ordinal) || new[] { "connections", "querytable", "externallink", "pivotcache", "oleobject", "embeddings/", "activex", "webextension" }
            .Any(value.Contains)) throw Error("excel_external_data");
    }
    private static PipelineValidationException Error(string code) => new(code, PipelineErrors.Describe("read", code));
}
