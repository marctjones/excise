using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// #884 — two recoveries that made excise refuse files mutool, pdftocairo and
/// qpdf all read. Both fixtures are authored here, so neither depends on the
/// gitignored pdfium corpus.
/// </summary>
public class CrOnlyLineEndingRecoveryTests
{
    /// <summary>
    /// CR-ONLY LINE ENDINGS + a wrong xref.
    ///
    /// The offset-repair path was reached and did nothing, because the regex
    /// that finds object headers was anchored <c>(?m)^</c> and .NET's multiline
    /// <c>^</c> matches only after <b>\n</b> — never after a lone <b>\r</b>. On
    /// pdfium's linearized_bug_1055.pdf it found <b>1 of 37</b> headers, so
    /// three pages were lost.
    ///
    /// This fixture reproduces the shape exactly: every line ends with a bare
    /// CR, and every xref offset is deliberately 5 bytes early — the same
    /// off-by-5 that file has, landing inside the preceding "endobj".
    /// </summary>
    [Fact]
    public void CrOnlyLineEndings_WithWrongXrefOffsets_StillOpens()
    {
        var pdf = CrOnlyPdf(offsetSkew: -5);

        using var doc = PdfDocument.Open(pdf);

        doc.PageCount.Should().Be(1,
            "the xref offsets are wrong, so recovery must locate the object headers " +
            "itself — which it cannot do if it is blind to CR-only line endings");
        doc.GetPage(1).Width.Should().Be(200);
    }

    /// <summary>
    /// Control: the same file with CORRECT offsets must keep working, so the
    /// test above cannot pass merely because recovery runs on every document.
    /// </summary>
    [Fact]
    public void CrOnlyLineEndings_WithCorrectXrefOffsets_StillOpens()
    {
        using var doc = PdfDocument.Open(CrOnlyPdf(offsetSkew: 0));
        doc.PageCount.Should().Be(1);
    }

    /// <summary>
    /// A leaf page carrying the WRONG /Type. pdfium's bad_page_type.pdf marks
    /// its second page <c>/Type /Template</c>; excise dropped it, reporting one
    /// page where qpdf, pdfinfo and mutool all report two — losing an intact
    /// text page and six images.
    /// </summary>
    [Fact]
    public void PageLeafWithWrongType_IsStillTreatedAsAPage()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>\nendobj\n",
            // /Template, not /Page — and no /Kids, so it is a leaf.
            "4 0 obj\n<< /Type /Template /Parent 2 0 R /MediaBox [0 0 300 150] >>\nendobj\n",
        }, "\n");

        using var doc = PdfDocument.Open(pdf);

        doc.PageCount.Should().Be(2, "a leaf with a wrong /Type is still a page");
        doc.GetPage(2).Width.Should().Be(300);
    }

    /// <summary>
    /// The guard on that recovery. An empty or malformed <c>/Pages</c> node
    /// also has no <c>/Kids</c>; promoting those to pages would manufacture
    /// phantom pages across the corpus, which is why the rule requires /Type to
    /// be present AND not /Pages rather than the simpler "no /Kids means leaf".
    /// </summary>
    [Fact]
    public void EmptyPagesNode_IsNotPromotedToAPhantomPage()
    {
        var pdf = Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>\nendobj\n",
            // A /Pages node with no /Kids — malformed, but NOT a page.
            "4 0 obj\n<< /Type /Pages /Parent 2 0 R >>\nendobj\n",
        }, "\n");

        using var doc = PdfDocument.Open(pdf);

        doc.PageCount.Should().Be(1,
            "an empty /Pages node is not a leaf — counting it would invent a page");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] CrOnlyPdf(int offsetSkew) => Assemble(new[]
    {
        "1 0 obj\r<< /Type /Catalog /Pages 2 0 R >>\rendobj\r",
        "2 0 obj\r<< /Type /Pages /Kids [3 0 R] /Count 1 >>\rendobj\r",
        "3 0 obj\r<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\rendobj\r",
    }, "\r", offsetSkew);

    private static byte[] Assemble(string[] objects, string eol, int offsetSkew = 0)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7").Append(eol);
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref").Append(eol).Append("0 ").Append(objects.Length + 1).Append(eol);
        sb.Append("0000000000 65535 f ").Append(eol);
        foreach (var o in offsets)
            sb.Append(Math.Max(0, o + offsetSkew).ToString("D10")).Append(" 00000 n ").Append(eol);
        sb.Append("trailer").Append(eol)
          .Append("<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>").Append(eol)
          .Append("startxref").Append(eol).Append(xref).Append(eol).Append("%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
