using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2EntropyDecoderTests
{
    [Fact]
    public void MqArithmeticDecoder_MaintainsIndependentContextStates()
    {
        var decoder = new Jbig2MqArithmeticDecoder(new byte[] { 0x00, 0x00, 0x00 }, contextCount: 4);

        int context0 = 0;
        int context1 = 1;

        decoder.Decode(ref context0).Should().BeFalse();
        decoder.Decode(ref context1).Should().BeTrue();
        decoder.GetContextStateForTest(1).Should().Be((1, 1));

        decoder.Decode(ref context0).Should().BeTrue();

        decoder.GetContextStateForTest(0).Should().Be((6, 0));
        decoder.GetContextStateForTest(1).Should().Be((1, 1));
    }

    [Fact]
    public void MqArithmeticDecoder_WithContextOutOfRange_Throws()
    {
        var decoder = new Jbig2MqArithmeticDecoder(new byte[] { 0x00, 0x00 }, contextCount: 1);
        int context = 1;

        var act = () => decoder.Decode(ref context);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MqArithmeticDecoder_WithMarkerAfterFF_DecodesWithoutAdvancingPastMarkerLoop()
    {
        var decoder = new Jbig2MqArithmeticDecoder(new byte[] { 0xFF, 0x90, 0x00 }, contextCount: 2);
        int context = 0;

        var act = () => decoder.Decode(ref context);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(new[] { false, false, false, false }, 0)]
    [InlineData(new[] { false, false, true, true }, 3)]
    [InlineData(new[] { true, true, false, false, false, true, true }, -7)]
    [InlineData(new[] { false, true, true, true, true, false, false, false, false, true, false, false, true, false, false, false, true, true }, 631)]
    public void ArithmeticIntegerDecoder_DecodesMagnitudeTiers(bool[] bits, long expected)
    {
        var decoder = new Jbig2ArithmeticIntegerDecoder(new ScriptedArithmeticDecoder(bits));

        long? value = decoder.Decode();

        value.Should().Be(expected);
    }

    [Fact]
    public void ArithmeticIntegerDecoder_NegativeZeroReturnsOutOfBand()
    {
        var decoder = new Jbig2ArithmeticIntegerDecoder(new ScriptedArithmeticDecoder(true, false, false, false));

        long? value = decoder.Decode();

        value.Should().BeNull();
    }

    [Fact]
    public void ArithmeticIntegerDecoder_UsesContextBaseAndPreviousState()
    {
        var scripted = new ScriptedArithmeticDecoder(false, false, true, true);
        var decoder = new Jbig2ArithmeticIntegerDecoder(scripted, contextBase: 1000);

        decoder.Decode().Should().Be(3);

        scripted.Contexts.Should().Equal(1001, 1002, 1004, 1009);
    }

    [Fact]
    public void IaidDecoder_DecodesFixedLengthSymbolId()
    {
        var decoder = new Jbig2IaidDecoder(new ScriptedArithmeticDecoder(true, false, true));

        uint symbolId = decoder.Decode(codeLength: 3);

        symbolId.Should().Be(5);
    }

    [Fact]
    public void IaidDecoder_UsesContextBaseAndPreviousState()
    {
        var scripted = new ScriptedArithmeticDecoder(true, false, true);
        var decoder = new Jbig2IaidDecoder(scripted, contextBase: 200);

        decoder.Decode(codeLength: 3).Should().Be(5);

        scripted.Contexts.Should().Equal(201, 203, 206);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void IaidDecoder_WithUnsupportedCodeLength_Throws(int codeLength)
    {
        var decoder = new Jbig2IaidDecoder(new ScriptedArithmeticDecoder(false));

        var act = () => decoder.Decode(codeLength);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BitReader_ReadBits_ReadsMsbFirst()
    {
        var reader = new Jbig2BitReader(new byte[] { 0b1010_1100, 0b0110_0000 });

        reader.ReadBits(3).Should().Be(0b101);
        reader.ReadBits(5).Should().Be(0b0_1100);
        reader.ReadBits(4).Should().Be(0b0110);
        reader.BitsRemaining.Should().Be(4);
    }

    [Fact]
    public void BitReader_AlignToByte_SkipsPartialByteRemainder()
    {
        var reader = new Jbig2BitReader(new byte[] { 0b1010_1100, 0b0110_0000 });

        reader.ReadBits(3).Should().Be(0b101);
        reader.AlignToByte();

        reader.ReadBits(4).Should().Be(0b0110);
    }

    [Fact]
    public void HuffmanTable_DecodesPrefixAndRangeTail()
    {
        var table = MakeSyntheticHuffmanTable();
        var reader = new Jbig2BitReader(new byte[] { 0b1010_0000 });

        long? value = table.Decode(reader);

        value.Should().Be(6);
    }

    [Fact]
    public void HuffmanTable_FromCanonicalLines_AssignsCodesByPrefixLength()
    {
        var table = Jbig2HuffmanTable.FromCanonicalLines(
        [
            new Jbig2HuffmanTableLine(1, 0, 0),
            new Jbig2HuffmanTableLine(2, 0, 1),
            new Jbig2HuffmanTableLine(3, 0, 2),
            new Jbig2HuffmanTableLine(3, 0, 3),
        ]);

        table.Decode(Bits("0")).Should().Be(0);
        table.Decode(Bits("10")).Should().Be(1);
        table.Decode(Bits("110")).Should().Be(2);
        table.Decode(Bits("111")).Should().Be(3);
    }

    [Fact]
    public void HuffmanTable_LowerRange_SubtractsRangeTail()
    {
        var table = Jbig2HuffmanTable.FromCanonicalLines(
        [
            new Jbig2HuffmanTableLine(1, 2, 10, IsLowerRange: true),
        ]);

        table.Decode(Bits("011")).Should().Be(7);
    }

    [Fact]
    public void HuffmanTable_DecodesOutOfBandEntry()
    {
        var table = MakeSyntheticHuffmanTable();
        var reader = new Jbig2BitReader(new byte[] { 0b1110_0000 });

        long? value = table.Decode(reader);

        value.Should().BeNull();
    }

    [Fact]
    public void HuffmanTable_WithNoMatchingPrefix_Throws()
    {
        var table = new Jbig2HuffmanTable(
        [
            new Jbig2HuffmanTableEntry(2, 0b00, 0, 0),
        ]);
        var reader = new Jbig2BitReader(new byte[] { 0b1000_0000 });

        var act = () => table.Decode(reader);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No JBIG2 Huffman table entry*");
    }

    [Fact]
    public void StandardHuffmanTableB1_DecodesRepresentativeRanges()
    {
        var table = Jbig2StandardHuffmanTables.Get(1);

        table.Decode(Bits("0" + "1111")).Should().Be(15);
        table.Decode(Bits("10" + "00000000")).Should().Be(16);
        table.Decode(Bits("110" + "0000000000000000")).Should().Be(272);
        table.Decode(Bits("111" + "00000000000000000000000000000000")).Should().Be(65808);
    }

    [Fact]
    public void StandardHuffmanTableB2_DecodesRangeAndOutOfBand()
    {
        var table = Jbig2StandardHuffmanTables.Get(2);

        table.Decode(Bits("1110" + "101")).Should().Be(8);
        table.Decode(Bits("111111")).Should().BeNull();
    }

    [Fact]
    public void StandardHuffmanTableB5_DecodesLowerRange()
    {
        var table = Jbig2StandardHuffmanTables.Get(5);

        table.Decode(Bits("1111111" + "00000000000000000000000000000001")).Should().Be(-257);
    }

    [Fact]
    public void StandardHuffmanTables_DecodeSimpleSymbolDictionarySequence()
    {
        var reader = Bits("0" + "10" + "111111" + "0" + "0000");

        Jbig2StandardHuffmanTables.Get(4).Decode(reader).Should().Be(1);
        Jbig2StandardHuffmanTables.Get(2).Decode(reader).Should().Be(1);
        Jbig2StandardHuffmanTables.Get(2).Decode(reader).Should().BeNull();
        Jbig2StandardHuffmanTables.Get(1).Decode(reader).Should().Be(0);
    }

    [Fact]
    public void StandardHuffmanTable_WithInvalidNumber_Throws()
    {
        var act = () => Jbig2StandardHuffmanTables.Get(16);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UserHuffmanTableBuilder_BuildsEncodedTable()
    {
        var segment = new Jbig2HuffmanTableSegment(
            HasOutOfBand: false,
            PrefixSizeBits: 3,
            RangeSizeBits: 2,
            LowValue: 0,
            HighValue: 4,
            PayloadDataOffset: 9,
            PayloadDataLength: 2);
        byte[] payload = PackBits("001" + "10" + "010" + "010");

        var table = Jbig2UserHuffmanTableBuilder.Build(segment, payload);

        table.Decode(Bits("0" + "11")).Should().Be(3);
        table.Decode(Bits("10" + "00000000000000000000000000000001")).Should().Be(-2);
        table.Decode(Bits("11" + "00000000000000000000000000000010")).Should().Be(6);
    }

    [Fact]
    public void UserHuffmanTableBuilder_BuildsOutOfBandLine()
    {
        var segment = new Jbig2HuffmanTableSegment(
            HasOutOfBand: true,
            PrefixSizeBits: 3,
            RangeSizeBits: 2,
            LowValue: 0,
            HighValue: 4,
            PayloadDataOffset: 9,
            PayloadDataLength: 2);
        byte[] payload = PackBits("001" + "10" + "011" + "011" + "011");

        var table = Jbig2UserHuffmanTableBuilder.Build(segment, payload);

        table.Decode(Bits("110")).Should().BeNull();
    }

    private static Jbig2HuffmanTable MakeSyntheticHuffmanTable()
        => new(
        [
            new Jbig2HuffmanTableEntry(1, 0b0, 0, 0),
            new Jbig2HuffmanTableEntry(2, 0b10, 2, 4),
            new Jbig2HuffmanTableEntry(3, 0b110, 0, -1),
            new Jbig2HuffmanTableEntry(3, 0b111, 0, 0, IsOutOfBand: true),
        ]);

    private static Jbig2BitReader Bits(string bits)
        => new(PackBits(bits));

    private static byte[] PackBits(string bits)
    {
        int byteCount = (bits.Length + 7) / 8;
        byte[] data = new byte[byteCount];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                data[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }

        return data;
    }

    private sealed class ScriptedArithmeticDecoder : IJbig2ArithmeticDecoder
    {
        private readonly Queue<bool> _bits;

        public ScriptedArithmeticDecoder(params bool[] bits)
        {
            _bits = new Queue<bool>(bits);
        }

        public List<int> Contexts { get; } = [];

        public bool Decode(ref int context)
        {
            Contexts.Add(context);
            if (_bits.Count == 0)
                throw new InvalidOperationException("Scripted arithmetic decoder exhausted");

            return _bits.Dequeue();
        }
    }
}
