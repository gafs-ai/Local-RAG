using System.Text.Json.Serialization;

namespace LocalRag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<TextEncodingMode>))]
public enum TextEncodingMode { Auto, Utf8, ShiftJis, EucJp, Iso2022Jp, Gb18030, Big5, Korean, Utf16Le, Utf16Be, Utf32Le, Utf32Be }

public sealed record TextEncodingChoice(TextEncodingMode Mode, string Name, int CodePage);

public static class TextEncodings
{
    public static IReadOnlyList<TextEncodingChoice> All { get; } = Array.AsReadOnly(new TextEncodingChoice[]
    {
        new(TextEncodingMode.Auto, "自動判別", 0), new(TextEncodingMode.Utf8, "UTF-8", 65001),
        new(TextEncodingMode.ShiftJis, "Shift-JIS（Windows-31J / CP932）", 932),
        new(TextEncodingMode.EucJp, "EUC-JP", 51932), new(TextEncodingMode.Iso2022Jp, "ISO-2022-JP", 50220),
        new(TextEncodingMode.Gb18030, "GB18030 / GBK（簡体字中国語）", 54936),
        new(TextEncodingMode.Big5, "Big5（繁体字中国語）", 950),
        new(TextEncodingMode.Korean, "CP949 / EUC-KR（韓国語）", 949),
        new(TextEncodingMode.Utf16Le, "UTF-16 LE", 1200), new(TextEncodingMode.Utf16Be, "UTF-16 BE", 1201),
        new(TextEncodingMode.Utf32Le, "UTF-32 LE", 12000), new(TextEncodingMode.Utf32Be, "UTF-32 BE", 12001)
    });
}
