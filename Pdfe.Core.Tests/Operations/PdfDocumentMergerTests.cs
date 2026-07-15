using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Operations;
using Xunit;

namespace Pdfe.Core.Tests.Operations;

/// <summary>
/// #628: merging pages from multiple documents must not silently break
/// internal links, must splice each source's outline (bookmark) tree with
/// destinations pointing at the pages' NEW positions, and must merge
/// AcroForm fields without letting same-named fields from different
/// sources collide into one.
/// </summary>
public class PdfDocumentMergerTests
{
    /// <summary>
    /// Build a 2-page document with: an internal link on page 1 pointing to
    /// page 2, a 2-item root-level outline (one destination per page), and
    /// one AcroForm text field (also referenced from page 1's /Annots, so
    /// merge's clonedRefs dedup between page-copy and AcroForm-copy is
    /// exercised for real, not just in the AcroForm array).
    /// </summary>
    private static byte[] BuildDocument(string fieldName, string outlineTitle1, string outlineTitle2)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R /AcroForm << /Fields [9 0 R] >> >>");
        sb.AppendLine("endobj");

        long obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");

        long obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [9 0 R 10 0 R] >>");
        sb.AppendLine("endobj");

        long obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 2 >>");
        sb.AppendLine("endobj");

        long obj7Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine($"<< /Title ({outlineTitle1}) /Parent 6 0 R /Next 8 0 R /Dest [3 0 R /XYZ 0 0 0] >>");
        sb.AppendLine("endobj");

        long obj8Pos = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine($"<< /Title ({outlineTitle2}) /Parent 6 0 R /Prev 7 0 R /Dest [4 0 R /XYZ 0 0 0] >>");
        sb.AppendLine("endobj");

        long obj9Pos = sb.Length;
        sb.AppendLine("9 0 obj");
        sb.AppendLine($"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Rect [0 0 100 20] /P 3 0 R >>");
        sb.AppendLine("endobj");

        long obj10Pos = sb.Length;
        sb.AppendLine("10 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Link /Rect [0 100 100 120] /Dest [4 0 R /XYZ 0 0 0] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 11");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine($"{obj7Pos:D10} 00000 n ");
        sb.AppendLine($"{obj8Pos:D10} 00000 n ");
        sb.AppendLine($"{obj9Pos:D10} 00000 n ");
        sb.AppendLine($"{obj10Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 11 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Minimal 1-page document with no outline and no AcroForm.</summary>
    private static byte[] BuildPlainDocument()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void Merge_SingleSource_InternalLinkResolvesToPageWithinMergedDocument()
    {
        var bytes = BuildDocument("Name", "Chapter 1", "Chapter 2");
        using var source = PdfDocument.Open(new MemoryStream(bytes), false);

        using var target = PdfDocumentMerger.Merge([(source, new[] { 0, 1 })]);

        target.PageCount.Should().Be(2);
        var links = target.GetPage(1).GetLinks();
        links.Should().ContainSingle(l => l.DestinationPage == 2,
            "the link from page 1 to page 2 must resolve to the merged document's own page 2, " +
            "not be dropped as if it were a page-tree back-reference");
    }

    [Fact]
    public void Merge_TwoSources_PagesAppendInOrder_OutlineSplicedWithCorrectDestinations_LinksResolvePerSource()
    {
        var bytesA = BuildDocument("Name", "A Chapter 1", "A Chapter 2");
        var bytesB = BuildDocument("Name", "B Chapter 1", "B Chapter 2");
        using var sourceA = PdfDocument.Open(new MemoryStream(bytesA), false);
        using var sourceB = PdfDocument.Open(new MemoryStream(bytesB), false);

        using var target = PdfDocumentMerger.Merge(
        [
            (sourceA, new[] { 0, 1 }),
            (sourceB, new[] { 0, 1 }),
        ]);

        target.PageCount.Should().Be(4, "2 pages from each of 2 sources");

        // Outline: 4 root items, in source order, each pointing at the
        // page's NEW (post-merge) page number.
        var outline = PdfOutlineParser.Parse(target);
        outline.Should().HaveCount(4);
        outline[0].Title.Should().Be("A Chapter 1");
        outline[0].PageNumber.Should().Be(1);
        outline[1].Title.Should().Be("A Chapter 2");
        outline[1].PageNumber.Should().Be(2);
        outline[2].Title.Should().Be("B Chapter 1");
        outline[2].PageNumber.Should().Be(3);
        outline[3].Title.Should().Be("B Chapter 2");
        outline[3].PageNumber.Should().Be(4);

        // Links: each source's internal link must resolve within the
        // merged document to ITS OWN pages, not the other source's.
        target.GetPage(1).GetLinks().Should().ContainSingle(l => l.DestinationPage == 2);
        target.GetPage(3).GetLinks().Should().ContainSingle(l => l.DestinationPage == 4);

        // AcroForm: both sources' field is named "Name" — must not collide.
        var acroForm = target.GetAcroForm();
        acroForm.Should().NotBeNull();
        acroForm!.Fields.Should().HaveCount(2);
        acroForm.Fields.Select(f => f.FullName).Should().Contain("Name");
        acroForm.Fields.Select(f => f.FullName).Should().Contain("Name (2)");
    }

    [Fact]
    public void Merge_SourceWithNoOutlineOrAcroForm_DoesNotThrow_AndContributesNoFieldsOrOutline()
    {
        var plainBytes = BuildPlainDocument();
        using var source = PdfDocument.Open(new MemoryStream(plainBytes), false);

        using var target = PdfDocumentMerger.Merge([(source, new[] { 0 })]);

        target.PageCount.Should().Be(1);
        PdfOutlineParser.Parse(target).Should().BeEmpty();
        target.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void Merge_PlainSourceThenOutlinedSource_OutlineFromSecondSourceStillSplicesCorrectly()
    {
        var plainBytes = BuildPlainDocument();
        var outlinedBytes = BuildDocument("Name", "Only Chapter", "Second Chapter");
        using var plain = PdfDocument.Open(new MemoryStream(plainBytes), false);
        using var outlined = PdfDocument.Open(new MemoryStream(outlinedBytes), false);

        using var target = PdfDocumentMerger.Merge(
        [
            (plain, new[] { 0 }),
            (outlined, new[] { 0, 1 }),
        ]);

        target.PageCount.Should().Be(3);
        var outline = PdfOutlineParser.Parse(target);
        outline.Should().HaveCount(2, "the plain source contributes nothing; only the second source's outline splices in");
        outline[0].PageNumber.Should().Be(2, "second source's first page landed at merged position 2");
        outline[1].PageNumber.Should().Be(3);
    }

    [Fact]
    public void Merge_NoSources_Throws()
    {
        var act = () => PdfDocumentMerger.Merge([]);
        act.Should().Throw<ArgumentException>();
    }
}
