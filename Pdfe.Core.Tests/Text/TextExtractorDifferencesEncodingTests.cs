using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Text;

/// <summary>
/// Tests for TextExtractor's <c>/Encoding &lt;&lt; /BaseEncoding ... /Differences [...] &gt;&gt;</c>
/// decode path (#662). Before this fix, a simple font with a custom
/// <c>/Differences</c>-based encoding and no <c>/ToUnicode</c> CMap fell
/// straight through to a raw WinAnsi cast of the font's own (often small,
/// sequential) character codes, producing control-character/punctuation
/// garbage — confirmed on the real-world fixtures
/// <c>test-pdfs/pdfjs/canvas.pdf</c> (TrueType) and
/// <c>test-pdfs/pdfjs/bug1001080.pdf</c> (Type3), both of which went from 0%
/// to 100% mutool-relative coverage once this path was fixed.
/// </summary>
public class TextExtractorDifferencesEncodingTests
{
    [Fact]
    public void ExtractText_DifferencesEncoding_UniConventionGlyphName_DecodesCorrectly()
    {
        // /Differences [1 /uni0041] — code 1 (SOH under a raw WinAnsi cast,
        // i.e. exactly the pre-fix garbage symptom) should decode to 'A'.
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <01> Tj ET",
            baseEncoding: null,
            differences: "[1 /uni0041]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("A");
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_UniConventionMultiChar_DecodesCorrectly()
    {
        // "uni00410042" — two 4-hex-digit groups concatenated — decodes to "AB"
        // for a single character code, per the AGL multi-character convention.
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <01> Tj ET",
            baseEncoding: null,
            differences: "[1 /uni00410042]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("AB");
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_ShortUniConventionName_DecodesCorrectly()
    {
        // "uFB01" (4-6 hex digits after a bare "u") — the AGL's other
        // algorithmic naming convention, here for the "fi" ligature codepoint.
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <01> Tj ET",
            baseEncoding: null,
            differences: "[1 /uFB01]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("ﬁ");
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_StandardPostScriptGlyphNames_DecodeCorrectly()
    {
        // Real-world producer convention (matches canvas.pdf exactly): plain
        // StandardEncoding glyph names, not the uniXXXX algorithmic form.
        // /Differences [1 /C /a /n /v /s /space] assigns codes 1=C 2=a 3=n
        // 4=v 5=s 6=space; the content stream spells "Canvas " as C-a-n-v-a-s-space.
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <01020304020506> Tj ET",
            baseEncoding: null,
            differences: "[1 /C /a /n /v /s /space]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Be("Canvas ");
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_GlyphNameLigature_DecodesToLigatureCodepoint()
    {
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <01> Tj ET",
            baseEncoding: null,
            differences: "[1 /fi]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("ﬁ");
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_CodeNotInDifferences_FallsBackToBaseEncoding()
    {
        // /Differences only overrides code 1. Code 0x99 (WinAnsi trademark
        // sign) is untouched and must fall back to /BaseEncoding, not the
        // bare "assume WinAnsi" default (which would happen to agree here,
        // so also cover MacRoman below to actually distinguish the paths).
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <99> Tj ET",
            baseEncoding: "WinAnsiEncoding",
            differences: "[1 /uni0041]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("™"); // WinAnsi trademark sign
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_CodeNotInDifferences_FallsBackToMacRomanBaseEncoding()
    {
        // Same shape as above but with /BaseEncoding /MacRomanEncoding —
        // proves the fallback actually reads /BaseEncoding rather than
        // coincidentally matching WinAnsi's default.
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <80> Tj ET",
            baseEncoding: "MacRomanEncoding",
            differences: "[1 /uni0041]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Ä"); // MacRoman code 0x80 => Ä
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_UnrecognizedGlyphName_FallsBackToDefaultDecodeForThatCode()
    {
        // A Differences entry naming a glyph this codebase's AGL subset
        // doesn't recognize must not throw or invent new guessing logic —
        // it falls through to the same default WinAnsi decode as an
        // unmapped code (#662 fix scope note: "don't regress").
        var pdfData = CreatePdfWithDifferencesEncoding(
            content: "BT /F1 12 Tf 100 700 Td <41> Tj ET",
            baseEncoding: null,
            differences: "[65 /some-totally-unrecognized-glyph-name]");
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("A"); // DecodeWinAnsi(0x41) == 'A'
    }

    [Fact]
    public void ExtractText_DifferencesEncoding_ToUnicodePresent_TakesPriorityOverDifferences()
    {
        // /ToUnicode remains the highest-priority source even when
        // /Differences is also present (matches the pre-existing priority
        // for the bare-encoding-name path).
        var pdfData = CreatePdfWithDifferencesEncodingAndToUnicode(
            content: "BT /F1 12 Tf 100 700 Td <01> Tj ET",
            differences: "[1 /uni0041]",
            toUnicodeBfChar: "<01> <005A>"); // maps code 1 to 'Z' instead of 'A'
        using var doc = PdfDocument.Open(pdfData);
        var extractor = new TextExtractor(doc.GetPage(1));

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Z");
    }

    #region Helper Methods

    private static byte[] CreatePdfWithDifferencesEncoding(string content, string? baseEncoding, string differences)
    {
        var baseEncodingEntry = baseEncoding != null ? $"/BaseEncoding /{baseEncoding} " : "";
        var encodingDict = $"<< {baseEncodingEntry}/Differences {differences} >>";
        return BuildPdf(content, $"<< /Type /Font /Subtype /TrueType /BaseFont /CustomSubset /Encoding {encodingDict} >>");
    }

    private static byte[] CreatePdfWithDifferencesEncodingAndToUnicode(string content, string differences, string toUnicodeBfChar)
    {
        var toUnicodeCMap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "1 beginbfchar\n" +
            $"{toUnicodeBfChar}\n" +
            "endbfchar\n" +
            "end end";

        var encodingDict = $"<< /Differences {differences} >>";
        return BuildPdf(
            content,
            $"<< /Type /Font /Subtype /TrueType /BaseFont /CustomSubset /Encoding {encodingDict} /ToUnicode 6 0 R >>",
            toUnicodeCMap);
    }

    private static byte[] BuildPdf(string content, string fontDict, string? toUnicodeStream = null)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var maxObj = toUnicodeStream != null ? 6 : 5;
        var offsets = new long[maxObj + 1];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font with the caller-supplied /Encoding dictionary
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine(fontDict);
        writer.WriteLine("endobj");
        writer.Flush();

        if (toUnicodeStream != null)
        {
            // Object 6: ToUnicode CMap stream
            offsets[6] = ms.Position;
            writer.WriteLine("6 0 obj");
            writer.WriteLine($"<< /Length {toUnicodeStream.Length} >>");
            writer.WriteLine("stream");
            writer.Write(toUnicodeStream);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();
        }

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {maxObj + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= maxObj; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size {maxObj + 1} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion
}
