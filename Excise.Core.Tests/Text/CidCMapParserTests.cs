using System.Text;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

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
        // 0xFF is outside the declared 1-byte <00> <7f> codespace AND no
        // codespace's lead-byte range claims it, so the invalid code
        // consumes exactly ONE byte (Adobe TN #5014 undefined-code
        // handling, matching mutool/pdf.js) — the decoder must not pair it
        // with the following valid byte, and must not loop or drop input.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <00> <7f>
            endcodespacerange
            """);

        cmap.Decode([0x41, 0xFF, 0x41]).Should().Equal(0x41, 0xFF, 0x41);
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

    // ── #515 slice 4: per-byte-range codespace matching (§9.7.6.2) ──────────
    //
    // Expected sequences below are computed from the CMap spec by hand, not
    // from excise: a codespace <lo> <hi> contains a code only when EVERY byte
    // lies within the corresponding byte range of the bounds — NOT when the
    // code's scalar value lies within [lo, hi].

    [Fact]
    public void Decode_CodespaceMatchIsByteWise_NotScalar()
    {
        // GBK-EUC-H-shaped mixed-width codespaces, plus a crafted 1-byte
        // space wide enough to catch what the 2-byte space rejects.
        var cmap = CidCMap.Parse("""
            2 begincodespacerange
            <00> <FF>
            <8140> <FEFE>
            endcodespacerange
            2 begincidchar
            <81> 900
            <FF> 901
            endcidchar
            1 begincidrange
            <8141> <8149> 100
            endcidrange
            """);

        // <8141>: byte 0 ∈ [81,FE], byte 1 ∈ [40,FE] → one 2-byte code.
        cmap.DecodeDetailed([0x81, 0x41]).Should().Equal((0x8141, 100, 2));

        // <81FF>: scalar-wise inside [0x8140, 0xFEFE] — the historical bug —
        // but byte 1 (0xFF) is OUTSIDE [0x40, 0xFE], so the 2-byte space
        // must NOT claim it. Both bytes fall to the 1-byte codespace and hit
        // their cidchar mappings.
        cmap.DecodeDetailed([0x81, 0xFF]).Should().Equal((0x81, 900, 1), (0xFF, 901, 1));

        // Mixed stream around the invalid pair keeps byte-exact segmentation.
        cmap.DecodeDetailed([0x81, 0x41, 0x81, 0xFF, 0x81, 0x49])
            .Should().Equal((0x8141, 100, 2), (0x81, 900, 1), (0xFF, 901, 1), (0x8149, 108, 2));
    }

    [Fact]
    public void Decode_ByteWiseInvalidCode_ConsumesLeadByteCodespaceWidth()
    {
        // Only a 2-byte codespace: an invalid code whose LEAD byte the
        // codespace claims consumes the codespace's full width (Adobe TN
        // #5014 undefined-code handling); a lead byte nothing claims
        // consumes exactly one byte.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <8140> <FEFE>
            endcodespacerange
            """);

        // 0x81 ∈ [81,FE] → the invalid pair <81FF> is consumed as 2 bytes.
        cmap.DecodeDetailed([0x81, 0xFF, 0x81, 0x41])
            .Should().Equal((0x81FF, 0x81FF, 2), (0x8141, 0x8141, 2));

        // 0xFF ∉ [81,FE] → single-byte consumption, and the following byte
        // is re-examined on its own instead of being swallowed.
        cmap.DecodeDetailed([0xFF, 0x41])
            .Should().Equal((0xFF, 0xFF, 1), (0x41, 0x41, 1));
    }

    [Fact]
    public void Decode_TruncatedTrailingCode_ConsumesRemainingByte()
    {
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <8140> <FEFE>
            endcodespacerange
            """);

        // A dangling lead byte at end-of-input cannot form a 2-byte code;
        // it must be consumed as a 1-byte fallback, not dropped.
        cmap.DecodeDetailed([0x81, 0x41, 0x81])
            .Should().Equal((0x8141, 0x8141, 2), (0x81, 0x81, 1));
    }

    [Fact]
    public void Decode_RealRksjCMap_RejectsByteWiseInvalidTrailByte()
    {
        // The shipped 90ms-RKSJ-H declares <8140> <9FFC> / <E040> <FCFC>
        // 2-byte spaces. <81FF> is scalar-inside [0x8140, 0x9FFC] but its
        // trail byte 0xFF exceeds 0xFC — per-byte matching must treat it as
        // an invalid code (2 bytes via the lead), NOT as a valid member
        // eligible for cidrange mapping.
        var cmap = PredefinedCMapProvider.TryGetEncodingCMap("90ms-RKSJ-H");
        cmap.Should().NotBeNull();

        var decoded = cmap!.DecodeDetailed([0x81, 0xFF, 0x41]);
        decoded.Should().HaveCount(2);
        decoded[0].ByteLength.Should().Be(2, "the lead byte 0x81 claims the 2-byte width");
        decoded[0].Code.Should().Be(0x81FF);
        decoded[1].Should().Be((0x41, 264, 1), "the following ASCII byte decodes normally");
    }

    // ── #515 slice 4: malformed-CMap resilience ─────────────────────────────

    [Fact(Timeout = 30000)]
    public void Parse_HugeCidRange_IsCappedWithoutHangingOrExhaustingMemory()
    {
        // A hostile range spanning the whole positive int space: a naive
        // scalar expansion would insert 2^31 entries (OOM) — and with
        // <FFFFFFFF> as the bound an int loop counter would wrap and never
        // terminate. The parser must cap the expansion and keep working.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <00000000> <FFFFFFFF>
            endcodespacerange
            1 begincidrange
            <00000000> <7FFFFFFF> 5
            endcidrange
            """);

        cmap.Mapping.Count.Should().BeLessThanOrEqualTo(65536);
        cmap.Mapping[0].Should().Be(5);
        cmap.Mapping[0xFFFF].Should().Be(5 + 0xFFFF, "the cap keeps a full 64K prefix intact");
    }

    [Fact(Timeout = 30000)]
    public void Parse_FullTwoByteRange_SurvivesTheCapIntact()
    {
        // The largest legitimate incrementing range must not be truncated.
        var cmap = CidCMap.Parse("""
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            1 begincidrange
            <0000> <FFFF> 0
            endcidrange
            """);

        cmap.Mapping.Count.Should().Be(65536);
        cmap.Mapping[0xFFFF].Should().Be(0xFFFF);
    }

    [Fact(Timeout = 30000)]
    public void Parse_JunkContent_DoesNotThrowOrHang()
    {
        // Assorted garbage: binary noise, over-long hex bounds, dangling
        // delimiters, an unterminated string literal. Graceful no-crash
        // parsing is all that is required.
        var junk = new[]
        {
            "\u0000\u0001\u0002<<<<>>>>[[[]]]",
            "1 begincodespacerange <112233445566778899AABB> <FFFFFFFFFFFFFFFFFFFFFF> endcodespacerange",
            "1 begincidrange <41> <40> 7 endcidrange",   // hi < lo
            "begincmap endcmap (unterminated literal",
            "/ /// %%% < > <zz> begincidchar",
            "1 beginbfrange <0000> <FFFFFFFF> <0041> endbfrange",
        };

        foreach (var content in junk)
        {
            var cmap = CidCMap.Parse(content);
            // Decoding arbitrary bytes over whatever survived must also work.
            cmap.Decode([0x41, 0xFF, 0x00]).Should().NotBeNull();
        }
    }
}
