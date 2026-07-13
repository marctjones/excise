using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Xunit;

namespace Pdfe.Core.Tests.Text.Segmentation;

/// <summary>
/// #637: a real-world PDF (IRS-1040-instructions.pdf, page 47) wraps long
/// content-stream string literals mid-word using PDF's line-continuation
/// escape — REVERSE SOLIDUS immediately followed by a raw EOL byte, which
/// PDF32000-1 §7.3.4.2 Table 3 defines as producing NO character. Before the
/// fix, both <c>TextExtractor.ParseStringLiteral</c> and
/// <c>ContentStreamParser.ParseStringLiteral</c> instead inserted a spurious
/// literal newline, splitting "Instructions" into "Instruc\ntions". That
/// broke <c>RedactText</c>'s substring matching on the whole word: pdfe's own
/// extractor could never see "Instructions" as one token, so redaction
/// silently never matched it there — while an independent extractor (mutool)
/// read the line-continuation correctly and still recovered the "redacted"
/// word from the saved file.
///
/// Per CLAUDE.md, the content-stream-only assertion
/// (<c>page.Text.Should().NotContain(word)</c>) is NOT sufficient on its own
/// — it has passed on leaking documents before. This test asserts the
/// carrier-agnostic ground truth instead: the saved bytes, decoded as both
/// ASCII and UTF-16BE, must not contain the word in any carrier.
/// </summary>
public class LineContinuationRedactionLeakTests
{
    private const string Target = "Instructions";

    [Fact]
    public void RedactText_RemovesWordSplitByLineContinuationEscape()
    {
        var pdf = CreatePdfWithLineContinuationSplitWord();
        using var doc = PdfDocument.Open(pdf);

        var matches = doc.RedactText(Target);
        matches.Should().Be(1, "the word must be recognized as one token, not split across a spurious newline");

        var saved = Save(doc);

        Utf16AndAsciiOf(saved).Should().NotContain(Target,
            "the line-continuation escape must not defeat RedactText's substring matching — " +
            "an independent extractor that correctly collapses the escape (e.g. mutool) must not " +
            "still recover the word from the saved file");
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms);
        return ms.ToArray();
    }

    private static string Utf16AndAsciiOf(byte[] saved)
    {
        var ascii = Encoding.ASCII.GetString(saved);
        var utf16 = Encoding.BigEndianUnicode.GetString(saved);
        return ascii + "\n" + utf16;
    }

    /// <summary>
    /// A minimal PDF whose content stream contains the target word split by
    /// a real line-continuation escape: <c>(Instruc\&lt;LF&gt;tions)</c>.
    /// </summary>
    private static byte[] CreatePdfWithLineContinuationSplitWord()
    {
        var content = "BT /F1 24 Tf 100 700 Td (Instruc\\\ntions) Tj ET";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
