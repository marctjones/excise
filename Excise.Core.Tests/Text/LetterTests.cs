using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #1485 moved <see cref="Letter.MarkedContentId"/> off an auto-property's
/// <c>Nullable&lt;int&gt;</c> onto an int plus a presence flag. These pin that
/// the property still behaves exactly like <c>int?</c> for every value.
/// </summary>
public class LetterTests
{
    private static Letter CreateLetter() =>
        new("A", new PdfRectangle(10, 20, 20, 32), 12.0, "F1", 10, 20, 10, 65);

    [Fact]
    public void MarkedContentId_DefaultsToNull()
    {
        CreateLetter().MarkedContentId.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void MarkedContentId_RoundTripsEveryIntValue(int mcid)
    {
        var letter = CreateLetter();

        letter.MarkedContentId = mcid;

        letter.MarkedContentId.Should().Be(mcid);
        letter.MarkedContentId.HasValue.Should().BeTrue();
    }

    [Fact]
    public void MarkedContentId_SetNullAfterValue_ReadsNull()
    {
        var letter = CreateLetter();
        letter.MarkedContentId = 0;

        letter.MarkedContentId = null;

        letter.MarkedContentId.Should().BeNull();
    }

    [Fact]
    public void MarkedContentId_DoesNotDisturbNeighbouringFields()
    {
        var letter = CreateLetter();
        letter.IsCidFont = true;
        letter.IsInHiddenOptionalContent = true;
        letter.OperandByteOffset = 3;
        letter.TjElementIndex = 2;

        letter.MarkedContentId = -5;

        letter.IsCidFont.Should().BeTrue();
        letter.IsInHiddenOptionalContent.Should().BeTrue();
        letter.OperandByteOffset.Should().Be(3);
        letter.TjElementIndex.Should().Be(2);
        letter.CharacterCode.Should().Be(65);
        letter.CodeByteLength.Should().Be(1);
    }

    // #1485: the letter model is retained for the document's lifetime, so the
    // strings every letter points at are shared rather than copied per glyph.
    // Reference identity is the property being pinned; the values are
    // asserted too so a sharing change can never alter what is read.

    [Fact]
    public void FontName_RepeatedTfOperators_ShareOneString()
    {
        var pdf = Content.ContentStreamFixture.Build(
            "BT /F1 12 Tf 100 700 Td (A) Tj /F1 12 Tf (B) Tj /F1 12 Tf (C) Tj ET");
        using var doc = PdfDocument.Open(pdf);

        var letters = Content.ContentStreamFixture.ExtractLetters(doc.GetPage(1));

        letters.Select(l => l.Value).Should().Equal("A", "B", "C");
        letters.Should().AllSatisfy(l => l.FontName.Should().Be("F1"));
        ReferenceEquals(letters[0].FontName, letters[1].FontName).Should().BeTrue();
        ReferenceEquals(letters[1].FontName, letters[2].FontName).Should().BeTrue();
    }

    [Fact]
    public void Value_FromToUnicodeMap_IsSharedAcrossExtractions()
    {
        const string cmap =
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n"
          + "1 beginbfchar\n<41> <2019>\nendbfchar\n";
        var pdf = Content.ContentStreamFixture.Build(
            "BT /F1 12 Tf 100 700 Td (AA) Tj ET",
            fontObject: "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>\nendobj\n",
            extraObjects: $"6 0 obj\n<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream\nendobj\n");
        using var doc = PdfDocument.Open(pdf);

        // Each extraction walks the page afresh and re-parses the /ToUnicode
        // map, which is what used to give every page its own copy of a value.
        var first = Content.ContentStreamFixture.ExtractLetters(doc.GetPage(1));
        var second = Content.ContentStreamFixture.ExtractLetters(doc.GetPage(1));

        first.Select(l => l.Value).Should().Equal("’", "’");
        second.Select(l => l.Value).Should().Equal("’", "’");
        ReferenceEquals(first[0].Value, second[0].Value).Should().BeTrue();
    }

    [Fact]
    public void ShareSingleChar_PreservesValueForEveryLength()
    {
        GlyphUnicodeDecoder.ShareSingleChar("").Should().BeEmpty();
        GlyphUnicodeDecoder.ShareSingleChar("fi").Should().Be("fi");
        GlyphUnicodeDecoder.ShareSingleChar("😀").Should().Be("😀");
        GlyphUnicodeDecoder.ShareSingleChar(new string('\uD800', 1)).Should().Be("\uD800");

        var a = new string('•', 1);
        var b = new string('•', 1);
        ReferenceEquals(a, b).Should().BeFalse();
        var sharedA = GlyphUnicodeDecoder.ShareSingleChar(a);
        sharedA.Should().Be("•");
        ReferenceEquals(sharedA, GlyphUnicodeDecoder.ShareSingleChar(b)).Should().BeTrue();
    }
}
