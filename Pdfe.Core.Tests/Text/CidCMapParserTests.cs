using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Text;

public class CidCMapParserTests
{
    [Fact]
    public void Parse_BfCharType0EncodingMap_DecodesCharacterCodesToCids()
    {
        var cmapData = Encoding.UTF8.GetBytes("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            3 beginbfchar
            <0020> <0003>
            <0043> <0026>
            <0068> <004b>
            endbfchar
            endcmap
            end
            end
            """);

        var cmap = CidCMap.Parse(cmapData);

        cmap.Mapping[0x0020].Should().Be(0x0003);
        cmap.Mapping[0x0043].Should().Be(0x0026);
        cmap.Mapping[0x0068].Should().Be(0x004b);
        cmap.Decode([0x00, 0x43, 0x00, 0x68, 0x00, 0x20])
            .Should().Equal(0x0026, 0x004b, 0x0003);
    }

    [Fact]
    public void Parse_CidRangeAndBfRange_DecodesIncrementingAndArrayRanges()
    {
        var cmap = CidCMap.Parse("""
            2 begincodespacerange
            <00> <7f>
            <8100> <81ff>
            endcodespacerange
            1 begincidrange
            <41> <43> 100
            endcidrange
            1 beginbfrange
            <8101> <8103> [<0200> <0205> <0209>]
            endbfrange
            """);

        cmap.Decode([0x41, 0x42, 0x43, 0x81, 0x01, 0x81, 0x02, 0x81, 0x03])
            .Should().Equal(100, 101, 102, 0x0200, 0x0205, 0x0209);
    }

    [Fact]
    public void Parse_UseCMapIdentityH_InheritsTwoByteCodespace()
    {
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <20> <7f>
            endcodespacerange
            /Identity-H usecmap
            """);

        cmap.CodespaceRanges.Should().Contain(r => r.Low == 0 && r.High == 0xffff && r.Bytes == 2);
        cmap.Decode([0x00, 0x41, 0x00, 0x42])
            .Should().Equal(0x0041, 0x0042);
    }

    // The tests below pin the parser/decoder edge branches that previously
    // ran only under corpus fixtures (which CI does not download) — added
    // while closing the CI coverage-gate shortfall for v2.30.0, but each
    // asserts real spec behavior, not just line execution.

    [Fact]
    public void Parse_CidCharAndDecimalDestinations_MapIndividualCodes()
    {
        // begincidchar (not bfchar) with a DECIMAL destination — both the
        // cidchar keyword branch and TryGetCid's Number branch.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            2 begincidchar
            <0041> 7
            <0042> <000A>
            endcidchar
            """);

        cmap.Mapping[0x0041].Should().Be(7);
        cmap.Mapping[0x0042].Should().Be(10);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsEmpty()
    {
        CidCMap.Parse("1 begincidchar <41> 1 endcidchar").Decode([]).Should().BeEmpty();
    }

    [Fact]
    public void Decode_NoCodespacesDeclared_DefaultsToTwoByteIdentity()
    {
        // A CMap with mappings but no begincodespacerange must decode
        // 2 bytes at a time per the Identity default.
        var cmap = CidCMap.Parse("1 begincidchar <0041> 900 endcidchar");
        cmap.Decode([0x00, 0x41, 0x00, 0x99]).Should().Equal(900, 0x0099);
    }

    [Fact]
    public void Decode_ByteOutsideEveryCodespace_FallsBackWithoutLosingData()
    {
        // 0xFF is outside the declared 1-byte <00> <7f> codespace; the
        // decoder's fallback must still consume input (2 bytes when
        // available) instead of looping or dropping bytes.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <00> <7f>
            endcodespacerange
            """);

        cmap.Decode([0x41, 0xFF, 0x41]).Should().Equal(0x41, 0xFF41);
        // Trailing single out-of-space byte: 1-byte fallback.
        cmap.Decode([0xFF]).Should().Equal(0xFF);
    }

    [Fact]
    public void Parse_CommentsAndStringLiterals_AreSkippedByTheTokenizer()
    {
        // % comments and (...) string literals (with nesting and escapes)
        // appear in real CMap prologues; the tokenizer must skip both
        // without corrupting subsequent tokens.
        var cmap = CidCMap.Parse("""
            %%BeginResource: CMap (Custom)
            /Notice (a (nested) literal with \) an escaped paren) def
            1 begincodespacerange
            <00> <ff>
            endcodespacerange
            1 begincidchar
            <41> 5
            endcidchar
            """);

        cmap.Mapping[0x41].Should().Be(5);
        cmap.Decode([0x41]).Should().Equal(5);
    }

    [Fact]
    public void Parse_MalformedSections_StopParsingThatSectionButKeepTheRest()
    {
        // A cidrange whose destination is garbage (a Name, not hex/number)
        // must abort that section without throwing and without poisoning a
        // later, well-formed section.
        var cmap = CidCMap.Parse("""
            1 begincidrange
            <41> <43> /NotACid
            endcidrange
            1 begincidchar
            <50> 77
            endcidchar
            """);

        cmap.Mapping.Should().NotContainKey(0x41);
        cmap.Mapping[0x50].Should().Be(77);
    }

    [Fact]
    public void Parse_DescendingRange_IsIgnoredRatherThanLooping()
    {
        var cmap = CidCMap.Parse("""
            1 begincidrange
            <43> <41> 100
            endcidrange
            """);

        cmap.Mapping.Should().BeEmpty("high < low is a malformed range, not an infinite loop");
    }

    [Fact]
    public void Parse_BfRangeArrayShorterThanRange_MapsOnlyProvidedEntries()
    {
        var cmap = CidCMap.Parse("""
            1 beginbfrange
            <41> <45> [<0100> <0101>]
            endbfrange
            """);

        cmap.Mapping[0x41].Should().Be(0x0100);
        cmap.Mapping[0x42].Should().Be(0x0101);
        cmap.Mapping.Should().NotContainKey(0x43, "the array ran out — no invented mappings");
    }

    [Fact]
    public void Parse_OddLengthHexAndNegativeNumbers_AreTolerated()
    {
        // Odd-length hex gets an implied leading zero (spec-tolerant), and a
        // negative decimal destination parses via the number token path.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <0> <f>
            endcodespacerange
            1 begincidchar
            <A> -1
            endcidchar
            """);

        cmap.Mapping[0x0A].Should().Be(-1);
    }

    [Fact]
    public void Parse_UseCMapUnknownName_DoesNotAddCodespaces()
    {
        var cmap = CidCMap.Parse("/SomeUnknown-CMap usecmap");
        cmap.CodespaceRanges.Should().BeEmpty(
            "only the Identity CMaps are predefined; unknown usecmap names contribute nothing");
    }

    [Fact]
    public void Parse_TruncatedSections_DoNotThrow()
    {
        // Sections cut off mid-pair/mid-triple (real-world truncated
        // streams) must parse to whatever was complete, without exceptions.
        var truncatedPairs = CidCMap.Parse("2 begincidchar <41> 1 <42>");
        truncatedPairs.Mapping[0x41].Should().Be(1);
        truncatedPairs.Mapping.Should().NotContainKey(0x42);

        var truncatedTriple = CidCMap.Parse("1 begincidrange <41> <43>");
        truncatedTriple.Mapping.Should().BeEmpty();

        var truncatedCodespace = CidCMap.Parse("1 begincodespacerange <00>");
        truncatedCodespace.CodespaceRanges.Should().BeEmpty();
    }
}
