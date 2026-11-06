using LocalRag.Core;

namespace LocalRag.Infrastructure;

public sealed record CodeSelection(SourceCodeUnit Unit, string Content, string Mode);

public static class CodeHierarchy
{
    public static string Description(SourceCodeUnit unit) => string.Join("\n\n", unit.Descriptions);
    public static string Title(SourceCodeUnit unit) => $"{unit.Kind}: {unit.Name}（{unit.StartLine}–{unit.EndLine}行）";
    public static string Summary(SourceCodeUnit unit) => Title(unit) + "\n説明（AI生成・原文との照合が必要）：\n" +
        (unit.Descriptions.Count == 0 ? "説明未作成" : Description(unit));
    public static string Full(SourceCodeUnit unit) => Summary(unit) + "\n\nソースコード：\n" + unit.Code;

    public static string Outline(SourceCodeUnit unit, IReadOnlyList<SourceCodeUnit> units)
    {
        var children = units.Where(u => u.ContainerId == unit.Id).ToArray();
        var parts = new List<string>();
        if (unit.Variables.Count > 0) parts.Add("変数一覧：\n" + string.Join("\n", unit.Variables));
        if (children.Length > 0) parts.Add("直下のクラス・関数一覧と説明（AI生成・説明未作成の単位も含む）：\n" +
            string.Join("\n\n", children.Select(Summary)));
        return string.Join("\n\n", parts);
    }

    public static CodeSelection Select(SourceCodeUnit hit, IReadOnlyList<SourceCodeUnit> units, int maximum)
    {
        if (maximum < 500) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (hit.Kind == "function")
        {
            var container = hit.ContainerId;
            while (container is not null)
            {
                var ancestor = units.Single(u => u.Id == container);
                if (ancestor.Kind == "class")
                {
                    var fullClass = Full(ancestor);
                    if (fullClass.Length <= maximum) return new(ancestor, fullClass, "full");
                    break;
                }
                container = ancestor.ContainerId;
            }
        }
        var full = Full(hit);
        if (full.Length <= maximum) return new(hit, full, "full");
        var outline = Outline(hit, units);
        var summary = Summary(hit);
        const string omitted = "\n\nソースコードは返却上限のため省略しました。元ファイルはsourceのパスに存在します。全文は文書IDでfetchできます。";
        if (outline.Length > 0 && (summary + "\n\n" + outline + omitted).Length <= maximum)
            return new(hit, summary + "\n\n" + outline + omitted, "outline");
        if (summary.Length + omitted.Length <= maximum) return new(hit, summary + omitted, "summary");
        const string shortened = "\n説明も返却上限のため一部省略しています。";
        var length = maximum - omitted.Length - shortened.Length;
        if (char.IsHighSurrogate(summary[length - 1])) length--;
        return new(hit, summary[..length] + shortened + omitted, "summary_truncated");
    }
}
