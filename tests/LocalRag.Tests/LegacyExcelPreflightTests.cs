using System.Buffers.Binary;
using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public sealed class LegacyExcelPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsBiff8InRegularAndMiniStream(bool mini)
        => LegacyExcelPreflight.Validate(Container(Workbook(), mini));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IgnoresUninitializedHighStreamSizeDwordInVersion3(bool mini)
    {
        var bytes = Container(Workbook(), mini);
        W32(bytes, 512 + 124, 0xfedcba98);
        W32(bytes, 512 + 128 + 124, uint.MaxValue);
        LegacyExcelPreflight.Validate(bytes);
    }

    [Theory]
    [InlineData(0xfc)]
    [InlineData(0xfd)]
    [InlineData(0xfe)]
    public void IgnoresUndefinedUpperBitsInBoundSheetHiddenState(byte state)
    {
        var workbook = Workbook();
        workbook[28] = state;
        LegacyExcelPreflight.Validate(Container(workbook));
    }

    [Fact]
    public void RejectsReservedBoundSheetHiddenState()
    {
        var workbook = Workbook();
        workbook[28] = 0xff;
        Fails(Container(workbook), "excel_invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsSameSheetAndUnusedSupBookSentinels(bool unicode)
    {
        byte[] sameSheet = unicode ? [0, 0, 1, 0, 1, 0, 0] : [0, 0, 1, 0, 0, 0];
        byte[] unused = unicode ? [0, 0, 1, 0, 1, 0x20, 0] : [0, 0, 1, 0, 0, 0x20];
        byte[] unusedWithPlaceholder = [1, 0, 1, 0, 0, 0x20, 1, 0, 0, 0x20];
        LegacyExcelPreflight.Validate(Container(Workbook([
            Record(0x01ae, sameSheet), Record(0x01ae, unused), Record(0x01ae, unusedWithPlaceholder)])));
    }

    [Fact]
    public void AcceptsOrdinaryFormattingAndCachedExternalWorkbookReferences()
    {
        var supBook = new byte[] { 1, 0, 2, 0, 0, 1, (byte)'x', 1, 0, 0, (byte)'S' };
        var book = Workbook([Record(0x0042, [0xb0, 0x04]), Record(0x01ae, [1, 0, 1, 4]),
            Record(0x01ae, supBook), Record(0x01b7, [0, 0]), Record(0x0863, [0, 0, 0, 0])]);
        LegacyExcelPreflight.Validate(Container(book));
    }

    [Theory]
    [InlineData(0x002f, "excel_encrypted")]
    [InlineData(0x00d3, "excel_macro_sheet")]
    [InlineData(0x01ad, "excel_external_data")]
    [InlineData(0x0876, "excel_external_data")]
    [InlineData(0x00d5, "excel_external_data")]
    [InlineData(0x0813, "excel_external_data")]
    public void RejectsEncryptedMacrosConnectionsAndPivotCaches(int id, string code)
        => Fails(Container(Workbook([Record((ushort)id, [])])), code);

    [Fact]
    public void RejectsMacroBoundSheetBeforeOpening()
        => Fails(Container(Workbook(sheetKind: 1)), "excel_macro_sheet");

    [Fact]
    public void RejectsMacroBofEvenWithWorksheetDirectory()
        => Fails(Container(Workbook(sheetBof: 0x0040)), "excel_macro_sheet");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    public void RejectsAutomaticMacroDefinedNames(byte builtin)
    {
        var name = new byte[16];
        name[0] = 0x20; name[3] = 1; name[15] = builtin;
        Fails(Container(Workbook([Record(0x0018, name)])), "excel_macro_sheet");
    }

    [Fact]
    public void AcceptsNormalPrintAreaDefinedName()
    {
        var name = new byte[16];
        name[0] = 0x20; name[3] = 1; name[15] = 6;
        LegacyExcelPreflight.Validate(Container(Workbook([Record(0x0018, name)])));
    }

    [Fact]
    public void RejectsRefreshOnLoadAndDdeAddins()
    {
        Fails(Container(Workbook([Record(0x01b7, [1, 0])])), "excel_external_data");
        Fails(Container(Workbook([Record(0x01ae, [0, 0, 1, 0, 0, 65])])), "excel_external_data");
        Fails(Container(Workbook([Record(0x01ae, [1, 0, 1, 0x3a])])), "excel_external_data");
    }

    [Fact]
    public void RejectsOlderBiffAndDualStream()
    {
        var book = Workbook();
        W16(book, 4, 0x0500);
        Fails(Container(book), "excel_unsupported");
        Fails(Container(Workbook([Record(0x0161, [1, 0])])), "excel_unsupported");
    }

    [Fact]
    public void RejectsBrokenFatChainAndSectorOverlap()
    {
        var cycle = Container(Workbook());
        W32(cycle, 1024 + 2 * 4, 2);
        Fails(cycle, "excel_invalid");
        var overlap = Container(Workbook());
        W32(overlap, 512 + 128 + 116, 0);
        Fails(overlap, "excel_invalid");
    }

    [Fact]
    public void RejectsBrokenMiniChainAndDirectoryCycle()
    {
        var cycle = Container(Workbook(), true);
        W32(cycle, 1536, 0); // one mini sector points to itself rather than ENDOFCHAIN
        Fails(cycle, "excel_invalid");
        var directoryCycle = Container(Workbook());
        W32(directoryCycle, 512 + 128 + 72, 1);
        Fails(directoryCycle, "excel_invalid");
    }

    [Fact]
    public void RejectsMissingEofWrongSheetPointerAndTruncatedRecord()
    {
        var missing = Workbook();
        missing = missing[..^4];
        Fails(Container(missing), "excel_invalid");
        var pointer = Workbook();
        W32(pointer, 24, 41);
        Fails(Container(pointer), "excel_invalid");
        var truncated = Workbook();
        W16(truncated, 2, 9000);
        Fails(Container(truncated), "excel_invalid");
    }

    [Fact]
    public void RejectsUnsupportedEmbeddedStorageAndVba()
    {
        var embedded = Container(Workbook());
        DirectoryEntry(embedded.AsSpan(512 + 256, 128), "Embedded", 1, uint.MaxValue - 1, 0);
        W32(embedded, 512 + 128 + 72, 2);
        Fails(embedded, "excel_unsupported");
        var vba = Container(Workbook());
        DirectoryEntry(vba.AsSpan(512 + 256, 128), "_VBA_PROJECT_CUR", 1, uint.MaxValue - 1, 0);
        W32(vba, 512 + 128 + 72, 2);
        Fails(vba, "excel_macro_sheet");
    }

    [Fact]
    public void RejectsEncryptedPackageContainer()
    {
        var bytes = Container(Workbook());
        DirectoryEntry(bytes.AsSpan(512 + 128, 128), "EncryptedPackage", 2, 2, 4096);
        Fails(bytes, "excel_encrypted");
    }

    [Fact]
    public void RejectsRandomOrTruncatedContainer()
    {
        Fails(new byte[500], "excel_invalid");
        Fails(Container(Workbook())[..^1], "excel_invalid");
    }

    private static void Fails(byte[] bytes, string code)
        => Assert.Equal(code, Assert.Throws<PipelineValidationException>(() => LegacyExcelPreflight.Validate(bytes)).Code);

    private static byte[] Record(ushort id, byte[] payload)
    {
        var result = new byte[4 + payload.Length];
        W16(result, 0, id); W16(result, 2, (ushort)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static byte[] Bof(ushort kind)
    {
        var payload = new byte[16]; W16(payload, 0, 0x0600); W16(payload, 2, kind);
        return Record(0x0809, payload);
    }

    private static byte[] Workbook(byte[][]? extras = null, byte sheetKind = 0, ushort sheetBof = 0x0010)
    {
        extras ??= [];
        var bound = new byte[12];
        W32(bound, 0, (uint)(40 + extras.Sum(e => e.Length)));
        bound[5] = sheetKind; bound[6] = 4;
        Encoding.ASCII.GetBytes("Data").CopyTo(bound, 8);
        return Bof(5).Concat(extras.SelectMany(e => e)).Concat(Record(0x0085, bound)).Concat(Record(0x000a, []))
            .Concat(Bof(sheetBof)).Concat(Record(0x000a, [])).ToArray();
    }

    private static byte[] Container(byte[] workbook, bool mini = false)
    {
        const uint end = 0xfffffffe, free = 0xffffffff;
        if (mini && workbook.Length > 512) throw new ArgumentOutOfRangeException(nameof(workbook));
        var file = new byte[mini ? 2560 : 5632];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(file, 0);
        W16(file, 24, 0x003e); W16(file, 26, 3); W16(file, 28, 0xfffe); W16(file, 30, 9); W16(file, 32, 6);
        W32(file, 44, 1); W32(file, 48, 0); W32(file, 56, 4096); W32(file, 60, mini ? 2U : end);
        W32(file, 64, mini ? 1U : 0U); W32(file, 68, end);
        for (var i = 0; i < 109; i++) W32(file, 76 + i * 4, free);
        W32(file, 76, 1);
        for (var i = 0; i < 128; i++) W32(file, 1024 + i * 4, free);
        W32(file, 1024, end); W32(file, 1028, 0xfffffffd);
        var miniSize = (workbook.Length + 63) / 64 * 64;
        DirectoryEntry(file.AsSpan(512, 128), "Root Entry", 5, mini ? 3U : end, mini ? miniSize : 0, 1);
        DirectoryEntry(file.AsSpan(640, 128), "Workbook", 2, mini ? 0U : 2U, mini ? workbook.Length : 4096);
        if (mini)
        {
            W32(file, 1032, end); W32(file, 1036, end);
            for (var i = 0; i < 128; i++) W32(file, 1536 + i * 4, free);
            for (var i = 0; i < miniSize / 64; i++) W32(file, 1536 + i * 4, i + 1 == miniSize / 64 ? end : (uint)(i + 1));
            workbook.CopyTo(file, 2048);
        }
        else
        {
            for (var i = 2; i < 10; i++) W32(file, 1024 + i * 4, i == 9 ? end : (uint)(i + 1));
            workbook.CopyTo(file, 1536);
        }
        return file;
    }

    private static void DirectoryEntry(Span<byte> entry, string name, byte type, uint start, int size, uint child = uint.MaxValue)
    {
        entry.Clear();
        Encoding.Unicode.GetBytes(name + "\0").CopyTo(entry);
        W16(entry, 64, (ushort)((name.Length + 1) * 2)); entry[66] = type; entry[67] = 1;
        W32(entry, 68, uint.MaxValue); W32(entry, 72, uint.MaxValue); W32(entry, 76, child);
        W32(entry, 116, start); W32(entry, 120, (uint)size);
    }

    private static void W16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), value);
    private static void W32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value);
}
