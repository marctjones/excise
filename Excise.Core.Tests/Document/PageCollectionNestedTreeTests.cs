using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
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

    /// <summary>
    /// The inheritance a cloned page must carry with it (§7.7.3.4). Root
    /// (/MediaBox [0 0 100 100]) → Pages(/MediaBox [0 0 400 300] /Rotate 90
    /// /Resources 7 0 R) → p1, p2. The nearer /MediaBox must win; p2 declares
    /// its own /Rotate 180, which must beat the inherited 90; both pages
    /// inherit ONE indirect /Resources, which must stay one object.
    /// </summary>
    private static byte[] BuildInheritedAttributesPdf()
    {
        var objects = new List<(int objNum, string content)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 2 /MediaBox [0 0 100 100] >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 /MediaBox [0 0 400 300] /Rotate 90 /Resources 7 0 R >>"),
            (4, "<< /Type /Page /Parent 3 0 R /Contents 8 0 R >>"),
            (5, "<< /Type /Page /Parent 3 0 R /Rotate 180 /Contents 9 0 R >>"),
            (7, "<< /Font << /F1 6 0 R >> >>"),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        AddContentStream(objects, 8, "% marker-page-ONE\nBT /F1 24 Tf 50 100 Td (INHERITEDFONT) Tj ET\n");
        AddContentStream(objects, 9, "% marker-page-TWO\nBT /F1 24 Tf 50 100 Td (INHERITEDFONT) Tj ET\n");
        return BuildPdfBytes(objects);
    }

    private static void AssertInheritedAttributes(PdfPage page, int rotation)
    {
        page.Width.Should().Be(400, "the nearest ancestor /MediaBox wins, not the default 612");
        page.Height.Should().Be(300);
        page.Rotation.Should().Be(rotation);
        var font = page.GetFont("F1");
        font.Should().NotBeNull("/Resources lived on the Pages node, so the clone must carry it");
        font!.GetNameOrNull("BaseFont").Should().Be("Helvetica");
    }

    /// <summary>Asserts on saved-and-reopened OUTPUT pages, not on the clones in memory.</summary>
    private static List<PdfPage> AssertInheritedAttributesLanded(PdfDocument output, params int[] rotations)
    {
        var pages = PdfDocument.Open(output.SaveToBytes()).GetPages().ToList();
        pages.Should().HaveCount(rotations.Length);
        foreach (var (page, rotation) in pages.Zip(rotations))
            AssertInheritedAttributes(page, rotation);
        return pages;
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

    [Fact]
    public void Insert_FromNestedSource_CarriesTheInheritedAttributes()
    {
        using var target = PdfDocument.Open(BuildNestedTreePdf());
        using var source = PdfDocument.Open(BuildInheritedAttributesPdf());

        target.Pages.Insert(1, source.GetPage(1));
        target.Pages.Insert(2, source.GetPage(2));

        var inserted = target.GetPages().Skip(1).Take(2).ToList();
        foreach (var (page, rotation) in inserted.Zip(new[] { 90, 180 }))
            AssertInheritedAttributes(page, rotation);
    }

    [Fact]
    public void Merge_FromNestedSource_CarriesTheInheritedAttributes()
    {
        using var source = PdfDocument.Open(BuildInheritedAttributesPdf());

        using var merged = PdfDocumentMerger.Merge(new[] { (source, (IReadOnlyList<int>)new[] { 0, 1 }) });

        var pages = AssertInheritedAttributesLanded(merged, 90, 180);
        pages[0].Dictionary.GetOptional("Resources").Should().BeOfType<PdfReference>()
            .Which.Should().Be(pages[1].Dictionary.GetOptional("Resources"),
                "a shared indirect /Resources stays one object");
    }

    [Fact]
    public void Split_FromNestedSource_CarriesTheInheritedAttributes()
    {
        using var source = PdfDocument.Open(BuildInheritedAttributesPdf());

        var fragments = PdfDocumentSplitter.SplitToSinglePages(source);

        fragments.Should().HaveCount(2);
        AssertInheritedAttributesLanded(fragments[0], 90);
        AssertInheritedAttributesLanded(fragments[1], 180);
        foreach (var fragment in fragments)
            fragment.Dispose();
    }

    /// <summary>
    /// document.json's "Document catalog, page tree, inheritance, and page
    /// geometry" preserve claim: PagesA's inheritable /Rotate 90 and
    /// /Resources survive a full save+reparse and still resolve correctly on
    /// its leaves, which declare neither locally. A naive writer that
    /// flattens inherited attributes onto leaves while reading but does not
    /// restore the /Parent chain on write would break exactly this -- the
    /// individual dictionaries could each look fine in isolation while the
    /// INHERITANCE ITSELF silently stopped resolving.
    /// </summary>
    [Fact]
    public void InheritedRotateAndResources_SurviveASaveAndReparse_StillResolveOnLeaves()
    {
        using var doc = PdfDocument.Open(BuildNestedTreePdf());
        using var reopened = PdfDocument.Open(doc.SaveToBytes());

        foreach (var marker in new[] { "marker-page-ONE", "marker-page-TWO" })
        {
            var page = reopened.GetPages().Single(p => Marker(p) == marker);
            page.Rotation.Should().Be(90,
                $"{marker}: /Rotate is inherited from the intermediate Pages node, not declared locally");
            page.Resources.Should().NotBeNull($"{marker}: /Resources is inherited from the intermediate Pages node");
            page.Resources!.GetArray("ProcSet").Select(v => v.ToString()).Should().Contain("/PDF");
        }

        // The third leaf sits under a DIFFERENT intermediate node with no
        // /Rotate/-/Resources of its own -- must NOT pick up PagesA's values.
        var unrelated = reopened.GetPages().Single(p => Marker(p) == "marker-page-THREE");
        unrelated.Rotation.Should().Be(0, "marker-page-THREE's ancestor never declared /Rotate");
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
