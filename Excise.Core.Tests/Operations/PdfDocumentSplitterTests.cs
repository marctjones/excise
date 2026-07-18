using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// #628: splitting must group pages correctly under each policy, and a
/// link between two pages that land in the SAME output fragment must
/// still resolve (a free correctness improvement from reusing the fixed
/// merge page-cloning path instead of the old single-page clone).
/// </summary>
public class PdfDocumentSplitterTests
{
    /// <summary>
    /// Build a document of <paramref name="pageCount"/> pages. Page 1 (index
    /// 0) carries an internal link to page 2 (index 1) so a policy that
    /// keeps both in the same fragment can be checked to preserve it. Each
    /// page whose 1-based number is in <paramref name="bookmarkPages"/> gets
    /// a root-level outline entry pointing at itself.
    /// </summary>
    private static byte[] BuildDocument(int pageCount, IReadOnlyList<int> bookmarkPages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        var offsets = new Dictionary<int, long>();

        int firstPageObj = 3;
        int catalogObj = 1;
        int pagesObj = 2;

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObj + i} 0 R"));

        // Outline objects, if any, come after all page objects.
        int outlineRootObj = firstPageObj + pageCount;
        var outlineItemObjs = bookmarkPages.Select((_, i) => outlineRootObj + 1 + i).ToList();

        bool hasOutline = bookmarkPages.Count > 0;
        string catalogExtra = hasOutline ? $" /Outlines {outlineRootObj} 0 R" : "";

        offsets[catalogObj] = sb.Length;
        sb.AppendLine($"{catalogObj} 0 obj");
        sb.AppendLine($"<< /Type /Catalog /Pages {pagesObj} 0 R{catalogExtra} >>");
        sb.AppendLine("endobj");

        offsets[pagesObj] = sb.Length;
        sb.AppendLine($"{pagesObj} 0 obj");
        sb.AppendLine($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        sb.AppendLine("endobj");

        for (int i = 0; i < pageCount; i++)
        {
            int objNum = firstPageObj + i;
            offsets[objNum] = sb.Length;
            sb.AppendLine($"{objNum} 0 obj");
            sb.AppendLine($"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] >>");
            sb.AppendLine("endobj");
        }

        if (hasOutline)
        {
            offsets[outlineRootObj] = sb.Length;
            sb.AppendLine($"{outlineRootObj} 0 obj");
            sb.AppendLine($"<< /Type /Outlines /First {outlineItemObjs[0]} 0 R /Last {outlineItemObjs[^1]} 0 R /Count {outlineItemObjs.Count} >>");
            sb.AppendLine("endobj");

            for (int i = 0; i < outlineItemObjs.Count; i++)
            {
                int objNum = outlineItemObjs[i];
                int targetPageObj = firstPageObj + (bookmarkPages[i] - 1);
                offsets[objNum] = sb.Length;
                sb.AppendLine($"{objNum} 0 obj");
                var links = new List<string> { $"/Parent {outlineRootObj} 0 R" };
                if (i > 0) links.Add($"/Prev {outlineItemObjs[i - 1]} 0 R");
                if (i + 1 < outlineItemObjs.Count) links.Add($"/Next {outlineItemObjs[i + 1]} 0 R");
                sb.AppendLine($"<< /Title (Bookmark {bookmarkPages[i]}) {string.Join(" ", links)} /Dest [{targetPageObj} 0 R /XYZ 0 0 0] >>");
                sb.AppendLine("endobj");
            }
        }

        long xrefPos = sb.Length;
        int maxObj = offsets.Keys.Max();
        sb.AppendLine("xref");
        sb.AppendLine($"0 {maxObj + 1}");
        sb.AppendLine("0000000000 65535 f ");
        for (int n = 1; n <= maxObj; n++)
            sb.AppendLine($"{offsets[n]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($"<< /Size {maxObj + 1} /Root {catalogObj} 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Build a document where page 1 has a real /Link annotation (not the placeholder from BuildDocument) to page 2.</summary>
    private static byte[] BuildDocumentWithRealLink(int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        var offsets = new Dictionary<int, long>();
        int firstPageObj = 3;
        int linkObj = firstPageObj + pageCount;
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObj + i} 0 R"));

        offsets[1] = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        offsets[2] = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        sb.AppendLine("endobj");

        for (int i = 0; i < pageCount; i++)
        {
            int objNum = firstPageObj + i;
            offsets[objNum] = sb.Length;
            sb.AppendLine($"{objNum} 0 obj");
            string annots = i == 0 ? $" /Annots [{linkObj} 0 R]" : "";
            sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{annots} >>");
            sb.AppendLine("endobj");
        }

        offsets[linkObj] = sb.Length;
        sb.AppendLine($"{linkObj} 0 obj");
        sb.AppendLine($"<< /Type /Annot /Subtype /Link /Rect [0 0 100 20] /Dest [{firstPageObj + 1} 0 R /XYZ 0 0 0] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        int maxObj = offsets.Keys.Max();
        sb.AppendLine("xref");
        sb.AppendLine($"0 {maxObj + 1}");
        sb.AppendLine("0000000000 65535 f ");
        for (int n = 1; n <= maxObj; n++)
            sb.AppendLine($"{offsets[n]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($"<< /Size {maxObj + 1} /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void SplitEveryNPages_GroupsPagesIntoFixedSizeChunks_LastChunkSmaller()
    {
        var bytes = BuildDocument(5, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitEveryNPages(source, 2));

        fragments.Items.Should().HaveCount(3);
        fragments.Items[0].PageCount.Should().Be(2);
        fragments.Items[1].PageCount.Should().Be(2);
        fragments.Items[2].PageCount.Should().Be(1);
    }

    [Fact]
    public void SplitAtPageBoundaries_UsesExplicitStartIndices()
    {
        var bytes = BuildDocument(6, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitAtPageBoundaries(source, [0, 4]));

        fragments.Items.Should().HaveCount(2);
        fragments.Items[0].PageCount.Should().Be(4);
        fragments.Items[1].PageCount.Should().Be(2);
    }

    [Fact]
    public void SplitAtPageBoundaries_ImpliesLeadingZeroBoundaryIfMissing()
    {
        var bytes = BuildDocument(6, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitAtPageBoundaries(source, [3]));

        fragments.Items.Should().HaveCount(2);
        fragments.Items[0].PageCount.Should().Be(3);
        fragments.Items[1].PageCount.Should().Be(3);
    }

    [Fact]
    public void SplitToSinglePages_BurstsOneDocumentPerPage()
    {
        var bytes = BuildDocument(4, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitToSinglePages(source));

        fragments.Items.Should().HaveCount(4);
        fragments.Items.Should().AllSatisfy(f => f.PageCount.Should().Be(1));
    }

    [Fact]
    public void SplitAtBookmarks_UsesRootLevelOutlineDestinationsAsBoundaries()
    {
        // Bookmarks at pages 1 and 4 of a 6-page document -> fragments [1-3], [4-6].
        var bytes = BuildDocument(6, [1, 4]);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitAtBookmarks(source));

        fragments.Items.Should().HaveCount(2);
        fragments.Items[0].PageCount.Should().Be(3);
        fragments.Items[1].PageCount.Should().Be(3);
    }

    [Fact]
    public void SplitAtBookmarks_NoOutline_FallsBackToOneFragmentWithWholeDocument()
    {
        var bytes = BuildDocument(3, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var fragments = new DisposableList(PdfDocumentSplitter.SplitAtBookmarks(source));

        fragments.Items.Should().HaveCount(1);
        fragments.Items[0].PageCount.Should().Be(3);
    }

    [Fact]
    public void SplitEveryNPages_LinkBetweenPagesInSameFragment_StillResolves()
    {
        var bytes = BuildDocumentWithRealLink(4);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        // Pages 1-2 land in the same 2-page fragment as the link's source and target.
        using var fragments = new DisposableList(PdfDocumentSplitter.SplitEveryNPages(source, 2));

        fragments.Items[0].GetPage(1).GetLinks().Should().ContainSingle(l => l.DestinationPage == 2,
            "a link between two pages placed in the same output fragment must still resolve");
    }

    [Fact]
    public void SplitEveryNPages_RejectsNonPositiveChunkSize()
    {
        var bytes = BuildDocument(2, []);
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        var act = () => PdfDocumentSplitter.SplitEveryNPages(source, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Disposes every fragment document when the test scope ends.</summary>
    private sealed class DisposableList : IDisposable
    {
        public IReadOnlyList<PdfDocument> Items { get; }
        public DisposableList(IReadOnlyList<PdfDocument> items) => Items = items;
        public void Dispose()
        {
            foreach (var doc in Items) doc.Dispose();
        }
    }
}
