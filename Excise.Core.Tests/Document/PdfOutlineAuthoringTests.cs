using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the outline (bookmark) authoring API added for #1412 —
/// PdfOutline.cs previously only parsed /Outlines, it could not write one.
/// </summary>
public class PdfOutlineAuthoringTests
{
    /// <summary>Two-page PDF with no /Outlines. Smallest base for authoring tests.</summary>
    private static byte[] TwoPagePdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void AddOutlineItem_OnPdfWithoutOutlines_CreatesOutlinesAndItem()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());
        PdfOutlineParser.Parse(doc).Should().BeEmpty("baseline: no /Outlines yet");

        doc.AddOutlineItem("Chapter 1", pageNumber: 1);

        var outline = PdfOutlineParser.Parse(doc);
        outline.Should().HaveCount(1);
        outline[0].Title.Should().Be("Chapter 1");
        outline[0].PageNumber.Should().Be(1);
        outline[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void AddOutlineItem_Twice_AppendsAsSecondTopLevelSibling()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());

        doc.AddOutlineItem("Chapter 1", pageNumber: 1);
        doc.AddOutlineItem("Chapter 2", pageNumber: 2);

        var outline = PdfOutlineParser.Parse(doc);
        outline.Should().HaveCount(2);
        outline[0].Title.Should().Be("Chapter 1");
        outline[0].PageNumber.Should().Be(1);
        outline[1].Title.Should().Be("Chapter 2");
        outline[1].PageNumber.Should().Be(2);
    }

    [Fact]
    public void AddChildOutlineItem_NestsUnderParent()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());

        var parentRef = doc.AddOutlineItem("Chapter 1", pageNumber: 1);
        doc.AddChildOutlineItem(parentRef, "Section 1.1", pageNumber: 1);
        doc.AddChildOutlineItem(parentRef, "Section 1.2", pageNumber: 2);

        var outline = PdfOutlineParser.Parse(doc);
        outline.Should().HaveCount(1);
        outline[0].Children.Should().HaveCount(2);
        outline[0].Children[0].Title.Should().Be("Section 1.1");
        outline[0].Children[1].Title.Should().Be("Section 1.2");
        outline[0].Children[1].PageNumber.Should().Be(2);
    }

    [Fact]
    public void AddOutlineItem_InvalidPageNumber_Throws()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());

        var act = () => doc.AddOutlineItem("Nowhere", pageNumber: 99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddOutlineItem_SurvivesASaveAndReload()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());
        var parentRef = doc.AddOutlineItem("Chapter 1", pageNumber: 1);
        doc.AddChildOutlineItem(parentRef, "Section 1.1", pageNumber: 2);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        var outline = PdfOutlineParser.Parse(reopened);

        outline.Should().HaveCount(1);
        outline[0].Title.Should().Be("Chapter 1");
        outline[0].Children.Should().HaveCount(1);
        outline[0].Children[0].Title.Should().Be("Section 1.1");
        outline[0].Children[0].PageNumber.Should().Be(2);
    }

    [Fact]
    public void AddOutlineItem_OnPdfWithExistingOutline_AppendsRatherThanReplacing()
    {
        using var doc = PdfDocument.Open(TwoPagePdf());
        doc.AddOutlineItem("Chapter 1", pageNumber: 1);

        doc.AddOutlineItem("Chapter 2", pageNumber: 2);

        var outline = PdfOutlineParser.Parse(doc);
        outline.Should().HaveCount(2, "adding a second top-level item must not discard the first");
    }
}
