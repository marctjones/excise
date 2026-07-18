using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for <see cref="PdfOutlineItem"/> model and <see cref="PdfOutlineParser"/>
/// covering outline tree parsing, destination resolution, page ref mapping, and edge cases.
/// </summary>
public class PdfOutlineTests
{
    // ─── PdfOutlineItem model tests ─────────────────────────────────────────

    [Fact]
    public void PdfOutlineItem_Constructor_CapturesAllFields()
    {
        var title = "Test Outline";
        var pageNumber = 5;
        var children = new List<PdfOutlineItem>();

        var item = new PdfOutlineItem(title, pageNumber, children);

        item.Title.Should().Be(title);
        item.PageNumber.Should().Be(pageNumber);
        item.Children.Should().BeEmpty();
    }

    [Fact]
    public void PdfOutlineItem_NullPageNumber_Captured()
    {
        var item = new PdfOutlineItem("NoLink", null, new List<PdfOutlineItem>());

        item.PageNumber.Should().BeNull();
    }

    [Fact]
    public void PdfOutlineItem_WithChildren_ChildrenAccessible()
    {
        var children = new List<PdfOutlineItem>
        {
            new PdfOutlineItem("Child1", 2, new List<PdfOutlineItem>()),
            new PdfOutlineItem("Child2", 3, new List<PdfOutlineItem>())
        };
        var item = new PdfOutlineItem("Parent", 1, children);

        item.Children.Should().HaveCount(2);
        item.Children[0].Title.Should().Be("Child1");
        item.Children[1].Title.Should().Be("Child2");
    }

    // ─── PDF builders ───────────────────────────────────────────────────────

    /// <summary>Build a basic multi-page PDF with an outline.</summary>
    private static byte[] MakePdfWithOutline(string outlineDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($@"<<
            /Type /Catalog
            /Pages 2 0 R
            {outlineDef}
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ─── Test: basic parsing ────────────────────────────────────────────────

    [Fact]
    public void Parse_NoOutlines_ReturnsEmpty()
    {
        var pdf = MakePdfWithOutline("");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyOutlines_ReturnsEmpty()
    {
        var outlineDef = "/Outlines << /Type /Outlines >>";
        var pdf = MakePdfWithOutline(outlineDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleOutlineItem_Parsed()
    {
        var outlineDef = @"/Outlines <<
            /Type /Outlines
            /First 6 0 R
        >>";
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($@"<<
            /Type /Catalog
            /Pages 2 0 R
            {outlineDef}
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long outlineItemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (Chapter 1)
            /Dest [3 0 R /XYZ 0 0 0]
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{outlineItemPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Chapter 1");
        result[0].PageNumber.Should().Be(1);
    }

    // ─── Test: outline hierarchy ────────────────────────────────────────────

    [Fact]
    public void Parse_HierarchicalOutline_ChildrenExtracted()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long parentItemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (Chapter 1)
            /Dest [3 0 R /XYZ 0 0 0]
            /First 7 0 R
        >>");
        sb.AppendLine("endobj");

        long childItem1Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine(@"<<
            /Title (Section 1.1)
            /Dest [4 0 R /XYZ 0 0 0]
            /Next 8 0 R
            /Parent 6 0 R
        >>");
        sb.AppendLine("endobj");

        long childItem2Pos = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine(@"<<
            /Title (Section 1.2)
            /Dest [5 0 R /XYZ 0 0 0]
            /Parent 6 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 9");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine($"{parentItemPos:D10} 00000 n ");
        sb.AppendLine($"{childItem1Pos:D10} 00000 n ");
        sb.AppendLine($"{childItem2Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 9 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result.Should().HaveCount(1);
        var parent = result[0];
        parent.Title.Should().Be("Chapter 1");
        parent.Children.Should().HaveCount(2);
        parent.Children[0].Title.Should().Be("Section 1.1");
        parent.Children[1].Title.Should().Be("Section 1.2");
    }

    [Fact]
    public void Parse_MultipleRootOutlines_AllExtracted()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long item1Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (Chapter 1)
            /Dest [3 0 R /XYZ 0 0 0]
            /Next 7 0 R
        >>");
        sb.AppendLine("endobj");

        long item2Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine(@"<<
            /Title (Chapter 2)
            /Dest [4 0 R /XYZ 0 0 0]
            /Next 8 0 R
        >>");
        sb.AppendLine("endobj");

        long item3Pos = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine(@"<<
            /Title (Chapter 3)
            /Dest [5 0 R /XYZ 0 0 0]
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 9");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine($"{item1Pos:D10} 00000 n ");
        sb.AppendLine($"{item2Pos:D10} 00000 n ");
        sb.AppendLine($"{item3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 9 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result.Should().HaveCount(3);
        result[0].Title.Should().Be("Chapter 1");
        result[1].Title.Should().Be("Chapter 2");
        result[2].Title.Should().Be("Chapter 3");
    }

    // ─── Test: destination resolution ───────────────────────────────────────

    [Fact]
    public void Parse_OutlineItemWithGoToAction_ResolvesPage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long itemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (Go to Page 2)
            /A << /S /GoTo /D [4 0 R /XYZ 0 0 0] >>
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{itemPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result[0].PageNumber.Should().Be(2);
    }

    [Fact]
    public void Parse_OutlineItemWithoutDestination_NoPageNumber()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long itemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (No Destination)
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{itemPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result[0].PageNumber.Should().BeNull();
    }

    [Fact]
    public void Parse_OutlineItemWithNonGoToAction_NoPageNumber()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long itemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (External Link)
            /A << /S /URI /URI (https://example.com) >>
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{itemPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result[0].PageNumber.Should().BeNull();
    }

    // ─── Test: named destinations ───────────────────────────────────────────

    [Fact]
    public void BuildNamedDestinations_CatalogDests_Extracted()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Dests <<
                /MyDest [3 0 R /XYZ 0 0 0]
                /OtherDest [3 0 R /Fit]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.BuildNamedDestinations(doc);

        result.Should().NotBeNull();
        result!.Keys.Should().Contain("MyDest");
        result!.Keys.Should().Contain("OtherDest");
    }

    [Fact]
    public void BuildNamedDestinations_NoDests_ReturnsNull()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.BuildNamedDestinations(doc);

        result.Should().BeNull();
    }

    // ─── Test: page ref mapping ─────────────────────────────────────────────

    [Fact]
    public void BuildPageRefMap_MultiplePages_AllMapped()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        sb.AppendLine("endobj");

        long page1Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page2Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long page3Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{page1Pos:D10} 00000 n ");
        sb.AppendLine($"{page2Pos:D10} 00000 n ");
        sb.AppendLine($"{page3Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.BuildPageRefMap(doc);

        result.Should().NotBeNull();
        result.Should().HaveCount(3);
        result[(3, 0)].Should().Be(1);
        result[(4, 0)].Should().Be(2);
        result[(5, 0)].Should().Be(3);
    }

    [Fact]
    public void BuildPageRefMap_NoPages_ReturnsEmpty()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 2");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 2 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.BuildPageRefMap(doc);

        result.Should().BeEmpty();
    }

    // ─── Test: empty title handling ─────────────────────────────────────────

    [Fact]
    public void Parse_OutlineItemWithoutTitle_EmptyTitle()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long itemPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Dest [3 0 R /XYZ 0 0 0]
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{itemPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var result = PdfOutlineParser.Parse(doc);

        result[0].Title.Should().Be(string.Empty);
    }

    // ─── Test: cycle detection ──────────────────────────────────────────────

    [Fact]
    public void Parse_OutlineCycle_DoesNotHang()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /Outlines <<
                /Type /Outlines
                /First 6 0 R
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        // Create a cycle: Item 6 -> Next -> Item 7 -> Next -> Item 6
        long item6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Title (Item 1)
            /Dest [3 0 R /XYZ 0 0 0]
            /Next 7 0 R
        >>");
        sb.AppendLine("endobj");

        long item7Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine(@"<<
            /Title (Item 2)
            /Dest [3 0 R /XYZ 0 0 0]
            /Next 6 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f "); // obj0 free
        sb.AppendLine($"{catalogPos:D10} 00000 n "); // obj1
        sb.AppendLine($"{pagesPos:D10} 00000 n ");   // obj2
        sb.AppendLine($"{pagePos:D10} 00000 n ");    // obj3
        sb.AppendLine("0000000000 65535 f ");   // obj4 free
        sb.AppendLine("0000000000 65535 f ");   // obj5 free
        sb.AppendLine($"{item6Pos:D10} 00000 n "); // obj6
        sb.AppendLine($"{item7Pos:D10} 00000 n "); // obj7
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        // Should not hang, should detect cycle and break
        var result = PdfOutlineParser.Parse(doc);

        // Only items before cycle detected
        result.Should().HaveCount(2);
    }
}
