using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Byte-exact pins for the content-stream token parsers in
/// <see cref="TextExtractor"/> (#600). The #600 hot-path work rewrote
/// ParseStringLiteral / ParseHexString / ParseName / ParseNumber /
/// ParseKeyword for allocation, with the contract that extraction output is
/// BYTE-IDENTICAL — same decoded strings, same Letter values / character
/// codes / code byte lengths, same positions. These tests pin that contract
/// on a fixture that deliberately exercises every rewritten path: literal
/// escapes (named, octal 1–3 digit, escaped parens, line continuation),
/// nested parentheses, hex strings with whitespace and an odd digit count,
/// #XX name escapes, integer and real operands, and multi-operator TJ/'
/// content. Expected values are hand-derived from PDF32000-1 §7.3.4 and
/// were verified identical on the pre-#600 parser.
/// </summary>
public class TextExtractorParsePinTests
{
    // Exercises, in order:
    //  - Tj literal with a 3-digit octal escape (\154 = 'l') and escaped parens
    //  - Tj literal with leading space, octal escapes, and NESTED parens
    //  - Td relative positioning with real operands
    //  - Tj literal with a backslash line-continuation (produces NO character),
    //    octal 'A' (\101) and octal 0xFF (\377)
    //  - Tj hex string with embedded whitespace and an ODD digit count
    //    (trailing nibble padded with 0 -> 0x50 'P')
    //  - TJ array mixing literals with negative-int and real kern adjustments
    //  - TL + ' (move-to-next-line-and-show)
    //  - A #XX hex escape inside the font NAME operand (/F#31 == /F1)
    private const string PinContent =
        "BT\n" +
        "/F1 12 Tf\n" +
        "1 0 0 1 72 700 Tm\n" +
        "(Hel\\154o \\(World\\)) Tj\n" +
        "( Hi\\054 nested (parens) ok) Tj\n" +
        "-2.5 -14 Td\n" +
        "(Wrap\\\nped line\\101\\377) Tj\n" +
        "<48 65 6C6C 6F5> Tj\n" +
        "[ (Kern) -120 (ed) 250.5 (!) ] TJ\n" +
        "14 TL\n" +
        "/F#31 10 Tf\n" +
        "(Next) '\n" +
        "ET";

    private const string ExpectedText =
        "Hello (World)" +
        " Hi, nested (parens) ok" +
        "Wrapped lineAÿ" +
        "HelloP" +
        "Kerned!" +
        "Next";

    [Fact]
    public void ExtractText_TokenParserFixture_IsByteIdentical()
    {
        using var doc = PdfDocument.Open(CreatePdfWithContentStream(PinContent));
        var extractor = new TextExtractor(doc.GetPage(1)) { IncludeFormFieldValues = false };

        extractor.ExtractText().Should().Be(ExpectedText);
    }

    [Fact]
    public void ExtractLetters_TokenParserFixture_PinsValuesCodesAndPositions()
    {
        using var doc = PdfDocument.Open(CreatePdfWithContentStream(PinContent));
        var extractor = new TextExtractor(doc.GetPage(1)) { IncludeFormFieldValues = false };

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(ExpectedText.Length);
        string.Concat(letters.Select(l => l.Value)).Should().Be(ExpectedText);

        // First letter: 'H' at the Tm origin, Helvetica 'H' width 722/1000 * 12.
        var first = letters[0];
        first.Value.Should().Be("H");
        first.CharacterCode.Should().Be('H');
        first.CodeByteLength.Should().Be(1);
        first.StartX.Should().BeApproximately(72.0, 1e-9);
        first.StartY.Should().BeApproximately(700.0, 1e-9);
        first.Width.Should().BeApproximately(8.664, 1e-9);
        first.FontSize.Should().Be(12);
        first.FontName.Should().Be("F1");
        first.IsInHiddenOptionalContent.Should().BeFalse();

        // The octal escape \154 decoded to a real 'l' with the byte value 108.
        letters[3].Value.Should().Be("l");
        letters[3].CharacterCode.Should().Be(108);

        // \377 decoded through WinAnsi to U+00FF with the source byte preserved.
        var yuml = letters.Single(l => l.Value == "ÿ");
        yuml.CharacterCode.Should().Be(255);
        yuml.CodeByteLength.Should().Be(1);

        // First letter after "-2.5 -14 Td": 'W' at (72 - 2.5, 700 - 14) — the
        // line-continuation escape before it must produce NO character.
        var w = letters.Single(l => l.Value == "W" && l.StartY < 700);
        w.StartX.Should().BeApproximately(69.5, 1e-9);
        w.StartY.Should().BeApproximately(686.0, 1e-9);

        // Hex-string letters: <48...> starts with 'H' (0x48); the odd trailing
        // nibble '5' was padded to 0x50 = 'P'.
        letters.Single(l => l.Value == "P").CharacterCode.Should().Be(0x50);

        // The #XX name escape resolved /F#31 to font F1 for the final block.
        var next = letters.Single(l => l.Value == "N");
        next.FontName.Should().Be("F1");
        next.FontSize.Should().Be(10);

        // ' moved down by the 14 TL leading from the current line origin.
        next.StartY.Should().BeApproximately(672.0, 1e-9);
    }

    [Fact]
    public void ExtractLetters_TokenParserFixture_TjKernAdjustsPosition()
    {
        using var doc = PdfDocument.Open(CreatePdfWithContentStream(PinContent));
        var extractor = new TextExtractor(doc.GetPage(1)) { IncludeFormFieldValues = false };

        var letters = extractor.ExtractLetters();

        // TJ: [ (Kern) -120 (ed) 250.5 (!) ] — an adjustment a is applied as
        // tx = -(a / 1000) * fontSize (§9.4.3): the int -120 widens the gap
        // before "ed" by 1.44; the real 250.5 pulls '!' back by 3.006.
        // "Kerned!" occupies a fixed index range of the pinned text.
        var kIndex = ExpectedText.IndexOf("Kerned!", StringComparison.Ordinal);
        string.Concat(letters.Skip(kIndex).Take(7).Select(l => l.Value)).Should().Be("Kerned!");

        var n = letters[kIndex + 3];  // 'n' of "Kern"
        var e2 = letters[kIndex + 4]; // 'e' of "ed"
        (e2.StartX - (n.StartX + n.Width)).Should().BeApproximately(1.44, 1e-9);

        var d = letters[kIndex + 5];  // 'd' of "ed"
        var bang = letters[kIndex + 6];
        (bang.StartX - (d.StartX + d.Width)).Should().BeApproximately(-3.006, 1e-9);
    }

    [Fact]
    public void ExtractWords_TokenParserFixture_SegmentsUnchanged()
    {
        using var doc = PdfDocument.Open(CreatePdfWithContentStream(PinContent));
        var extractor = new TextExtractor(doc.GetPage(1)) { IncludeFormFieldValues = false };

        var words = extractor.ExtractWords().Select(w => w.Text).ToList();

        words.Should().Contain(new[] { "Hello", "(World)", "Hi,", "nested", "(parens)", "Next" });
    }

    private static byte[] CreatePdfWithContentStream(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 6 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
