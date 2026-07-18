using AwesomeAssertions;
using Excise.Rendering;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Unit tests for <c>RenderContext.UnescapePdfStringBytes</c> — the
/// PDF literal-string escape decoder. Specific spec edge case driving
/// these tests is line continuation (<c>\&lt;EOL&gt;</c>), which a
/// prior version of the decoder mis-handled and produced visible
/// placeholder squares in rendered Word-derived forms.
/// </summary>
public class PdfStringEscapeTests
{
    [Theory]
    [InlineData(@"hello", "hello")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\(b", "a(b")]
    [InlineData(@"a\)b", "a)b")]
    [InlineData(@"a\\b", "a\\b")]
    public void Decodes_StandardEscapes(string input, string expected)
    {
        var actual = System.Text.Encoding.Latin1.GetString(RenderContext.UnescapePdfStringBytes(input));
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"a\053b", "a+b")]    // octal 053 = '+'
    [InlineData(@"\101\102\103", "ABC")]  // octal 101=A, 102=B, 103=C
    public void Decodes_OctalEscapes(string input, string expected)
    {
        var actual = System.Text.Encoding.Latin1.GetString(RenderContext.UnescapePdfStringBytes(input));
        actual.Should().Be(expected);
    }

    [Fact]
    public void BackslashBeforeUnknownChar_DropsBackslash_KeepsChar()
    {
        // Spec 7.3.4.2: "If the character following the REVERSE SOLIDUS is
        // not one of those shown in Table 3, the REVERSE SOLIDUS shall be
        // ignored." So \X with X not in the table is just X.
        var actual = System.Text.Encoding.Latin1.GetString(
            RenderContext.UnescapePdfStringBytes(@"a\Xb"));
        actual.Should().Be("aXb");
    }

    [Fact]
    public void BackslashBeforeNewline_DropsBoth_LineContinuation()
    {
        // Spec 7.3.4.2: "If a REVERSE SOLIDUS appears at the end of a line,
        // then the REVERSE SOLIDUS and the end-of-line marker following it
        // shall be treated as parts of the string but ignored."
        // Test all three EOL forms: LF, CR, CRLF.
        RenderContext.UnescapePdfStringBytes("ab\\\nxy")
            .Should().Equal(System.Text.Encoding.Latin1.GetBytes("abxy"));
        RenderContext.UnescapePdfStringBytes("ab\\\rxy")
            .Should().Equal(System.Text.Encoding.Latin1.GetBytes("abxy"));
        RenderContext.UnescapePdfStringBytes("ab\\\r\nxy")
            .Should().Equal(System.Text.Encoding.Latin1.GetBytes("abxy"));
    }

    [Fact]
    public void RealWorldBirthCertSequence_DropsContinuationGracefully()
    {
        // The exact pattern that produced visible ⊠ placeholders in the
        // birth-certificate-request form: a long underscore run with a
        // \<CR> continuation in the middle, broken across two source
        // lines in the PDF.
        var s = "____________________________________________________________________\\\r__________";
        var bytes = RenderContext.UnescapePdfStringBytes(s);
        var decoded = System.Text.Encoding.Latin1.GetString(bytes);
        decoded.Should().Be(new string('_', 78), // 68 + 10 underscores
            "the backslash + EOL pair must be entirely dropped, leaving an unbroken underscore run");
    }
}
