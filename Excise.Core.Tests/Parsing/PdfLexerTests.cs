using AwesomeAssertions;
using Excise.Core.Parsing;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Parsing;

public class PdfLexerTests
{
    [Theory]
    [InlineData("123", PdfTokenType.Integer, "123")]
    [InlineData("-45", PdfTokenType.Integer, "-45")]
    [InlineData("+67", PdfTokenType.Integer, "+67")]
    [InlineData("0", PdfTokenType.Integer, "0")]
    public void NextToken_Integer_ReturnsCorrectToken(string input, PdfTokenType expectedType, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(expectedType);
        token.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("3.14", PdfTokenType.Real, "3.14")]
    [InlineData("-0.5", PdfTokenType.Real, "-0.5")]
    [InlineData(".25", PdfTokenType.Real, ".25")]
    [InlineData("123.456", PdfTokenType.Real, "123.456")]
    public void NextToken_Real_ReturnsCorrectToken(string input, PdfTokenType expectedType, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(expectedType);
        token.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    [InlineData("obj", "obj")]
    [InlineData("endobj", "endobj")]
    [InlineData("stream", "stream")]
    [InlineData("endstream", "endstream")]
    [InlineData("R", "R")]
    public void NextToken_Keyword_ReturnsCorrectToken(string input, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Keyword);
        token.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("/Name", "Name")]
    [InlineData("/Type", "Type")]
    [InlineData("/A;Name_With-Various.Chars", "A;Name_With-Various.Chars")]
    [InlineData("/Name#20With#20Spaces", "Name With Spaces")]
    public void NextToken_Name_ReturnsCorrectToken(string input, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Name);
        token.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("(Hello)", "Hello")]
    [InlineData("(Hello World)", "Hello World")]
    [InlineData("(Nested (parens) work)", "Nested (parens) work")]
    [InlineData(@"(Line\nBreak)", "Line\nBreak")]
    [InlineData(@"(Tab\tChar)", "Tab\tChar")]
    [InlineData(@"(Backslash\\Char)", "Backslash\\Char")]
    [InlineData(@"(Paren\(Escape)", "Paren(Escape")]
    public void NextToken_LiteralString_ReturnsCorrectToken(string input, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("<48656C6C6F>", "48656C6C6F")] // "Hello"
    [InlineData("<48 65 6C 6C 6F>", "48656C6C6F")] // With whitespace
    [InlineData("<4865>", "4865")]
    public void NextToken_HexString_ReturnsCorrectToken(string input, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be(expectedValue);
    }

    [Fact]
    public void NextToken_ArrayDelimiters_ReturnsCorrectTokens()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("[ 1 2 3 ]"));

        lexer.NextToken().Type.Should().Be(PdfTokenType.ArrayStart);
        lexer.NextToken().Type.Should().Be(PdfTokenType.Integer);
        lexer.NextToken().Type.Should().Be(PdfTokenType.Integer);
        lexer.NextToken().Type.Should().Be(PdfTokenType.Integer);
        lexer.NextToken().Type.Should().Be(PdfTokenType.ArrayEnd);
    }

    [Fact]
    public void NextToken_DictionaryDelimiters_ReturnsCorrectTokens()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<< /Key /Value >>"));

        lexer.NextToken().Type.Should().Be(PdfTokenType.DictionaryStart);
        lexer.NextToken().Type.Should().Be(PdfTokenType.Name);
        lexer.NextToken().Type.Should().Be(PdfTokenType.Name);
        lexer.NextToken().Type.Should().Be(PdfTokenType.DictionaryEnd);
    }

    [Fact]
    public void NextToken_SkipsWhitespace()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("  \t\n\r  123  "));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("123");
    }

    [Fact]
    public void NextToken_SkipsComments()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("% This is a comment\n123"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("123");
    }

    [Fact]
    public void NextToken_IndirectReference_TokenSequence()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("5 0 R"));

        var t1 = lexer.NextToken();
        var t2 = lexer.NextToken();
        var t3 = lexer.NextToken();

        t1.Type.Should().Be(PdfTokenType.Integer);
        t1.Value.Should().Be("5");
        t2.Type.Should().Be(PdfTokenType.Integer);
        t2.Value.Should().Be("0");
        t3.Type.Should().Be(PdfTokenType.Keyword);
        t3.Value.Should().Be("R");
    }

    [Fact]
    public void NextToken_AtEndOfFile_ReturnsEof()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123"));

        lexer.NextToken(); // consume the integer
        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Eof);
    }

    [Fact]
    public void Position_TracksCurrentPosition()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123 456"));

        lexer.Position.Should().Be(0);
        lexer.NextToken();
        // After "123" and whitespace
        lexer.NextToken();
        lexer.Position.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Seek_MovesToPosition()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123 456 789"));

        lexer.NextToken(); // 123
        lexer.NextToken(); // 456

        lexer.Seek(0);
        var token = lexer.NextToken();

        token.Value.Should().Be("123");
    }

    [Fact]
    public void PeekToken_ReturnsSameTokenWithoutConsuming()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123 456"));

        var peeked1 = lexer.PeekToken();
        var peeked2 = lexer.PeekToken();

        peeked1.Type.Should().Be(PdfTokenType.Integer);
        peeked1.Value.Should().Be("123");
        peeked1.Should().Be(peeked2);

        var actual = lexer.NextToken();
        actual.Value.Should().Be("123");
    }

    [Fact]
    public void PeekToken_MultipleReads()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123 456 789"));

        var peeked = lexer.PeekToken();
        peeked.Value.Should().Be("123");

        lexer.NextToken(); // consume 123
        var next = lexer.NextToken();
        next.Value.Should().Be("456");

        var peeked2 = lexer.PeekToken();
        peeked2.Value.Should().Be("789");
    }

    [Fact]
    public void ReadComment_SkipsToNextToken()
    {
        // Comments are skipped by SkipWhitespaceAndComments; NextToken returns the post-comment token
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("% This is a comment\n123"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("123");
    }

    [Fact]
    public void ReadComment_CommentAtEof_ReturnsEof()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("% Comment at EOF"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Eof);
    }

    [Fact]
    public void ReadComment_EmptyComment_SkipsToNextToken()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("%\n123"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("123");
    }

    [Fact]
    public void ReadComment_CommentFollowedByObject()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("% Comment\n<< /Key /Value >>"));

        var dictStart = lexer.NextToken();

        dictStart.Type.Should().Be(PdfTokenType.DictionaryStart);
    }

    [Theory]
    [InlineData("1e5", PdfTokenType.Real, "1e5")] // Scientific notation
    [InlineData("1E-3", PdfTokenType.Real, "1E-3")] // Uppercase E with sign
    [InlineData("-2.5e+10", PdfTokenType.Real, "-2.5e+10")] // Full notation
    [InlineData(".5e2", PdfTokenType.Real, ".5e2")] // Starting with decimal
    public void ReadNumberOrKeyword_ScientificNotation(string input, PdfTokenType expectedType, string expectedValue)
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var token = lexer.NextToken();

        token.Type.Should().Be(expectedType);
        token.Value.Should().Be(expectedValue);
    }

    [Fact]
    public void ReadNumberOrKeyword_SingleMinus_ReadAsKeyword()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("- 123"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Keyword);
        token.Value.Should().Be("-");
    }

    [Fact]
    public void ReadNumberOrKeyword_SinglePlus_ReadAsKeyword()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("+ 456"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Keyword);
        token.Value.Should().Be("+");
    }

    [Fact]
    public void ReadNumberOrKeyword_SingleDecimal_ReadAsKeyword()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(". 789"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Keyword);
        token.Value.Should().Be(".");
    }

    [Fact]
    public void ReadLiteralString_OctalEscape_SingleDigit()
    {
        // \1 = octal 1 = decimal 1
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\1End)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("");
    }

    [Fact]
    public void ReadLiteralString_OctalEscape_TwoDigits()
    {
        // \12 = octal 12 = decimal 10
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\12End)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("\n");
    }

    [Fact]
    public void ReadLiteralString_OctalEscape_ThreeDigits()
    {
        // \101 = octal 101 = decimal 65 = 'A'
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\101End)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("A");
    }

    [Fact]
    public void ReadLiteralString_OctalEscape_StopsAtNonOctal()
    {
        // \12X should read \12 as octal, then 'X' literally
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\12XEnd)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("X");
    }

    [Fact]
    public void ReadLiteralString_LineContinuation_WithCarriageReturn()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Line1\\\r\nLine2)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Be("Line1Line2");
    }

    [Fact]
    public void ReadLiteralString_LineContinuation_WithCarriageReturnOnly()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Line1\\\rLine2)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Be("Line1Line2");
    }

    [Fact]
    public void ReadLiteralString_EscapeBackspaceCharacter()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\bEnd)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("\b");
    }

    [Fact]
    public void ReadLiteralString_EscapeFormFeedCharacter()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\fEnd)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("\f");
    }

    [Fact]
    public void ReadLiteralString_UnknownEscapeIncludesCharacter()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(@"(Test\xEnd)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("x");
    }

    [Fact]
    public void ReadHexString_OddNumberOfDigits()
    {
        // <ABC> = <AB C?> should pad or handle gracefully
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<ABC>"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be("ABC");
    }

    [Fact]
    public void ReadHexString_WithWhitespaceAndLineBreaks()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<48 65\n6C 6C\r\n6F>"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be("48656C6C6F");
    }

    [Fact]
    public void UnreadByte_CanRestorePreviousByte()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123"));

        lexer.NextToken(); // consume "123"

        lexer.Seek(0);
        lexer.NextToken();

        var token = lexer.NextToken();
        token.Type.Should().Be(PdfTokenType.Eof);
    }

    [Fact]
    public void NextToken_InvalidHexDigitInHexString_ThrowsException()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<48GG>"));

        var ex = Assert.Throws<PdfParseException>(() => lexer.NextToken());
        ex.Message.Should().Contain("Invalid hex digit");
    }

    [Fact]
    public void NextToken_SingleGreaterThan_ThrowsException()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(">"));

        var ex = Assert.Throws<PdfParseException>(() => lexer.NextToken());
        ex.Message.Should().Contain("Unexpected '>'");
    }

    [Fact]
    public void NextToken_InvalidCharacter_ThrowsException()
    {
        using var lexer = new PdfLexer(new byte[] { 0xFF });

        var ex = Assert.Throws<PdfParseException>(() => lexer.NextToken());
        ex.Message.Should().Contain("Unexpected character");
    }

    [Fact]
    public void ReadStreamData_ReadsExactNumberOfBytes()
    {
        byte[] data = Encoding.ASCII.GetBytes("stream\n1234567890endstream");
        using var lexer = new PdfLexer(data);

        lexer.NextToken(); // consume "stream"

        var streamData = lexer.ReadStreamData(10);

        streamData.Should().HaveCount(10);
        Encoding.ASCII.GetString(streamData).Should().Be("1234567890");
    }

    [Fact]
    public void PdfToken_Position_Property()
    {
        var token = new PdfToken(PdfTokenType.Integer, "123", 42);

        token.Position.Should().Be(42);
    }

    [Fact]
    public void PdfToken_ToString_ReturnsFormattedString()
    {
        var token = new PdfToken(PdfTokenType.Integer, "123", 10);

        var str = token.ToString();

        str.Should().Contain("Integer");
        str.Should().Contain("123");
        str.Should().Contain("10");
    }

    [Fact]
    public void PdfToken_IsEof_Property()
    {
        var eofToken = new PdfToken(PdfTokenType.Eof, "", 100);
        var intToken = new PdfToken(PdfTokenType.Integer, "123", 0);

        eofToken.IsEof.Should().BeTrue();
        intToken.IsEof.Should().BeFalse();
    }

    [Fact]
    public void PdfToken_IsNumber_Property()
    {
        var intToken = new PdfToken(PdfTokenType.Integer, "123", 0);
        var realToken = new PdfToken(PdfTokenType.Real, "3.14", 0);
        var nameToken = new PdfToken(PdfTokenType.Name, "Type", 0);

        intToken.IsNumber.Should().BeTrue();
        realToken.IsNumber.Should().BeTrue();
        nameToken.IsNumber.Should().BeFalse();
    }

    #region Additional Lexer Coverage Tests

    [Fact]
    public void ReadLiteralString_NestedParentheses_Balanced()
    {
        // Nested parentheses must be escaped or balanced
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Text with (nested) parens)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("nested");
    }

    [Fact]
    public void ReadLiteralString_DeepNesting_MultiLevel()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Outer (Middle (Inner) Middle) Outer)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().NotBeEmpty();
    }

    [Fact]
    public void ReadHexString_EmptyHexString()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<>"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be("");
    }

    [Fact]
    public void ReadHexString_SingleHexDigit()
    {
        // <A> should be treated as <A0> (padded)
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<A>"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be("A");
    }

    [Fact]
    public void ReadName_WithHashEscapes_DecodesCorrectly()
    {
        // /Name#20Word should become "Name Word"
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("/Font#20Name"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Name);
        token.Value.Should().Be("Font Name");
    }

    [Fact]
    public void ReadName_WithMultipleEscapes()
    {
        // /A#41B#42C#43 = /AABBCC
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("/A#41B#42C#43"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Name);
        token.Value.Should().Contain("A");
    }

    [Fact]
    public void ReadName_SpecialCharactersAllowed()
    {
        // Names can contain various special characters
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("/Name-With_Many.Chars"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Name);
        token.Value.Should().Contain("Name");
    }

    [Fact]
    public void ReadArray_EmptyArray()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("[]"));

        var start = lexer.NextToken();
        var end = lexer.NextToken();

        start.Type.Should().Be(PdfTokenType.ArrayStart);
        end.Type.Should().Be(PdfTokenType.ArrayEnd);
    }

    [Fact]
    public void ReadArray_NestedArrays()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("[1 [2 3] 4]"));

        lexer.NextToken(); // [
        lexer.NextToken(); // 1
        var nested = lexer.NextToken();
        nested.Type.Should().Be(PdfTokenType.ArrayStart);
    }

    [Fact]
    public void ReadDictionary_EmptyDictionary()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<<>>"));

        var start = lexer.NextToken();
        var end = lexer.NextToken();

        start.Type.Should().Be(PdfTokenType.DictionaryStart);
        end.Type.Should().Be(PdfTokenType.DictionaryEnd);
    }

    [Fact]
    public void ReadDictionary_WithMixedValueTypes()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<< /Key1 123 /Key2 (value) /Key3 /Name >>"));

        var dict = lexer.NextToken();
        dict.Type.Should().Be(PdfTokenType.DictionaryStart);

        // Consume key and int value
        lexer.NextToken(); // /Key1
        lexer.NextToken(); // 123

        // Next should be /Key2
        var key2 = lexer.NextToken();
        key2.Value.Should().Be("Key2");
    }

    [Fact]
    public void LiteralString_BackslashLineContinuation()
    {
        // Backslash at end of line continues to next line
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Line\\\nContinued)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Be("LineContinued");
    }

    [Fact]
    public void LiteralString_EscapeCarriageReturn()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Text\\rReturn)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("\r");
    }

    [Fact]
    public void LiteralString_EscapeLineFeed()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("(Text\\nLineFeed)"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.LiteralString);
        token.Value.Should().Contain("\n");
    }

    [Fact]
    public void Lexer_SequenceOfTokens_AllTokensExtracted()
    {
        // Test a realistic sequence: dict with array and values
        var input = "<< /Type /Catalog /Pages 2 0 R >>";
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        var tokens = new List<PdfToken>();
        var token = lexer.NextToken();
        while (!token.IsEof)
        {
            tokens.Add(token);
            token = lexer.NextToken();
        }

        tokens.Should().HaveCountGreaterThan(0);
        tokens[0].Type.Should().Be(PdfTokenType.DictionaryStart);
    }

    [Fact]
    public void Lexer_ReadStreamData_WithAsciiContent()
    {
        // Test ReadStreamData on simple ASCII content
        byte[] dataBytes = Encoding.ASCII.GetBytes("Hello World");
        byte[] input = Encoding.ASCII.GetBytes("stream\n");

        // Properly concatenate arrays
        var fullInput = new byte[input.Length + dataBytes.Length];
        Array.Copy(input, fullInput, input.Length);
        Array.Copy(dataBytes, 0, fullInput, input.Length, dataBytes.Length);

        using var lexer = new PdfLexer(fullInput);

        var streamToken = lexer.NextToken();
        streamToken.Value.Should().Be("stream");

        // ReadStreamData should extract the specified number of bytes
        var extracted = lexer.ReadStreamData(dataBytes.Length);
        Encoding.ASCII.GetString(extracted).Should().Be("Hello World");
    }

    [Fact]
    public void NumberToken_VeryLargeInteger()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("999999999999999"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("999999999999999");
    }

    [Fact]
    public void NumberToken_VerySmallNegative()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("-999999999999999"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Integer);
        token.Value.Should().Be("-999999999999999");
    }

    [Fact]
    public void NumberToken_ZeroWithLeadingDecimal()
    {
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes(".0"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.Real);
        token.Value.Should().Be(".0");
    }

    [Fact]
    public void HexString_AllValidHexChars()
    {
        // Test with all hex digits 0-9A-Fa-f
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("<0123456789ABCDEFabcdef>"));

        var token = lexer.NextToken();

        token.Type.Should().Be(PdfTokenType.HexString);
        token.Value.Should().Be("0123456789ABCDEFabcdef");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void NextToken_UnexpectedCharacter_ThrowsPdfParseException()
    {
        // Test line 131: "Unexpected character" - thrown when encountering unrecognized byte
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("@"));

        var action = () => lexer.NextToken();

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Unexpected character*");
    }

    [Fact]
    public void NextToken_UnexpectedCharacterInMiddleOfInput_ThrowsPdfParseException()
    {
        // Test unexpected character in middle of valid PDF content
        using var lexer = new PdfLexer(Encoding.ASCII.GetBytes("123 @ 456"));

        lexer.NextToken(); // 123
        var action = () => lexer.NextToken(); // @

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Unexpected character*");
    }

    [Fact]
    public void ReadStreamData_WithCarriageReturnOnly_UnreadsTheByte()
    {
        // Test lines 158-163: ReadStreamData when stream has \r without \n
        // This tests the "Just \r, put it back" recovery code
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        // Create a stream with \r-only as line terminator
        writer.Write("stream\r");  // Stream keyword followed by \r only (not \n)
        writer.Write("test data");
        writer.Flush();

        var bytes = ms.ToArray();
        using var lexer = new PdfLexer(new MemoryStream(bytes));

        // Skip to 'stream' keyword position
        long streamPos = Array.IndexOf(bytes, (byte)'s');
        lexer.Seek(streamPos);

        // Read the 'stream' keyword
        var streamToken = lexer.NextToken();
        streamToken.Value.Should().Be("stream");

        // Call ReadStreamData with length=9 ("test data")
        var streamData = lexer.ReadStreamData(9);

        // Should successfully read the data despite \r-only line ending
        Encoding.ASCII.GetString(streamData).Should().Be("test data");
    }

    [Fact]
    public void ReadStreamData_WithCRLF_SkipsLineEnding()
    {
        // Positive test: verify CRLF is properly skipped
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        writer.Write("stream\r\n");  // Standard CRLF
        writer.Write("content");
        writer.Flush();

        var bytes = ms.ToArray();
        using var lexer = new PdfLexer(new MemoryStream(bytes));

        long streamPos = Array.IndexOf(bytes, (byte)'s');
        lexer.Seek(streamPos);

        var streamToken = lexer.NextToken();
        var streamData = lexer.ReadStreamData(7);

        Encoding.ASCII.GetString(streamData).Should().Be("content");
    }

    [Fact]
    public void ReadStreamData_WithLFOnly_SkipsLineEnding()
    {
        // Positive test: verify LF-only is properly skipped
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        writer.Write("stream\n");   // LF only
        writer.Write("data");
        writer.Flush();

        var bytes = ms.ToArray();
        using var lexer = new PdfLexer(new MemoryStream(bytes));

        long streamPos = Array.IndexOf(bytes, (byte)'s');
        lexer.Seek(streamPos);

        var streamToken = lexer.NextToken();
        var streamData = lexer.ReadStreamData(4);

        Encoding.ASCII.GetString(streamData).Should().Be("data");
    }

    #endregion
}
