using System.Text;
using LocalRag.Core;
using UtfUnknown;

namespace LocalRag.Infrastructure;

public sealed record DecodedText(string Text, TextEncodingMode Mode, bool Automatic);

public static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static DecodedText Decode(byte[] bytes, TextEncodingMode mode = TextEncodingMode.Auto)
    {
        if (!Enum.IsDefined(mode)) throw Failure("encoding_invalid");
        var automatic = mode == TextEncodingMode.Auto;
        var (bomMode, offset) = Bom(bytes);
        if (offset > 0)
        {
            if (!automatic && mode != bomMode) throw Failure("encoding_bom_conflict");
            return DecodeStrict(bytes, offset, bomMode, automatic);
        }
        if (!automatic) return DecodeStrict(bytes, 0, mode, false);

        // ISO-2022-JP contains valid ASCII bytes. Detect escape sequences before the UTF-8 fast path.
        var escaped = bytes.Contains((byte)0x1B);
        if (!escaped)
        {
            try { return DecodeStrict(bytes, 0, TextEncodingMode.Utf8, true); }
            catch (PipelineValidationException ex) when (ex.Code == "encoding_invalid") { }
        }
        // The supported legacy encodings have no NUL bytes in ordinary text. BOM-less Unicode
        // containing NUL is deliberately left for an explicit user selection rather than guessed.
        if (bytes.Contains((byte)0)) throw Failure("encoding_uncertain");
        var detected = CharsetDetector.DetectFromBytes(bytes).Detected;
        if (detected is null || !float.IsFinite(detected.Confidence) || detected.Confidence < 0.80f)
            throw Failure("encoding_uncertain");
        var detectedMode = detected.EncodingName.ToLowerInvariant() switch
        {
            "shift-jis" or "shift_jis" or "windows-31j" => TextEncodingMode.ShiftJis,
            "euc-jp" => TextEncodingMode.EucJp,
            "iso-2022-jp" => TextEncodingMode.Iso2022Jp,
            "gb18030" or "gb2312" or "gbk" => TextEncodingMode.Gb18030,
            "big5" => TextEncodingMode.Big5,
            "euc-kr" or "uhc" or "cp949" => TextEncodingMode.Korean,
            _ => TextEncodingMode.Auto
        };
        if (detectedMode == TextEncodingMode.Auto) throw Failure("encoding_uncertain");
        return DecodeStrict(bytes, 0, detectedMode, true);
    }

    private static DecodedText DecodeStrict(byte[] bytes, int offset, TextEncodingMode mode, bool automatic)
    {
        var codePage = TextEncodings.All.Single(e => e.Mode == mode).CodePage;
        try
        {
            var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            // No replacement decoding, NULs or binary control characters in indexable input.
            if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f')))
                throw Failure("encoding_invalid");
            return new(text, mode, automatic);
        }
        catch (DecoderFallbackException) { throw Failure("encoding_invalid"); }
    }

    private static (TextEncodingMode Mode, int Offset) Bom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return (TextEncodingMode.Utf32Le, 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return (TextEncodingMode.Utf32Be, 4);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (TextEncodingMode.Utf8, 3);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return (TextEncodingMode.Utf16Le, 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return (TextEncodingMode.Utf16Be, 2);
        return (TextEncodingMode.Auto, 0);
    }

    private static PipelineValidationException Failure(string code) => new(code, "文字コードを確認し、適用する設定で指定してください。");
}
