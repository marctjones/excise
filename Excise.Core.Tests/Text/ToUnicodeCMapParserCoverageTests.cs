using AwesomeAssertions;
using Excise.Core.Text;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Additional coverage tests for ToUnicodeCMapParser.cs to push coverage from 91.5% to >95%.
/// Focuses on uncovered paths: ParseDetailed static method, Mapping and CodespaceRanges getters,
/// bfrange array form with multi-character entries, surrogate-pair destinations, edge cases,
/// and HexToInt with odd-length hex strings. Also covers string-literal tokenization (lines 276-289).
/// </summary>
public class ToUnicodeCMapParserCoverageTests
{
    /// <summary>
    /// Hit ParseDetailed(byte[]) static method and verify Mapping getter is populated.
    /// </summary>
    [Fact]
    public void ParseDetailed_ReturnsParserInstance_WithMappingPopulated()
    {
        // Arrange
        var cmapData = Encoding.UTF8.GetBytes(@"
            1 beginbfchar
            <0041> <0041>
            endbfchar
        ");

        // Act
        var parser = ToUnicodeCMapParser.ParseDetailed(cmapData);

        // Assert
        parser.Mapping.Should().NotBeNull();
        parser.Mapping.Should().HaveCount(1);
        parser.Mapping[0x0041].Should().Be("A");
    }

    /// <summary>
    /// Hit CodespaceRanges getter; verify it contains parsed codespace ranges.
    /// </summary>
    [Fact]
    public void ParseDetailed_CodespaceRanges_ArePopulated()
    {
        // Arrange
        var cmapData = Encoding.UTF8.GetBytes(@"
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            1 beginbfchar
            <0041> <0041>
            endbfchar
        ");

        // Act
        var parser = ToUnicodeCMapParser.ParseDetailed(cmapData);

        // Assert
        parser.CodespaceRanges.Should().NotBeEmpty();
        parser.CodespaceRanges.Should().HaveCount(1);
        var range = parser.CodespaceRanges[0];
        range.Low.Should().Be(0x0000);
        range.High.Should().Be(0xFFFF);
        range.Bytes.Should().Be(2);
    }

    /// <summary>
    /// Hit MaxCodeBytes getter; verify it reflects the largest codespace range.
    /// </summary>
    [Fact]
    public void ParseDetailed_MaxCodeBytes_ReflectsLargestCodespace()
    {
        // Arrange
        var cmapData = Encoding.UTF8.GetBytes(@"
            2 begincodespacerange
            <00> <FF>
            <0000> <FFFF>
            endcodespacerange
        ");

        // Act
        var parser = ToUnicodeCMapParser.ParseDetailed(cmapData);

        // Assert
        parser.MaxCodeBytes.Should().Be(2); // 2 bytes for <FFFF>
    }

    /// <summary>
    /// Hit bfrange array form with multiple multi-character entries.
    /// Example: <0001><0003>[<00410042><0043><0044>]
    /// Source 0x0001 → "AB", 0x0002 → "C", 0x0003 → "D"
    /// </summary>
    [Fact]
    public void ParseBfrange_ArrayFormWithMultiCharEntries_MapsCorrectly()
    {
        // Arrange
        var cmap = @"
            1 beginbfrange
            <0001> <0003> [<00410042> <0043> <0044>]
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(3);
        result[0x0001].Should().Be("AB"); // Multi-char destination (00410042 = "AB")
        result[0x0002].Should().Be("C");  // (0043 = "C")
        result[0x0003].Should().Be("D");  // (0044 = "D")
    }

    /// <summary>
    /// Hit surrogate-pair destinations in bfchar.
    /// Single mapping like <0001><D83DDE00> → "😀" (U+1F600).
    /// </summary>
    [Fact]
    public void ParseBfchar_SurrogatePairDestination_DecodesToEmoji()
    {
        // Arrange - D83D DE00 is UTF-16BE for U+1F600 (😀 grinning face)
        var cmap = @"
            1 beginbfchar
            <0001> <D83DDE00>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0001].Should().Be("😀");
    }

    /// <summary>
    /// Hit HexToInt with odd-length input (single hex digit).
    /// <F> should be interpreted as 0x0F (left-padded to "0F").
    /// </summary>
    [Fact]
    public void Parse_OddLengthHexString_LeftPadded()
    {
        // Arrange - <F> is odd length; should pad to <0F>
        var cmap = @"
            1 beginbfchar
            <F> <0041>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0F].Should().Be("A");
    }

    /// <summary>
    /// Hit empty hex-string source: <><> should not crash and produce no mappings.
    /// </summary>
    [Fact]
    public void ParseBfchar_EmptyHexString_NoException()
    {
        // Arrange
        var cmap = @"
            1 beginbfchar
            <> <>
            endbfchar
        ";

        // Act & Assert - should not throw
        var result = ToUnicodeCMapParser.Parse(cmap);
        result.Should().NotBeNull();
    }

    /// <summary>
    /// CMap with both bfchar and bfrange in many alternating blocks.
    /// Verifies both data structures get populated correctly.
    /// </summary>
    [Fact]
    public void Parse_AlternatingBfcharAndBfrange_PopulatesCorrectly()
    {
        // Arrange
        var cmap = @"
            1 beginbfchar
            <0001> <0041>
            endbfchar
            1 beginbfrange
            <0002> <0003> <0042>
            endbfrange
            1 beginbfchar
            <0004> <0044>
            endbfchar
            1 beginbfrange
            <0005> <0006> <0045>
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(6);
        result[0x0001].Should().Be("A");
        result[0x0002].Should().Be("B");
        result[0x0003].Should().Be("C");
        result[0x0004].Should().Be("D");
        result[0x0005].Should().Be("E");
        result[0x0006].Should().Be("F");
    }

    /// <summary>
    /// CMap that opens beginbfchar but truncates (missing endbfchar).
    /// Parser should handle gracefully and return partial results.
    /// </summary>
    [Fact]
    public void Parse_TruncatedBfcharBlock_ReturnsPartialResults()
    {
        // Arrange
        var cmap = @"
            2 beginbfchar
            <0041> <0041>
            <0042> <0042>
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(2);
        result[0x0041].Should().Be("A");
        result[0x0042].Should().Be("B");
    }

    /// <summary>
    /// Hit multiple codespace ranges in a single begincodespacerange block.
    /// </summary>
    [Fact]
    public void ParseDetailed_MultipleCodespaceRanges_AllPopulated()
    {
        // Arrange
        var cmapData = Encoding.UTF8.GetBytes(@"
            3 begincodespacerange
            <00> <FF>
            <0000> <FFFF>
            <000000> <FFFFFF>
            endcodespacerange
        ");

        // Act
        var parser = ToUnicodeCMapParser.ParseDetailed(cmapData);

        // Assert
        parser.CodespaceRanges.Should().HaveCount(3);
        parser.CodespaceRanges[0].Bytes.Should().Be(1);
        parser.CodespaceRanges[1].Bytes.Should().Be(2);
        parser.CodespaceRanges[2].Bytes.Should().Be(3);
        parser.MaxCodeBytes.Should().Be(3);
    }

    /// <summary>
    /// bfrange with array form where the array has fewer entries than the range span.
    /// Only the first N entries should be mapped.
    /// </summary>
    [Fact]
    public void ParseBfrange_ArrayFormPartialArray_OnlyMapsPresent()
    {
        // Arrange - <0041><0044> with array [<0001> <0002>] maps only 0x0041, 0x0042
        var cmap = @"
            1 beginbfrange
            <0041> <0044> [<0001> <0002>]
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert - only 2 entries should be populated
        result.Should().HaveCount(2);
        result[0x0041].Should().HaveLength(1); // <0001> = U+0001 (SOH control char)
        result[0x0042].Should().HaveLength(1); // <0002> = U+0002 (STX control char)
    }

    /// <summary>
    /// Complex CMap with codespacerange, multiple bfchar blocks, and bfrange blocks.
    /// Exercises full parsing path.
    /// </summary>
    [Fact]
    public void Parse_ComplexMultiBlockCMap_FullParsing()
    {
        // Arrange
        var cmap = @"
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            2 beginbfchar
            <0001> <0041>
            <0002> <0042>
            endbfchar
            2 beginbfrange
            <0003> <0004> <0043>
            <0010> <0012> [<0050> <0051> <0052>]
            endbfrange
            1 beginbfchar
            <0020> <0070>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert: 2 (bfchar A B) + 2 (range C D) + 3 (array range P Q R) + 1 (bfchar p) = 8
        result.Should().HaveCount(8);
        result[0x0001].Should().Be("A");
        result[0x0002].Should().Be("B");
        result[0x0003].Should().Be("C");
        result[0x0004].Should().Be("D");
        result[0x0010].Should().Be("P");
        result[0x0011].Should().Be("Q");
        result[0x0012].Should().Be("R");
        result[0x0020].Should().Be("p");
    }

    /// <summary>
    /// Single-byte hex string destination (edge case in HexToUnicodeString).
    /// <F0> (one byte) should be treated as Latin-1, not UTF-16BE.
    /// </summary>
    [Fact]
    public void ParseBfchar_SingleByteDestination_TreatsAsLatin1()
    {
        // Arrange - <F0> is 1 byte (odd count initially, padded to "F0")
        var cmap = @"
            1 beginbfchar
            <0001> <F0>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0001].Should().Be("ð"); // U+00F0 = ð
    }

    /// <summary>
    /// bfrange with incrementing destination and multi-character prefix.
    /// Example: <0001><0003><00410042> → 0x0001="AB", 0x0002="AC", 0x0003="AD"
    /// Tests the surrogate-pair handling in incrementing ranges.
    /// </summary>
    [Fact]
    public void ParseBfrange_IncrementingMultiCharDestination_PreservesPrefix()
    {
        // Arrange - <0001><0003><00410042> (source) → "AB" then increment last codepoint
        var cmap = @"
            1 beginbfrange
            <0001> <0003> <00410042>
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(3);
        result[0x0001].Should().Be("AB");
        result[0x0002].Should().Be("AC");
        result[0x0003].Should().Be("AD");
    }

    /// <summary>
    /// ParseDetailed with byte[] produces same mapping as Parse(string).
    /// </summary>
    [Fact]
    public void ParseDetailed_ByteArray_ProducesSameMappingAsParseString()
    {
        // Arrange
        var cmapStr = @"
            1 beginbfchar
            <0041> <0041>
            <0042> <0042>
            endbfchar
        ";
        var cmapBytes = Encoding.UTF8.GetBytes(cmapStr);

        // Act
        var mappingStr = ToUnicodeCMapParser.Parse(cmapStr);
        var parserBytes = ToUnicodeCMapParser.ParseDetailed(cmapBytes);

        // Assert
        parserBytes.Mapping.Should().Equal(mappingStr);
    }

    /// <summary>
    /// Empty beginbfrange block (no entries).
    /// </summary>
    [Fact]
    public void Parse_EmptyBfrangeBlock_NoException()
    {
        // Arrange
        var cmap = @"
            0 beginbfrange
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().NotBeNull();
    }

    /// <summary>
    /// CMap with comments (% to end of line).
    /// Parser should skip them correctly.
    /// </summary>
    [Fact]
    public void Parse_WithComments_SkipsCorrectly()
    {
        // Arrange
        var cmap = @"
            % This is a comment
            1 beginbfchar
            % Another comment
            <0041> <0041>
            % Comment at end
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0041].Should().Be("A");
    }

    /// <summary>
    /// CMap with real-world complexity: mixed case hex, extra whitespace, multiple sections.
    /// </summary>
    [Fact]
    public void Parse_RealWorldComplexCMap_FullParsing()
    {
        // Arrange
        var cmap = @"
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo
            << /Registry (Adobe)
               /Ordering (Identity-UCS)
               /Supplement 0 >>
            def
            /CMapName /Test-CMap def
            /CMapType 2 def

            2 begincodespacerange
            <0000> <00FF>
            <0100> <FFFF>
            endcodespacerange

            3 beginbfchar
            <0001> <0041>
            <0002> <0042>
            <FF00> <4E2D>
            endbfchar

            2 beginbfrange
            <0010> <0012> <0050>
            <0100> <0102> [<00430044><0045>]
            endbfrange

            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(8);
        result[0x0001].Should().Be("A");
        result[0x0002].Should().Be("B");
        result[0xFF00].Should().Be("中");
        result[0x0010].Should().Be("P");
        result[0x0012].Should().Be("R");
        result[0x0100].Should().Be("CD");
        result[0x0101].Should().Be("E");
    }

    /// <summary>
    /// Very large codespace range (e.g., 4-byte codes).
    /// Verify MaxCodeBytes is set correctly.
    /// </summary>
    [Fact]
    public void ParseDetailed_FourByteCodespace_MaxCodeBytesCorrect()
    {
        // Arrange
        var cmapData = Encoding.UTF8.GetBytes(@"
            1 begincodespacerange
            <00000000> <FFFFFFFF>
            endcodespacerange
        ");

        // Act
        var parser = ToUnicodeCMapParser.ParseDetailed(cmapData);

        // Assert
        parser.MaxCodeBytes.Should().Be(4);
        parser.CodespaceRanges.Should().HaveCount(1);
        parser.CodespaceRanges[0].Bytes.Should().Be(4);
    }

    // ── String Literal Tokenization (Lines 276-289) Coverage Tests ──
    // These tests exercise the string-literal handling in Tokenize() which is rare
    // but valid PDF syntax. String literals are skipped (not added to token list)
    // because CMaps don't use them semantically.

    /// <summary>
    /// Hit lines 276-289: Simple string literal (no nesting, no escapes).
    /// Parser should skip it and continue to next meaningful token.
    /// Verifies that the parser doesn't crash and continues tokenization after the string.
    /// </summary>
    [Fact]
    public void Parse_SimpleStringLiteralInCMap_SkippedCorrectly()
    {
        // Arrange - CMap with a string literal followed by valid mapping
        var cmap = @"
            (This is a PDF string literal)
            1 beginbfchar
            <0001> <0041>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert - parser should skip the string literal and process the mapping
        result.Should().HaveCount(1);
        result[0x0001].Should().Be("A");
    }

    /// <summary>
    /// Hit lines 283-284: Nested parentheses in string literal.
    /// Example: (outer (inner) end) — depth tracking must handle nesting correctly.
    /// </summary>
    [Fact]
    public void Parse_NestedParenthesesInStringLiteral_HandledCorrectly()
    {
        // Arrange - string literal with nested parens
        var cmap = @"
            (level 0 (level 1 (level 2) back to 1) back to 0)
            1 beginbfchar
            <0042> <0042>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0042].Should().Be("B");
    }

    /// <summary>
    /// Hit line 282: Escaped character sequence in string literal.
    /// Example: (escaped\(paren\) and backslash\\)
    /// Backslash followed by any character should be treated as an escape sequence.
    /// </summary>
    [Fact]
    public void Parse_EscapedCharactersInStringLiteral_ParsedCorrectly()
    {
        // Arrange - string literal with escape sequences for special characters
        var cmap = @"
            (text with escaped \( paren and \) paren and \\ backslash)
            1 beginbfchar
            <0043> <0043>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0043].Should().Be("C");
    }

    /// <summary>
    /// Hit line 282 edge case: Escaped character at end of string (when i+1 still valid).
    /// Example: (text with trailing escape\\)
    /// </summary>
    [Fact]
    public void Parse_TrailingEscapeSequenceInStringLiteral_Handled()
    {
        // Arrange - string ending with backslash-escaped character
        var cmap = @"
            (text with trailing escape\n)
            1 beginbfchar
            <0044> <0044>
            endbfchar
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(1);
        result[0x0044].Should().Be("D");
    }

    /// <summary>
    /// Hit line 288: String literal at EOF (no closing >).
    /// Verifies that reaching EOF while in string literal doesn't crash.
    /// </summary>
    [Fact]
    public void Parse_StringLiteralAtEOF_NoException()
    {
        // Arrange - string literal that reaches EOF without proper close
        var cmap = @"
            1 beginbfchar
            <0045> <0045>
            endbfchar
            (string at EOF without close";

        // Act & Assert - should not throw
        var result = ToUnicodeCMapParser.Parse(cmap);
        result.Should().HaveCount(1);
        result[0x0045].Should().Be("E");
    }

    /// <summary>
    /// bfrange array form with empty string destination (edge case).
    /// Example: <0001><0003>[<> <0042> <>]
    /// Maps to "", "B", ""
    /// </summary>
    [Fact]
    public void ParseBfrange_ArrayFormWithEmptyStringDestination_HandledGracefully()
    {
        // Arrange - array destination with empty hex strings
        var cmap = @"
            1 beginbfrange
            <0001> <0003> [<> <0042> <>]
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert - empty strings and normal mappings are valid
        result.Should().HaveCount(3);
        result[0x0001].Should().HaveLength(0); // empty string
        result[0x0002].Should().Be("B");
        result[0x0003].Should().HaveLength(0); // empty string
    }

    /// <summary>
    /// Hit surrogate-pair edge case in bfrange incrementing destination.
    /// When incrementing a surrogate pair, must carefully handle the pair increment.
    /// Example: <0001><0003><D83D+offset> should increment within surrogate pairs.
    /// </summary>
    [Fact]
    public void ParseBfrange_SurrogatePairIncrementingDestination_IncrementCorrectly()
    {
        // Arrange - bfrange with surrogate pair that increments
        // D83DDE00 = U+1F600 (😀), D83DDE01 = U+1F601 (😁), etc.
        var cmap = @"
            1 beginbfrange
            <0001> <0003> <D83DDE00>
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(3);
        result[0x0001].Should().Be("😀"); // U+1F600
        result[0x0002].Should().Be("😁"); // U+1F601
        result[0x0003].Should().Be("😂"); // U+1F602
    }

    /// <summary>
    /// Complex bfrange with mixed array-form and regular entries.
    /// Verifies that the tokenizer correctly distinguishes [ vs non-[ after <srcLo><srcHi>.
    /// </summary>
    [Fact]
    public void ParseBfrange_MixedArrayAndNonArrayForms_DistinguishedCorrectly()
    {
        // Arrange - one range with array form, one with simple form
        var cmap = @"
            2 beginbfrange
            <0001> <0002> [<0041> <0042>]
            <0010> <0012> <0050>
            endbfrange
        ";

        // Act
        var result = ToUnicodeCMapParser.Parse(cmap);

        // Assert
        result.Should().HaveCount(5);
        // Array form: 0x0001→A, 0x0002→B
        result[0x0001].Should().Be("A");
        result[0x0002].Should().Be("B");
        // Simple form: 0x0010→P, 0x0011→Q, 0x0012→R
        result[0x0010].Should().Be("P");
        result[0x0011].Should().Be("Q");
        result[0x0012].Should().Be("R");
    }
}
