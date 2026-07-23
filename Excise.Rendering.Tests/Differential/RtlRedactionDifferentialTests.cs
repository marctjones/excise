using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// RTL extraction and redaction verified against an INDEPENDENT extractor
/// (mutool), per the no-self-oracle rule (#606, #632).
///
/// The unit tests in Excise.Core.Tests assert the logical-order strings that
/// mutool produces for these fixture bytes — but they hard-code that
/// expectation. This suite asks mutool directly, at test time:
///
///   1. Parity: excise's extraction of a visual-order Arabic fixture must
///      equal what mutool reads from the same file (logical order).
///   2. Leak check: after RedactText with a logical-order needle, mutool must
///      not recover the word from the saved file in either order.
///
/// Before the #632 fix, (1) failed (excise returned the reversed string) and
/// the redaction underlying (2) silently removed nothing — RedactText
/// returned 0 while mutool read the full word out of the "redacted" output.
/// </summary>
public class RtlRedactionDifferentialTests : IDisposable
{
    private const string ArabicWord = "سلام"; // logical order

    private readonly List<string> _temp = new();

    [Fact]
    public void RtlExtraction_MatchesIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(VisualOrderArabicPdf());

        var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
        mutoolText.Should().NotBeNull("mutool must read the fixture");
        mutoolText!.Trim().Should().Be(ArabicWord,
            "oracle sanity: mutool reads the visual-order stream as the logical-order word");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        doc.GetPage(1).Text.Trim().Should().Be(mutoolText.Trim(),
            "excise must read RTL text in the same logical order the independent extractor does; " +
            "a reversed reading here is exactly the state in which RedactText silently fails");
    }

    [Fact]
    public void RedactedRtlWord_IsNotRecoverableByAnIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(VisualOrderArabicPdf());
        var removed = doc.RedactText(ArabicWord);
        removed.Should().BeGreaterThan(0,
            "a logical-order needle must match the visual-order glyph run");

        var path = WriteTemp(doc.SaveToBytes());

        var extracted = MutoolTextExtractor.ExtractPage(path, 1);
        extracted.Should().NotBeNull("mutool must be able to read the redacted file at all");
        extracted!.Should().NotContain(ArabicWord,
            "mutool reads every text carrier in the file; recovering the word means the " +
            "redaction was cosmetic");
        extracted.Should().NotContain(Reverse(ArabicWord),
            "the word must not survive in visual (reversed) order either");
    }

    /// <summary>
    /// Minimal PDF: Arabic word carried as codes 'DCBA' (visual order — the
    /// common producer encoding) with a /ToUnicode CMap mapping codes
    /// 0x41..0x44 to the logical-order scalars U+0633 U+0644 U+0627 U+0645.
    /// Same shape as Excise.Core.Tests.Text.RtlPdfFixtures.SingleTj.
    /// </summary>
    private static byte[] VisualOrderArabicPdf()
    {
        const string content = "BT /F1 24 Tf 100 700 Td (DCBA) Tj ET";
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "4 beginbfchar\n" +
            "<41> <0633>\n<42> <0644>\n<43> <0627>\n<44> <0645>\n" +
            "endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine($"4 0 obj\n<< /Length {content.Length} >>\nstream");
        writer.WriteLine(content);
        writer.WriteLine("endstream\nendobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>\nendobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine($"6 0 obj\n<< /Length {cmap.Length} >>\nstream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream\nendobj");

        long xrefPos = Flush(writer, ms);
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtl-diff-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
