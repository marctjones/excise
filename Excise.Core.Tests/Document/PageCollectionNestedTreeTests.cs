using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Structural page operations on NESTED page trees (#961).
///
/// Move/RemoveAt/Insert index the root /Kids by global page number, which is
/// only correct on a flat tree. Before EnsureFlatKids, RemoveAt(0) on a
/// nested tree removed an intermediate /Pages node — silently deleting every
/// page under it — and Move threw. The corpus-backed conservation gates that
/// found this live in Excise.Rendering.Tests (ConservationGateTests, mutool
/// oracle); these are the corpus-free unit pins on synthetic trees, so the
/// logic is covered where CI's Excise.Core coverage gate can see it.
/// </summary>
public class PageCollectionNestedTreeTests
{
    /// <summary>
    /// Root → [ PagesA(p1, p2), PagesB(p3) ]. Each leaf's content stream
    /// carries a distinct marker so order and identity are checkable without
    /// font machinery. PagesA carries inheritable /Rotate and /Resources
    /// that p1/p2 do NOT declare locally.
    /// </summary>
    private static byte[] BuildNestedTreePdf()
    {
        var objects = new List<(int objNum, string content)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 3 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R 6 0 R] /Count 2 /Rotate 90 /Resources << /ProcSet [/PDF] >> >>"),
            (4, "<< /Type /Pages /Parent 2 0 R /Kids [7 0 R] /Count 1 >>"),
            (5, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 8 0 R >>"),
            (6, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 9 0 R >>"),
            (7, "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Contents 10 0 R >>"),
        };
        AddContentStream(objects, 8, "% marker-page-ONE\n");
        AddContentStream(objects, 9, "% marker-page-TWO\n");
        AddContentStream(objects, 10, "% marker-page-THREE\n");
        return BuildPdfBytes(objects);
    }

    /// <summary>
    /// The trap KidsAreFlat exists for: TWO root kids, TWO pages total —
    /// counts match, but kid[1] is not page 1 (all pages sit under the
    /// first intermediate; the second is empty).
    /// </summary>
    private static byte[] BuildCountCoincidencePdf()
    {
        var objects = new List<(int objNum, string content)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [5 0 R 6 0 R] /Count 2 >>"),
            (4, "<< /Type /Pages /Parent 2 0 R /Kids [] /Count 0 >>"),
            (5, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 7 0 R >>"),
            (6, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 8 0 R >>"),
        };
        AddContentStream(objects, 7, "% marker-page-ONE\n");
        AddContentStream(objects, 8, "% marker-page-TWO\n");
        return BuildPdfBytes(objects);
    }

    private static string Marker(PdfPage page)
    {
        var text = Encoding.ASCII.GetString(page.GetContentStreamBytes());
        var start = text.IndexOf("marker-page-", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "every synthetic page carries a marker");
        return text.Substring(start, text.IndexOf('\n', start) - start).TrimEnd();
    }

    private static string[] Markers(PdfDocument doc) =>
        doc.GetPages().Select(Marker).ToArray();

    [Fact]
    public void RemoveAt_OnNestedTree_RemovesExactlyOnePage()
    {
        using var doc = PdfDocument.Open(BuildNestedTreePdf());
        doc.PageCount.Should().Be(3);

        // Pre-#961 this removed intermediate PagesA — i.e. pages 1 AND 2.
        doc.Pages.RemoveAt(0);

        doc.PageCount.Should().Be(2);
        Markers(doc).Should().Equal("marker-page-TWO", "marker-page-THREE");

        // And the result must survive a save/reopen round-trip intact.
        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.PageCount.Should().Be(2);
        Markers(reopened).Should().Equal("marker-page-TWO", "marker-page-THREE");
    }

    [Fact]
    public void Move_OnNestedTree_IsAPurePermutation()
    {
        using var doc = PdfDocument.Open(BuildNestedTreePdf());

        // Pre-#961 this threw ArgumentOutOfRangeException from PdfArray.Insert
        // (toIndex 2 into a 2-entry root Kids).
        doc.Pages.Move(0, 2);

        doc.PageCount.Should().Be(3);
        Markers(doc).Should().Equal("marker-page-TWO", "marker-page-THREE", "marker-page-ONE");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        Markers(reopened).Should().Equal("marker-page-TWO", "marker-page-THREE", "marker-page-ONE");
    }

    [Fact]
    public void Flattening_MaterializesInheritedAttributesOntoLeaves()
    {
        using var doc = PdfDocument.Open(BuildNestedTreePdf());

        // p1 and p2 inherit /Rotate 90 and /Resources from intermediate
        // PagesA. Any structural op reparents them to the root, so those
        // values must be materialized onto the leaves or they are lost.
        doc.GetPage(1).Rotation.Should().Be(90, "sanity: inherited before the operation");

        doc.Pages.Move(0, 2);

        var movedP1 = doc.GetPages().Single(p => Marker(p) == "marker-page-ONE");
        movedP1.Rotation.Should().Be(90, "the inherited /Rotate must survive reparenting");
        movedP1.Dictionary.GetOptional("Rotate").Should().NotBeNull("materialized locally, not re-inherited");
        movedP1.Dictionary.GetOptional("Resources").Should().NotBeNull("inherited /Resources must be materialized");

        var p3 = doc.GetPages().Single(p => Marker(p) == "marker-page-THREE");
        p3.Rotation.Should().Be(0, "PagesB never carried /Rotate — nothing may leak across subtrees");
    }

    [Fact]
    public void RemoveAt_WhenRootKidCountCoincidesWithPageCount_StillRemovesTheRightPage()
    {
        // kids.Count == pages.Count here, yet the tree is NOT flat. A count
        // comparison alone would treat root kid 0 (the intermediate holding
        // BOTH pages) as page 0.
        using var doc = PdfDocument.Open(BuildCountCoincidencePdf());
        doc.PageCount.Should().Be(2);

        doc.Pages.RemoveAt(0);

        doc.PageCount.Should().Be(1);
        Markers(doc).Should().Equal("marker-page-TWO");
    }

    [Fact]
    public void Insert_OnNestedTree_LandsAtTheRequestedIndex()
    {
        using var doc = PdfDocument.Open(BuildNestedTreePdf());
        using var source = PdfDocument.Open(BuildCountCoincidencePdf());

        doc.Pages.Insert(1, source.GetPage(1));

        doc.PageCount.Should().Be(4);
        // The inserted page is a clone of source page 1 ("marker-page-ONE"
        // from the other document); positions 0/2/3 keep this doc's order.
        Markers(doc).Should().Equal(
            "marker-page-ONE", "marker-page-ONE", "marker-page-TWO", "marker-page-THREE");
    }

    // ------------------------------------------------------------- plumbing

    private static void AddContentStream(List<(int objNum, string content)> objects, int objNum, string content)
    {
        objects.Add((objNum, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"));
    }

    private static byte[] BuildPdfBytes(List<(int objNum, string content)> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var sorted = objects.OrderBy(x => x.objNum).ToList();
        var offsets = new Dictionary<int, long>();
        foreach (var (objNum, content) in sorted)
        {
            offsets[objNum] = Encoding.UTF8.GetByteCount(sb.ToString());
            sb.Append($"{objNum} 0 obj\n{content}\nendobj\n");
        }

        long xrefPos = Encoding.UTF8.GetByteCount(sb.ToString());
        var maxObj = sorted.Max(x => x.objNum);
        sb.Append("xref\n");
        sb.Append($"0 {maxObj + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= maxObj; i++)
        {
            sb.Append(offsets.TryGetValue(i, out var offset)
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 00000 f \n");
        }
        sb.Append($"trailer\n<< /Size {maxObj + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
