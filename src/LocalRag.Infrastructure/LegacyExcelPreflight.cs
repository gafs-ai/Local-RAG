using System.Buffers.Binary;
using System.Text;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

/// <summary>
/// Reads only the CFB directory and BIFF records, before the legacy workbook is opened by Excel.
/// This is a format/safety gate, not a general purpose BIFF reader or a malware scanner.
/// </summary>
public static class LegacyExcelPreflight
{
    private const uint Free = 0xffffffff, End = 0xfffffffe, Fat = 0xfffffffd, Dif = 0xfffffffc;
    private const int MaxBytes = 64 * 1024 * 1024;

    public static void Validate(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxBytes) throw Error("excel_package_size", "Excel ファイルは 64 MiB 以下にしてください。");
        var workbook = new CompoundFile(input).ReadWorkbook();
        ValidateBiff(workbook);
    }

    private static PipelineValidationException Error(string code, string message) => new(code, message);
    private static PipelineValidationException Invalid() => Error("excel_invalid", "Excel ファイルの構造が破損しているか、不正です。");
    private static PipelineValidationException Unsupported() => Error("excel_unsupported", "この旧形式 Excel の構造には対応していません。Excel で .xlsx として保存してください。");
    private static PipelineValidationException Macros() => Error("excel_macro_sheet", "マクロを含む旧形式 Excel は自動変換しません。マクロを除いた .xlsx として保存してください。");
    private static PipelineValidationException ExternalData() => Error("excel_external_data", "外部データ接続・自動更新を含む旧形式 Excel は自動変換しません。接続を除いた .xlsx として保存してください。");
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    // MS-XLS BOF, BoundSheet8, FilePass, SupBook, Lbl and RefreshAll records.
    // https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/7d9326d6-691a-4fa1-8dce-42082f38e943
    private static void ValidateBiff(byte[] workbook)
    {
        var sheets = new Dictionary<int, ushort>();
        var observedSheets = new HashSet<int>();
        var scopes = new Stack<ushort>();
        var sawGlobals = false;
        var globalsComplete = false;
        for (var offset = 0; offset < workbook.Length;)
        {
            // Workbook streams may be padded to a complete sector after the last EOF.
            if (scopes.Count == 0 && globalsComplete && workbook.AsSpan(offset).IndexOfAnyExcept((byte)0) < 0) break;
            if (workbook.Length - offset < 4) throw Invalid();
            var recordOffset = offset;
            var id = U16(workbook, offset);
            var length = U16(workbook, offset + 2);
            offset += 4;
            if (length > 8224 || length > workbook.Length - offset) throw Invalid();
            var data = workbook.AsSpan(offset, length);
            offset += length;

            if (id == 0x002f) throw Error("excel_encrypted", "暗号化された Excel は取り込めません。");
            if (id == 0x00d3) throw Macros(); // ObProj: VBA project present.
            if (IsExternalDataRecord(id)) throw ExternalData();
            if (id == 0x0809)
            {
                if (length != 16) throw Unsupported();
                if (U16(data, 0) != 0x0600) throw Unsupported(); // Excel 97-2003 / BIFF8 only.
                var kind = U16(data, 2);
                if (kind is 0x0040 or 0x0006) throw Macros();
                if (kind is not (0x0005 or 0x0010 or 0x0020)) throw Unsupported();
                if (!sawGlobals)
                {
                    if (recordOffset != 0 || kind != 0x0005) throw Invalid();
                    sawGlobals = true;
                }
                else if (scopes.Count == 0)
                {
                    if (!globalsComplete || !sheets.TryGetValue(recordOffset, out var expected) || kind != expected) throw Invalid();
                    observedSheets.Add(recordOffset);
                }
                else if (!globalsComplete || kind != 0x0020 || scopes.Count > 1) throw Invalid();
                scopes.Push(kind);
                continue;
            }
            if (scopes.Count == 0) throw Invalid();
            if (id == 0x000a)
            {
                if (length != 0) throw Invalid();
                if (scopes.Pop() == 0x0005) globalsComplete = true;
                continue;
            }
            if (id == 0x0085)
            {
                if (scopes.Peek() != 0x0005 || length < 8) throw Invalid();
                var kind = data[5];
                if (kind is 1 or 6) throw Macros();
                if (kind is not (0 or 2)) throw Unsupported();
                // Only the low two bits specify the hidden state; the upper six are undefined.
                if ((data[4] & 3) > 2 || data[6] is 0 or > 31 || data[7] > 1 || length != 8 + data[6] * (data[7] + 1)) throw Invalid();
                var position = U32(data, 0);
                if (position >= workbook.Length || !sheets.TryAdd((int)position, kind == 0 ? (ushort)0x0010 : (ushort)0x0020)) throw Invalid();
            }
            else if (id == 0x0018) ValidateDefinedName(data);
            else if (id == 0x01ae) ValidateSupBook(data);
            else if (id == 0x01b7)
            {
                if (length != 2) throw Invalid();
                if (U16(data, 0) != 0) throw ExternalData();
            }
            else if (id == 0x0161)
            {
                if (length != 2) throw Invalid();
                if (U16(data, 0) != 0) throw Unsupported(); // BIFF5/8 dual stream ambiguity.
            }
            else if (id is 0x0009 or 0x0209 or 0x0409) throw Unsupported();
            // Ordinary BIFF8 formatting, chart and future-extension records are retained.
            // Conversion runs with events/macros/link updates/calculation disabled in addition to this gate.
        }
        if (!sawGlobals || !globalsComplete || scopes.Count != 0 || sheets.Count == 0 || observedSheets.Count != sheets.Count) throw Invalid();
    }

    private static bool IsExternalDataRecord(ushort id) => id is
        0x0050 or 0x0051 or 0x0052 or // DCon / DConRef / DConName
        0x00c6 or 0x00d5 or 0x00dc or // SXDB / SXStreamID / DbOrParamQry
        0x01ad or 0x01b5 or // Qsi / DConBin
        0x0802 or 0x0803 or 0x0805 or 0x0806 or 0x0807 or 0x080a or // query table / OLE DB
        0x0813 or 0x0876; // RealTimeData / DConn

    private static void ValidateSupBook(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw Invalid();
        var sheets = U16(data, 0);
        var characters = U16(data, 2);
        if (characters == 0x0401 && data.Length == 4) return; // Internal references.
        if (characters == 0x3a01) throw ExternalData(); // Add-ins.
        if (characters is 0 or > 255 || data.Length < 5 || data[4] > 1) throw Invalid();
        var width = data[4] + 1;
        if (data.Length < 5 + characters * width) throw Invalid();
        var first = width == 1 ? data[5] : U16(data, 5);
        // These are benign link sentinels, not DDE/OLE references. An unused link may retain sheet placeholders.
        if (characters == 1 && first == 0x20) return;
        if (characters == 1 && first == 0)
        {
            if (sheets != 0 || data.Length != 5 + width) throw Invalid();
            return;
        }
        if (sheets == 0) throw ExternalData(); // DDE or OLE.
        // Encoded workbook paths are safe cached links when opened with UpdateLinks=0.
        // DDE/OLE names and alternative link encodings are not admitted to automation.
        if (first != 1) throw ExternalData();
    }

    private static void ValidateDefinedName(ReadOnlySpan<byte> data)
    {
        if (data.Length < 15) throw Invalid();
        var flags = U16(data, 0);
        if ((flags & 0x000e) != 0) throw Macros(); // Function, VB procedure or macro name.
        var count = data[3];
        if (count == 0 || data[14] > 1) throw Invalid();
        var width = data[14] + 1;
        if (data.Length < 15 + count * width) throw Invalid();
        if ((flags & 0x0020) != 0)
        {
            if (count != 1) throw Invalid();
            var builtin = width == 1 ? data[15] : U16(data, 15);
            if (builtin is 1 or 2 or 10 or 11) throw Macros();
        }
        else
        {
            var name = width == 1 ? Encoding.Latin1.GetString(data.Slice(15, count)) : Encoding.Unicode.GetString(data.Slice(15, count * 2));
            if (name.Equals("Auto_Open", StringComparison.OrdinalIgnoreCase) || name.Equals("Auto_Close", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Auto_Activate", StringComparison.OrdinalIgnoreCase) || name.Equals("Auto_Deactivate", StringComparison.OrdinalIgnoreCase)) throw Macros();
        }
    }

    // MS-CFB: sector chains, DIFAT, mini FAT and directory tree. Bounds derive from the 64 MiB input limit.
    // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/05060311-bfce-4b12-874d-71fd4ce63aea
    private sealed class CompoundFile
    {
        private readonly byte[] bytes;
        private readonly int sectorSize;
        private readonly int sectorCount;
        private readonly ushort major;
        private readonly uint[] fat;
        private readonly HashSet<uint> claimed = [];
        private readonly HashSet<uint> miniClaimed = [];
        private uint[] miniFat = [];
        private byte[] miniStream = [];

        public CompoundFile(byte[] input)
        {
            bytes = input;
            ReadOnlySpan<byte> signature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
            if (input.Length < 512 || !input.AsSpan(0, 8).SequenceEqual(signature)) throw Invalid();
            major = U16(input, 26);
            if (major is not (3 or 4) || U16(input, 28) != 0xfffe || U16(input, 30) != (major == 3 ? 9 : 12)
                || U16(input, 32) != 6 || U32(input, 56) != 4096) throw Unsupported();
            sectorSize = major == 3 ? 512 : 4096;
            if (input.Length < sectorSize * 3 || input.Length % sectorSize != 0 || input.AsSpan(34, 6).IndexOfAnyExcept((byte)0) >= 0
                || (major == 3 && U32(input, 40) != 0) || (major == 4 && input.AsSpan(512, 3584).IndexOfAnyExcept((byte)0) >= 0)) throw Invalid();
            sectorCount = input.Length / sectorSize - 1;
            var fatCount = U32(input, 44);
            var difatCount = U32(input, 72);
            if (fatCount == 0 || fatCount > sectorCount || difatCount > sectorCount) throw Invalid();
            var fatIds = new List<uint>();
            void AddFat(uint value)
            {
                if (value == Free) return;
                if (value >= sectorCount || fatIds.Count >= fatCount || !claimed.Add(value)) throw Invalid();
                fatIds.Add(value);
            }
            for (var i = 0; i < 109; i++) AddFat(U32(input, 76 + i * 4));
            var difatIds = new List<uint>();
            var next = U32(input, 68);
            for (var i = 0U; i < difatCount; i++)
            {
                if (next >= sectorCount || !claimed.Add(next)) throw Invalid();
                difatIds.Add(next);
                var sector = Sector(next);
                for (var j = 0; j < sectorSize / 4 - 1; j++) AddFat(U32(sector, j * 4));
                next = U32(sector, sectorSize - 4);
            }
            if ((next != End && !(difatCount == 0 && next == Free)) || fatIds.Count != fatCount) throw Invalid();
            fat = new uint[fatIds.Count * (sectorSize / 4)];
            for (var i = 0; i < fatIds.Count; i++)
            {
                var sector = Sector(fatIds[i]);
                for (var j = 0; j < sectorSize / 4; j++) fat[i * (sectorSize / 4) + j] = U32(sector, j * 4);
            }
            if (fat.Length < sectorCount || fatIds.Any(id => fat[id] != Fat) || difatIds.Any(id => fat[id] != Dif)) throw Invalid();
        }

        private ReadOnlySpan<byte> Sector(uint id)
        {
            if (id >= sectorCount) throw Invalid();
            return bytes.AsSpan(((int)id + 1) * sectorSize, sectorSize);
        }

        private byte[] ReadChain(uint first, int? size = null, uint? count = null)
        {
            if (size is < 0 or > MaxBytes || count > sectorCount) throw Invalid();
            var expected = size.HasValue ? (size.Value + sectorSize - 1) / sectorSize : count.HasValue ? (int)count.Value : -1;
            if (expected == 0)
            {
                if (first is not (End or Free)) throw Invalid();
                return [];
            }
            var ids = new List<uint>();
            var current = first;
            while (current != End)
            {
                if (current >= sectorCount || !claimed.Add(current) || (expected >= 0 && ids.Count >= expected)) throw Invalid();
                ids.Add(current);
                current = fat[current];
            }
            if (ids.Count == 0 || (expected >= 0 && ids.Count != expected)) throw Invalid();
            var result = new byte[size ?? checked(ids.Count * sectorSize)];
            for (var i = 0; i < ids.Count; i++) Sector(ids[i]).Slice(0, Math.Min(sectorSize, result.Length - i * sectorSize)).CopyTo(result.AsSpan(i * sectorSize));
            return result;
        }

        public byte[] ReadWorkbook()
        {
            var directory = ReadChain(U32(bytes, 48), count: major == 4 ? U32(bytes, 40) : null);
            if (directory.Length / 128 > 65536) throw Unsupported();
            var entries = new List<Entry>();
            var unicode = new UnicodeEncoding(false, false, true);
            for (var i = 0; i < directory.Length; i += 128)
            {
                var data = directory.AsSpan(i, 128);
                var type = data[66];
                if (type == 0) { entries.Add(new("", 0, Free, Free, Free, End, 0)); continue; }
                var nameBytes = U16(data, 64);
                if (type is not (1 or 2 or 5) || nameBytes < 2 || nameBytes > 64 || nameBytes % 2 != 0 || U16(data, nameBytes - 2) != 0) throw Invalid();
                string name;
                try { name = unicode.GetString(data.Slice(0, nameBytes - 2)); }
                catch (DecoderFallbackException) { throw Invalid(); }
                if (name.Length == 0 || name.Contains('\0')) throw Invalid();
                // Legacy version-3 writers may leave the high DWORD uninitialized (MS-CFB 2.6.3).
                // Version 4 uses all 64 bits and must still respect the input bound.
                ulong size = major == 3 ? U32(data, 120) : BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(120, 8));
                if (size > MaxBytes) throw Invalid();
                entries.Add(new(name, type, U32(data, 68), U32(data, 72), U32(data, 76), U32(data, 116), (int)size));
            }
            if (entries.Count == 0 || entries[0].Type != 5 || entries[0].Left != Free || entries[0].Right != Free) throw Invalid();
            var root = entries[0];
            var visited = new HashSet<uint> { 0 };
            var pending = new Stack<uint>();
            if (root.Child != Free) pending.Push(root.Child);
            var streams = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (id >= entries.Count || !visited.Add(id)) throw Invalid();
                var entry = entries[(int)id];
                if (entry.Type == 0 || entry.Type == 5) throw Invalid();
                if (entry.Name.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                    throw Error("excel_encrypted", "暗号化された Excel は取り込めません。");
                if (entry.Name.Contains("VBA", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("Ctls", StringComparison.OrdinalIgnoreCase)) throw Macros();
                if (entry.Type != 2) throw Unsupported(); // No embedded packages, VBA or pivot-cache storages are opened.
                if (entry.Child != Free || !streams.TryAdd(entry.Name, entry)) throw Invalid();
                if (entry.Name is not ("Workbook" or "Book" or "\u0005SummaryInformation" or "\u0005DocumentSummaryInformation" or "\u0001CompObj")) throw Unsupported();
                if (entry.Left != Free) pending.Push(entry.Left);
                if (entry.Right != Free) pending.Push(entry.Right);
            }
            if (entries.Where(e => e.Type != 0).Count() != visited.Count) throw Invalid();
            if (streams.ContainsKey("Workbook") == streams.ContainsKey("Book")) throw Invalid();
            miniStream = ReadChain(root.Start, root.Size);
            var miniFatBytes = ReadChain(U32(bytes, 60), count: U32(bytes, 64));
            miniFat = new uint[miniFatBytes.Length / 4];
            for (var i = 0; i < miniFat.Length; i++) miniFat[i] = U32(miniFatBytes, i * 4);
            byte[]? result = null;
            foreach (var entry in streams.Values)
            {
                var content = entry.Size < 4096 ? ReadMini(entry) : ReadChain(entry.Start, entry.Size);
                if (entry.Name is "Workbook" or "Book") result = content;
            }
            return result ?? throw Invalid();
        }

        private byte[] ReadMini(Entry entry)
        {
            if (entry.Size == 0)
            {
                if (entry.Start is not (End or Free)) throw Invalid();
                return [];
            }
            var result = new byte[entry.Size];
            var current = entry.Start;
            for (var offset = 0; offset < entry.Size; offset += 64)
            {
                if (current >= miniFat.Length || current >= miniStream.Length / 64 || !miniClaimed.Add(current)) throw Invalid();
                miniStream.AsSpan((int)current * 64, Math.Min(64, result.Length - offset)).CopyTo(result.AsSpan(offset));
                current = miniFat[current];
            }
            if (current != End) throw Invalid();
            return result;
        }

        private sealed record Entry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, int Size);
    }
}
