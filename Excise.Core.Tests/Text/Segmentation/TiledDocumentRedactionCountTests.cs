using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1101 — a tiled document is one canvas cropped to several MediaBox windows.
/// On <c>test-pdfs/pdfjs/issue1350.pdf</c> three pages are byte-identical copies
/// of one canvas shown through three windows; RedactText read each page's full
/// (unclipped) letter set and counted the same canvas once per page, reporting
/// 36 removals of a term a reader — and mutool — sees 9 times.
///
/// <para>The count is the one number <c>excise redact</c> prints and a user acts
/// on. The fix tallies only matches VISIBLE in each page's own window, while
/// REMOVAL keeps full unclipped reach — a string in a page's content stream is
/// extractable, and therefore a leak, even where that page's window does not
/// show it (SavedPdfLeakScanner: anywhere in the file, any carrier). This gate
/// pins both halves on a synthetic two-page tile, with no gitignored corpus.</para>
/// </summary>
public class TiledDocumentRedactionCountTests
{
    private const string Secret = "SECRET";

    /// <summary>
    /// Two pages, each with its OWN content stream (separate objects, like
    /// issue1350's obj 4/17/18) showing "SECRET" at y=700. Page 1's window is
    /// the full 792pt height and shows it; page 2's window is only 400pt tall,
    /// so the same string is off-window there — visible on neither the page nor
    /// in mutool, but still present in page 2's content-stream bytes.
    /// </summary>
    private static byte[] BuildTwoPageTile()
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({Secret}) Tj ET\n";
        var body = Encoding.Latin1.GetBytes(content);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        // Page 1: window shows y=700 (MediaBox top 792).
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + "/Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n");
        // Page 2: window ends at y=400, so y=700 is off-window.
        W("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 400] "
          + "/Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n");
        W($"5 0 obj\n<< /Length {body.Length} >>\nstream\n"); ms.Write(body); W("\nendstream\nendobj\n");
        W($"6 0 obj\n<< /Length {body.Length} >>\nstream\n"); ms.Write(body); W("\nendstream\nendobj\n");
        W("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        // No xref table — PdfDocument reconstructs from object scanning.
        W("trailer\n<< /Root 1 0 R /Size 8 >>\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] RedactAndSave(byte[] pdf, out int verified)
    {
        using var doc = PdfDocument.Open(pdf);
        verified = doc.RedactText(Secret).VerifiedRemovals;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void TheCountIsVisibleOccurrences_NotOncePerTileOfTheCanvas()
    {
        var pdf = BuildTwoPageTile();

        // Guard: both pages carry the string before redaction.
        using (var before = PdfDocument.Open(pdf))
        {
            before.GetPage(1).Text.Should().Contain(Secret, "page 1's window shows it");
            before.GetPage(2).Text.Should().NotContain(Secret,
                "page 2's window ends at y=400, so y=700 is off-window — the tiling premise");
        }

        RedactAndSave(pdf, out var verified);

        verified.Should().Be(1,
            "the term is VISIBLE once (page 1's window); before #1101 RedactText "
          + "counted the full shared canvas once per page and reported 2");
    }

    [Fact]
    public void RemovalKeepsFullReach_TheOffWindowCopyIsGoneToo()
    {
        var saved = RedactAndSave(BuildTwoPageTile(), out _);

        // Full reach: the off-window copy in page 2's stream must be gone from
        // the saved bytes, compressed streams included. Counting visible-only
        // must NOT narrow what gets removed, or the fix would trade an inflated
        // count for a real leak.
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "removal reach stays full even though the count is window-limited — "
          + "page 2's off-window copy is still a leak and must be removed");
    }
}
