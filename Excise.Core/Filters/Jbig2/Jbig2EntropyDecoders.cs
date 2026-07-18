using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Filters.Jbig2;

internal interface IJbig2ArithmeticDecoder
{
    bool Decode(ref int context);
}

internal sealed class Jbig2ArithmeticContextState
{
    public Jbig2ArithmeticContextState(int contextCount)
    {
        if (contextCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(contextCount), contextCount, "Context count must be positive.");

        QeIndexByContext = new byte[contextCount];
        MpsByContext = new byte[contextCount];
    }

    public int Count => QeIndexByContext.Length;

    internal byte[] QeIndexByContext { get; }
    internal byte[] MpsByContext { get; }
}

/// <summary>
/// Context-state MQ arithmetic decoder following the JBIG2 arithmetic coding model.
/// This is the shared arithmetic engine for generic regions, symbol dictionaries,
/// text regions, and arithmetic integer procedures.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2MqArithmeticDecoder : IJbig2ArithmeticDecoder
{
    private static readonly (uint Qe, int NextMps, int NextLps, bool SwitchMps)[] QeTable =
    [
        (0x5601u, 1, 1, true),
        (0x3401u, 2, 6, false),
        (0x1801u, 3, 9, false),
        (0x0AC1u, 4, 12, false),
        (0x0521u, 5, 29, false),
        (0x0221u, 38, 33, false),
        (0x5601u, 7, 6, true),
        (0x5401u, 8, 14, false),
        (0x4801u, 9, 14, false),
        (0x3801u, 10, 14, false),
        (0x3001u, 11, 17, false),
        (0x2401u, 12, 18, false),
        (0x1C01u, 13, 20, false),
        (0x1601u, 29, 21, false),
        (0x5601u, 15, 14, true),
        (0x5401u, 16, 14, false),
        (0x5101u, 17, 15, false),
        (0x4801u, 18, 16, false),
        (0x3801u, 19, 17, false),
        (0x3401u, 20, 18, false),
        (0x3001u, 21, 19, false),
        (0x2801u, 22, 19, false),
        (0x2401u, 23, 20, false),
        (0x2201u, 24, 21, false),
        (0x1C01u, 25, 22, false),
        (0x1801u, 26, 23, false),
        (0x1601u, 27, 24, false),
        (0x1401u, 28, 25, false),
        (0x1201u, 29, 26, false),
        (0x1101u, 30, 27, false),
        (0x0AC1u, 31, 28, false),
        (0x09C1u, 32, 29, false),
        (0x08A1u, 33, 30, false),
        (0x0521u, 34, 31, false),
        (0x0441u, 35, 32, false),
        (0x02A1u, 36, 33, false),
        (0x0221u, 37, 34, false),
        (0x0141u, 38, 35, false),
        (0x0111u, 39, 36, false),
        (0x0085u, 40, 37, false),
        (0x0049u, 41, 38, false),
        (0x0025u, 42, 39, false),
        (0x0015u, 43, 40, false),
        (0x0009u, 44, 41, false),
        (0x0005u, 45, 42, false),
        (0x0001u, 45, 43, false),
        (0x5601u, 46, 46, false),
    ];

    private readonly byte[] _data;
    private readonly byte[] _qeIndexByContext;
    private readonly byte[] _mpsByContext;
    private int _byteIndex;
    private uint _a;
    private uint _c;
    private int _ct;

    public Jbig2MqArithmeticDecoder(byte[] data, int contextCount)
        : this(data, new Jbig2ArithmeticContextState(contextCount))
    {
    }

    public Jbig2MqArithmeticDecoder(byte[] data, Jbig2ArithmeticContextState contextState)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (contextState == null)
            throw new ArgumentNullException(nameof(contextState));

        _qeIndexByContext = contextState.QeIndexByContext;
        _mpsByContext = contextState.MpsByContext;
        _a = 0x8000;

        if (_data.Length > 0)
        {
            int firstByte = ReadByte();
            _c = (uint)firstByte << 16;
            ByteIn();
            _c <<= 7;
            _ct -= 7;
        }
    }

    public bool Decode(ref int context)
    {
        if ((uint)context >= (uint)_qeIndexByContext.Length)
            throw new ArgumentOutOfRangeException(nameof(context), context, "JBIG2 arithmetic context index is outside the context state table.");

        int qeIndex = _qeIndexByContext[context];
        byte mps = _mpsByContext[context];
        uint qe = QeTable[qeIndex].Qe;
        _a -= qe;

        int decoded;
        if ((_c >> 16) < qe)
        {
            decoded = LpsExchange(context, qeIndex, qe, mps);
            Renormalize();
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) == 0)
            {
                decoded = MpsExchange(context, qeIndex, mps);
                Renormalize();
            }
            else
            {
                decoded = mps;
            }
        }

        return decoded != 0;
    }

    internal (int QeIndex, int Mps) GetContextStateForTest(int context)
        => (_qeIndexByContext[context], _mpsByContext[context]);

    private int MpsExchange(int context, int qeIndex, byte mps)
    {
        var entry = QeTable[qeIndex];
        if (_a < entry.Qe)
        {
            if (entry.SwitchMps)
                _mpsByContext[context] ^= 1;

            _qeIndexByContext[context] = (byte)entry.NextLps;
            return 1 - mps;
        }

        _qeIndexByContext[context] = (byte)entry.NextMps;
        return mps;
    }

    private int LpsExchange(int context, int qeIndex, uint qe, byte mps)
    {
        var entry = QeTable[qeIndex];
        if (_a < qe)
        {
            _qeIndexByContext[context] = (byte)entry.NextMps;
            _a = qe;
            return mps;
        }

        if (entry.SwitchMps)
            _mpsByContext[context] ^= 1;

        _qeIndexByContext[context] = (byte)entry.NextLps;
        _a = qe;
        return 1 - mps;
    }

    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
                ByteIn();

            _a <<= 1;
            _c <<= 1;
            _ct--;
        } while ((_a & 0x8000) == 0);

        _c &= 0xFFFFFFFFu;
    }

    private void ByteIn()
    {
        if (_byteIndex > 0)
            _byteIndex--;

        int b = ReadByte();
        if (b == 0xFF)
        {
            int nextByte = ReadByte();
            if (nextByte > 0x8F)
            {
                _c += 0xFF00u;
                _ct = 8;
                _byteIndex = Math.Max(0, _byteIndex - 2);
            }
            else
            {
                _c += (uint)(nextByte << 9);
                _ct = 7;
            }
        }
        else
        {
            b = ReadByte();
            _c += (uint)(b << 8);
            _ct = 8;
        }

        _c &= 0xFFFFFFFFu;
    }

    private int ReadByte()
    {
        if (_byteIndex >= _data.Length)
            return 0xFF;

        return _data[_byteIndex++];
    }
}

/// <summary>
/// JBIG2 arithmetic integer decoder.
/// The caller provides a context-base per integer procedure so IADH/IADW/IAAI/etc.
/// keep independent arithmetic contexts as required by the coding model.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2ArithmeticIntegerDecoder
{
    private readonly IJbig2ArithmeticDecoder _decoder;
    private readonly int _contextBase;

    public Jbig2ArithmeticIntegerDecoder(IJbig2ArithmeticDecoder decoder, int contextBase = 0)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _contextBase = contextBase;
    }

    public long? Decode()
    {
        int previous = 1;
        int sign = DecodeBit(ref previous);
        (int bitsToRead, long offset) = DecodeMagnitudeTier(ref previous);

        long magnitude = 0;
        for (int i = 0; i < bitsToRead; i++)
            magnitude = (magnitude << 1) + DecodeBit(ref previous);

        long value = offset + magnitude;
        if (sign == 0)
            return value;

        return value == 0 ? null : -value;
    }

    private (int BitsToRead, long Offset) DecodeMagnitudeTier(ref int previous)
    {
        if (DecodeBit(ref previous) == 0)
            return (2, 0);
        if (DecodeBit(ref previous) == 0)
            return (4, 4);
        if (DecodeBit(ref previous) == 0)
            return (6, 20);
        if (DecodeBit(ref previous) == 0)
            return (8, 84);
        if (DecodeBit(ref previous) == 0)
            return (12, 340);

        return (32, 4436);
    }

    private int DecodeBit(ref int previous)
    {
        int context = _contextBase + (previous & 0x1FF);
        int bit = _decoder.Decode(ref context) ? 1 : 0;
        previous = previous < 256
            ? ((previous << 1) | bit) & 0x1FF
            : (int)((((uint)previous << 1) | (uint)bit) & 0x1FFu) | 0x100;
        return bit;
    }
}

/// <summary>
/// JBIG2 IAID fixed-length arithmetic symbol-id decoder.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2IaidDecoder
{
    private readonly IJbig2ArithmeticDecoder _decoder;
    private readonly int _contextBase;

    public Jbig2IaidDecoder(IJbig2ArithmeticDecoder decoder, int contextBase = 0)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _contextBase = contextBase;
    }

    public uint Decode(int codeLength)
    {
        if (codeLength is <= 0 or > 31)
            throw new ArgumentOutOfRangeException(nameof(codeLength), codeLength, "IAID code length must be between 1 and 31 bits.");

        long previous = 1;
        long contextMask = (1L << codeLength) - 1;
        for (int i = 0; i < codeLength; i++)
        {
            int context = checked(_contextBase + (int)(previous & contextMask));
            int bit = _decoder.Decode(ref context) ? 1 : 0;
            previous = (previous << 1) | (long)bit;
        }

        return checked((uint)(previous - (1L << codeLength)));
    }
}

internal sealed class Jbig2BitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _bitOffset;

    public Jbig2BitReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public int BitsRemaining => (_data.Length * 8) - _bitOffset;

    public bool ReadBit()
    {
        if (_bitOffset >= _data.Length * 8)
            throw new InvalidOperationException("Unexpected end of JBIG2 bit stream");

        ReadOnlySpan<byte> span = _data.Span;
        int byteIndex = _bitOffset / 8;
        int bitIndex = 7 - (_bitOffset % 8);
        _bitOffset++;
        return ((span[byteIndex] >> bitIndex) & 0x01) != 0;
    }

    public uint ReadBits(int count)
    {
        if (count is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Bit count must be between 0 and 32.");

        uint value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | (ReadBit() ? 1u : 0u);

        return value;
    }

    public void AlignToByte()
    {
        int remainder = _bitOffset % 8;
        if (remainder != 0)
            _bitOffset += 8 - remainder;
    }

    public byte ReadAlignedByte()
    {
        if ((_bitOffset % 8) != 0)
            throw new InvalidOperationException("JBIG2 bit stream is not byte-aligned");

        return (byte)ReadBits(8);
    }

    public byte[] ReadAlignedBytes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        var bytes = new byte[count];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = ReadAlignedByte();

        return bytes;
    }
}

internal readonly record struct Jbig2HuffmanTableEntry(
    int PrefixLength,
    uint PrefixCode,
    int RangeLength,
    long RangeLow,
    bool IsOutOfBand = false,
    bool IsLowerRange = false);

internal readonly record struct Jbig2HuffmanTableLine(
    int PrefixLength,
    int RangeLength,
    long RangeLow,
    bool IsOutOfBand = false,
    bool IsLowerRange = false);

/// <summary>
/// JBIG2 Huffman decoder for canonical table entries.
/// Standard and user-supplied tables both reduce to prefix entries with an optional range tail.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2HuffmanTable
{
    private readonly Dictionary<(int PrefixLength, uint PrefixCode), Jbig2HuffmanTableEntry> _entries;
    private readonly int _maxPrefixLength;

    public Jbig2HuffmanTable(IEnumerable<Jbig2HuffmanTableEntry> entries)
    {
        if (entries == null)
            throw new ArgumentNullException(nameof(entries));

        var entryArray = entries.ToArray();
        if (entryArray.Length == 0)
            throw new ArgumentException("A JBIG2 Huffman table must have at least one entry.", nameof(entries));

        _entries = new Dictionary<(int, uint), Jbig2HuffmanTableEntry>();
        foreach (var entry in entryArray)
        {
            if (entry.PrefixLength <= 0 || entry.PrefixLength > 32)
                throw new ArgumentOutOfRangeException(nameof(entries), entry.PrefixLength, "Huffman prefix length must be between 1 and 32.");
            if (entry.RangeLength < 0 || entry.RangeLength > 32)
                throw new ArgumentOutOfRangeException(nameof(entries), entry.RangeLength, "Huffman range length must be between 0 and 32.");

            var key = (entry.PrefixLength, entry.PrefixCode);
            if (!_entries.TryAdd(key, entry))
                throw new ArgumentException("Duplicate JBIG2 Huffman prefix entry.", nameof(entries));
        }

        _maxPrefixLength = entryArray.Max(e => e.PrefixLength);
    }

    public static Jbig2HuffmanTable FromCanonicalLines(IEnumerable<Jbig2HuffmanTableLine> lines)
    {
        if (lines == null)
            throw new ArgumentNullException(nameof(lines));

        var lineArray = lines.Where(line => line.PrefixLength > 0).ToArray();
        if (lineArray.Length == 0)
            throw new ArgumentException("A JBIG2 Huffman table must have at least one active line.", nameof(lines));

        int maxPrefixLength = lineArray.Max(line => line.PrefixLength);
        var lengthCounts = new int[maxPrefixLength + 1];
        foreach (var line in lineArray)
        {
            if (line.PrefixLength > maxPrefixLength)
                throw new ArgumentOutOfRangeException(nameof(lines), line.PrefixLength, "Huffman prefix length exceeds table bounds.");

            lengthCounts[line.PrefixLength]++;
        }

        var firstCode = new uint[maxPrefixLength + 1];
        for (int length = 1; length <= maxPrefixLength; length++)
            firstCode[length] = (firstCode[length - 1] + (uint)lengthCounts[length - 1]) << 1;

        var nextCode = (uint[])firstCode.Clone();
        var entries = new List<Jbig2HuffmanTableEntry>(lineArray.Length);
        foreach (var line in lineArray)
        {
            uint prefixCode = nextCode[line.PrefixLength]++;
            entries.Add(new Jbig2HuffmanTableEntry(
                line.PrefixLength,
                prefixCode,
                line.IsOutOfBand ? 0 : line.RangeLength,
                line.RangeLow,
                line.IsOutOfBand,
                line.IsLowerRange));
        }

        return new Jbig2HuffmanTable(entries);
    }

    public long? Decode(Jbig2BitReader bitReader)
    {
        if (bitReader == null)
            throw new ArgumentNullException(nameof(bitReader));

        uint code = 0;
        for (int prefixLength = 1; prefixLength <= _maxPrefixLength; prefixLength++)
        {
            code = (code << 1) | (bitReader.ReadBit() ? 1u : 0u);
            if (!_entries.TryGetValue((prefixLength, code), out var entry))
                continue;

            if (entry.IsOutOfBand)
                return null;

            long rangeOffset = entry.RangeLength == 0
                ? 0
                : bitReader.ReadBits(entry.RangeLength);
            return entry.IsLowerRange
                ? entry.RangeLow - rangeOffset
                : entry.RangeLow + rangeOffset;
        }

        throw new InvalidOperationException("No JBIG2 Huffman table entry matched the encoded prefix.");
    }
}

internal static class Jbig2StandardHuffmanTables
{
    private static readonly Jbig2HuffmanTableLine[][] Tables =
    [
        // B1
        [
            new(1, 4, 0),
            new(2, 8, 16),
            new(3, 16, 272),
            new(3, 32, 65808),
        ],
        // B2
        [
            new(1, 0, 0),
            new(2, 0, 1),
            new(3, 0, 2),
            new(4, 3, 3),
            new(5, 6, 11),
            new(6, 32, 75),
            new(6, 0, 0, IsOutOfBand: true),
        ],
        // B3
        [
            new(8, 8, -256),
            new(1, 0, 0),
            new(2, 0, 1),
            new(3, 0, 2),
            new(4, 3, 3),
            new(5, 6, 11),
            new(8, 32, -257, IsLowerRange: true),
            new(7, 32, 75),
            new(6, 0, 0, IsOutOfBand: true),
        ],
        // B4
        [
            new(1, 0, 1),
            new(2, 0, 2),
            new(3, 0, 3),
            new(4, 3, 4),
            new(5, 6, 12),
            new(5, 32, 76),
        ],
        // B5
        [
            new(7, 8, -255),
            new(1, 0, 1),
            new(2, 0, 2),
            new(3, 0, 3),
            new(4, 3, 4),
            new(5, 6, 12),
            new(7, 32, -256, IsLowerRange: true),
            new(6, 32, 76),
        ],
        // B6
        [
            new(5, 10, -2048),
            new(4, 9, -1024),
            new(4, 8, -512),
            new(4, 7, -256),
            new(5, 6, -128),
            new(5, 5, -64),
            new(4, 5, -32),
            new(2, 7, 0),
            new(3, 7, 128),
            new(3, 8, 256),
            new(4, 9, 512),
            new(4, 10, 1024),
            new(6, 32, -2049, IsLowerRange: true),
            new(6, 32, 2048),
        ],
        // B7
        [
            new(4, 9, -1024),
            new(3, 8, -512),
            new(4, 7, -256),
            new(5, 6, -128),
            new(5, 5, -64),
            new(4, 5, -32),
            new(4, 5, 0),
            new(5, 5, 32),
            new(5, 6, 64),
            new(4, 7, 128),
            new(3, 8, 256),
            new(3, 9, 512),
            new(3, 10, 1024),
            new(5, 32, -1025, IsLowerRange: true),
            new(5, 32, 2048),
        ],
        // B8
        [
            new(8, 3, -15),
            new(9, 1, -7),
            new(8, 1, -5),
            new(9, 0, -3),
            new(7, 0, -2),
            new(4, 0, -1),
            new(2, 1, 0),
            new(5, 0, 2),
            new(6, 0, 3),
            new(3, 4, 4),
            new(6, 1, 20),
            new(4, 4, 22),
            new(4, 5, 38),
            new(5, 6, 70),
            new(5, 7, 134),
            new(6, 7, 262),
            new(7, 8, 390),
            new(6, 10, 646),
            new(9, 32, -16, IsLowerRange: true),
            new(9, 32, 1670),
            new(2, 0, 0, IsOutOfBand: true),
        ],
        // B9
        [
            new(8, 4, -31),
            new(9, 2, -15),
            new(8, 2, -11),
            new(9, 1, -7),
            new(7, 1, -5),
            new(4, 1, -3),
            new(3, 1, -1),
            new(3, 1, 1),
            new(5, 1, 3),
            new(6, 1, 5),
            new(3, 5, 7),
            new(6, 2, 39),
            new(4, 5, 43),
            new(4, 6, 75),
            new(5, 7, 139),
            new(5, 8, 267),
            new(6, 8, 523),
            new(7, 9, 779),
            new(6, 11, 1291),
            new(9, 32, -32, IsLowerRange: true),
            new(9, 32, 3339),
            new(2, 0, 0, IsOutOfBand: true),
        ],
        // B10
        [
            new(7, 4, -21),
            new(8, 0, -5),
            new(7, 0, -4),
            new(5, 0, -3),
            new(2, 2, -2),
            new(5, 0, 2),
            new(6, 0, 3),
            new(7, 0, 4),
            new(8, 0, 5),
            new(2, 6, 6),
            new(5, 5, 70),
            new(6, 5, 102),
            new(6, 6, 134),
            new(6, 7, 198),
            new(6, 8, 326),
            new(6, 9, 582),
            new(6, 10, 1094),
            new(7, 11, 2118),
            new(8, 32, -22, IsLowerRange: true),
            new(8, 32, 4166),
            new(2, 0, 0, IsOutOfBand: true),
        ],
        // B11
        [
            new(1, 0, 1),
            new(2, 1, 2),
            new(4, 0, 4),
            new(4, 1, 5),
            new(5, 1, 7),
            new(5, 2, 9),
            new(6, 2, 13),
            new(7, 2, 17),
            new(7, 3, 21),
            new(7, 4, 29),
            new(7, 5, 45),
            new(7, 6, 77),
            new(7, 32, 141),
        ],
        // B12
        [
            new(1, 0, 1),
            new(2, 0, 2),
            new(3, 1, 3),
            new(5, 0, 5),
            new(5, 1, 6),
            new(6, 1, 8),
            new(7, 0, 10),
            new(7, 1, 11),
            new(7, 2, 13),
            new(7, 3, 17),
            new(7, 4, 25),
            new(8, 5, 41),
            new(8, 32, 73),
        ],
        // B13
        [
            new(1, 0, 1),
            new(3, 0, 2),
            new(4, 0, 3),
            new(5, 0, 4),
            new(4, 1, 5),
            new(3, 3, 7),
            new(6, 1, 15),
            new(6, 2, 17),
            new(6, 3, 21),
            new(6, 4, 29),
            new(6, 5, 45),
            new(7, 6, 77),
            new(7, 32, 141),
        ],
        // B14
        [
            new(3, 0, -2),
            new(3, 0, -1),
            new(1, 0, 0),
            new(3, 0, 1),
            new(3, 0, 2),
        ],
        // B15
        [
            new(7, 4, -24),
            new(6, 2, -8),
            new(5, 1, -4),
            new(4, 0, -2),
            new(3, 0, -1),
            new(1, 0, 0),
            new(3, 0, 1),
            new(4, 0, 2),
            new(5, 1, 3),
            new(6, 2, 5),
            new(7, 4, 9),
            new(7, 32, -25, IsLowerRange: true),
            new(7, 32, 25),
        ],
    ];

    private static readonly Jbig2HuffmanTable?[] CachedTables = new Jbig2HuffmanTable[Tables.Length];

    public static Jbig2HuffmanTable Get(int tableNumber)
    {
        if (tableNumber < 1 || tableNumber > Tables.Length)
            throw new ArgumentOutOfRangeException(nameof(tableNumber), tableNumber, "JBIG2 standard Huffman table number must be 1-15.");

        return CachedTables[tableNumber - 1] ??= Jbig2HuffmanTable.FromCanonicalLines(Tables[tableNumber - 1]);
    }
}

internal static class Jbig2UserHuffmanTableBuilder
{
    public static Jbig2HuffmanTable Build(Jbig2HuffmanTableSegment segment, ReadOnlySpan<byte> payload)
    {
        var reader = new Jbig2BitReader(payload.ToArray());
        var lines = new List<Jbig2HuffmanTableLine>();
        long currentRangeLow = segment.LowValue;

        while (currentRangeLow < segment.HighValue)
        {
            int prefixLength = checked((int)reader.ReadBits(segment.PrefixSizeBits));
            int rangeLength = checked((int)reader.ReadBits(segment.RangeSizeBits));
            lines.Add(new Jbig2HuffmanTableLine(prefixLength, rangeLength, currentRangeLow));
            currentRangeLow += 1L << rangeLength;
        }

        int lowerPrefixLength = checked((int)reader.ReadBits(segment.PrefixSizeBits));
        lines.Add(new Jbig2HuffmanTableLine(lowerPrefixLength, 32, segment.LowValue - 1L, IsLowerRange: true));

        int upperPrefixLength = checked((int)reader.ReadBits(segment.PrefixSizeBits));
        lines.Add(new Jbig2HuffmanTableLine(upperPrefixLength, 32, segment.HighValue));

        if (segment.HasOutOfBand)
        {
            int oobPrefixLength = checked((int)reader.ReadBits(segment.PrefixSizeBits));
            lines.Add(new Jbig2HuffmanTableLine(oobPrefixLength, 0, 0, IsOutOfBand: true));
        }

        return Jbig2HuffmanTable.FromCanonicalLines(lines);
    }
}
