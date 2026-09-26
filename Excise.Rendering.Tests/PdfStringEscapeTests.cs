using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// The PDF literal-string escapes (§7.3.4.2) as the real parsers decode them:
/// <see cref="PdfLexer"/> (file structure) and <see cref="ContentStreamParser"/>
/// (the path the renderer and the redaction engine read text through). The
/// renderer keeps no unescaper of its own. Specific spec edge case driving
/// these tests is line continuation (<c>\&lt;EOL&gt;</c>), which a prior
/// decoder mis-handled and produced visible placeholder squares in rendered
/// Word-derived forms.
/// </summary>
public class PdfStringEscapeTests
{
    // Latin1 maps char to byte one to one, so `body` is the exact literal-string
    // source and `expected` the exact decoded bytes.
    private static void AssertUnescapes(string body, string expected)
    {
        var literal = Encoding.Latin1.GetBytes("(" + body + ")");
        var expectedBytes = Encoding.Latin1.GetBytes(expected);

        using var lexer = new PdfLexer(literal);
        var token = lexer.NextToken();
        token.Type.Should().Be(PdfTokenType.LiteralString);
        Encoding.Latin1.GetBytes(token.Value).Should().Equal(expectedBytes, "PdfLexer");

        var content = new ContentStreamParser(Encoding.Latin1.GetBytes("(" + body + ") Tj")).Parse();
        ((PdfString)content.Operators.Single().Operands[0]).Bytes
            .Should().Equal(expectedBytes, "ContentStreamParser");
    }

    [Theory]
    [InlineData(@"hello", "hello")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\bb", "a\bb")]
    [InlineData(@"a\fb", "a\fb")]
    [InlineData(@"a\(b", "a(b")]
    [InlineData(@"a\)b", "a)b")]
    [InlineData(@"a\\b", "a\\b")]
    public void Decodes_StandardEscapes(string input, string expected)
        => AssertUnescapes(input, expected);

    [Theory]
    [InlineData(@"a\053b", "a+b")]    // octal 053 = '+'
    [InlineData(@"\101\102\103", "ABC")]  // octal 101=A, 102=B, 103=C
    public void Decodes_OctalEscapes(string input, string expected)
        => AssertUnescapes(input, expected);

    [Fact]
    public void BackslashBeforeUnknownChar_DropsBackslash_KeepsChar()
    {
        // Spec 7.3.4.2: "If the character following the REVERSE SOLIDUS is
        // not one of those shown in Table 3, the REVERSE SOLIDUS shall be
        // ignored." So \X with X not in the table is just X.
        AssertUnescapes(@"a\Xb", "aXb");
    }

    [Fact]
    public void BackslashBeforeNewline_DropsBoth_LineContinuation()
    {
        // Spec 7.3.4.2: "If a REVERSE SOLIDUS appears at the end of a line,
        // then the REVERSE SOLIDUS and the end-of-line marker following it
        // shall be treated as parts of the string but ignored."
        // Test all three EOL forms: LF, CR, CRLF.
        AssertUnescapes("ab\\\nxy", "abxy");
        AssertUnescapes("ab\\\rxy", "abxy");
        AssertUnescapes("ab\\\r\nxy", "abxy");
    }

    [Fact]
    public void RealWorldBirthCertSequence_DropsContinuationGracefully()
    {
        // The exact pattern that produced visible ⊠ placeholders in the
        // birth-certificate-request form: a long underscore run with a
        // \<CR> continuation in the middle, broken across two source
        // lines in the PDF.
        var s = "____________________________________________________________________\\\r__________";
        AssertUnescapes(s, new string('_', 78)); // 68 + 10 underscores
    }
}
