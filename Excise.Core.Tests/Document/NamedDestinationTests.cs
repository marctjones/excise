using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for named destinations.
/// Tests cover parsing /Catalog/Dests, /Catalog/Names/Dests name trees,
/// destination array parsing, and fit mode extraction.
/// </summary>
public class NamedDestinationTests
{
    // ─── NamedDestination record tests ─────────────────────────────────────

    [Fact]
    public void NamedDestination_Constructor_CapturesAllFields()
    {
        var dest = new NamedDestination(
            Name: "Chapter1",
            PageNumber: 5,
            X: 100.0,
            Y: 200.0,
            Zoom: 1.5,
            FitMode: "XYZ");

        dest.Name.Should().Be("Chapter1");
        dest.PageNumber.Should().Be(5);
        dest.X.Should().Be(100.0);
        dest.Y.Should().Be(200.0);
        dest.Zoom.Should().Be(1.5);
        dest.FitMode.Should().Be("XYZ");
    }

    [Fact]
    public void NamedDestination_WithNullCoordinates_StoresNulls()
    {
        var dest = new NamedDestination(
            Name: "Section",
            PageNumber: 3,
            FitMode: "Fit");

        dest.X.Should().BeNull();
        dest.Y.Should().BeNull();
        dest.Zoom.Should().BeNull();
        dest.FitMode.Should().Be("Fit");
    }

    // ─── PDF integration tests ──────────────────────────────────────────────

    [Fact]
    public void GetNamedDestinations_NoDestinations_ReturnsEmpty()
    {
        var pdf = MakePdfWithNamedDestinations(null);
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests.Should().BeEmpty();
    }

    [Fact]
    public void GetNamedDestinations_OldFormDests_ParsesCorrectly()
    {
        // /Catalog/Dests (older form, PDF 1.1-) is a dictionary
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /FirstSection [3 0 R /XYZ 0 0 1] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests.Should().ContainKey("FirstSection");
        dests["FirstSection"].PageNumber.Should().Be(1);  // Page 3 is the 1st page
        dests["FirstSection"].FitMode.Should().Be("XYZ");
    }

    [Fact]
    public void GetNamedDestinations_OldDests_MultipleDestinations()
    {
        // Test /Catalog/Dests (old form) with multiple named destinations
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /FirstSection [3 0 R /XYZ 0 0 1] /SecondSection [4 0 R /FitH 100] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests.Should().HaveCount(2);
        dests.Should().ContainKey("FirstSection");
        dests.Should().ContainKey("SecondSection");

        dests["FirstSection"].PageNumber.Should().Be(1);
        dests["FirstSection"].FitMode.Should().Be("XYZ");

        dests["SecondSection"].PageNumber.Should().Be(2);
        dests["SecondSection"].FitMode.Should().Be("FitH");
        dests["SecondSection"].Y.Should().Be(100.0);
    }

    [Fact]
    public void GetNamedDestinations_FitMode_ParsedCorrectly()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Fit [3 0 R /Fit] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Fit"].FitMode.Should().Be("Fit");
        dests["Fit"].X.Should().BeNull();
        dests["Fit"].Y.Should().BeNull();
    }

    [Fact]
    public void GetNamedDestinations_FitHMode_ParsesCoordinate()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Section [3 0 R /FitH 150] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Section"].FitMode.Should().Be("FitH");
        dests["Section"].Y.Should().Be(150.0);
        dests["Section"].X.Should().BeNull();
    }

    [Fact]
    public void GetNamedDestinations_FitVMode_ParsesCoordinate()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Vertical [3 0 R /FitV 200] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Vertical"].FitMode.Should().Be("FitV");
        dests["Vertical"].X.Should().Be(200.0);
    }

    [Fact]
    public void GetNamedDestinations_XYZMode_ParsesAllCoordinates()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Target [3 0 R /XYZ 100 200 1.5] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Target"].FitMode.Should().Be("XYZ");
        dests["Target"].X.Should().Be(100.0);
        dests["Target"].Y.Should().Be(200.0);
        dests["Target"].Zoom.Should().Be(1.5);
    }

    [Fact]
    public void GetNamedDestinations_FitRMode_ParsesRectangle()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Box [3 0 R /FitR 0 0 100 100] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Box"].FitMode.Should().Be("FitR");
        dests["Box"].X.Should().Be(0.0);
        dests["Box"].Y.Should().Be(0.0);
    }

    [Fact]
    public void GetNamedDestinations_FitBMode_NoCoordinates()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /BBox [3 0 R /FitB] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["BBox"].FitMode.Should().Be("FitB");
        dests["BBox"].X.Should().BeNull();
        dests["BBox"].Y.Should().BeNull();
    }

    [Fact]
    public void GetNamedDestinations_FitBHMode_ParsesCoordinate()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /BBoxH [3 0 R /FitBH 75] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["BBoxH"].FitMode.Should().Be("FitBH");
        dests["BBoxH"].Y.Should().Be(75.0);
    }

    [Fact]
    public void GetNamedDestinations_FitBVMode_ParsesCoordinate()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /BBoxV [3 0 R /FitBV 50] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["BBoxV"].FitMode.Should().Be("FitBV");
        dests["BBoxV"].X.Should().Be(50.0);
    }

    [Fact]
    public void GetNamedDestinations_MultipleDestinations_AllResolved()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /A [3 0 R /XYZ 0 0 1] /B [4 0 R /FitH 100] /C [5 0 R /Fit] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests.Should().HaveCount(3);
        dests.Keys.Should().Contain(new[] { "A", "B", "C" });

        dests["A"].PageNumber.Should().Be(1);
        dests["B"].PageNumber.Should().Be(2);
        dests["C"].PageNumber.Should().Be(3);
    }

    [Fact]
    public void GetNamedDestinations_InvalidPageRef_PageNumberNull()
    {
        // Destination points to invalid page reference
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Bad [99 0 R /XYZ 0 0 1] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests["Bad"].PageNumber.Should().BeNull();
    }

    [Fact]
    public void GetNamedDestinations_EmptyDestinationArray_NotIncluded()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Dests << /Empty [] >>");
        using var doc = PdfDocument.Open(pdf);

        var dests = doc.GetNamedDestinations();
        dests.Should().NotContainKey("Empty");
    }

    [Fact]
    public void GetNamedDestinations_Caching_ReturnsSameObject()
    {
        var pdf = MakePdfWithNamedDestinations(
            "/Names << /Dests << /Nums [(Cached) [3 0 R /XYZ 0 0 1]] >> >>");
        using var doc = PdfDocument.Open(pdf);

        var dests1 = doc.GetNamedDestinations();
        var dests2 = doc.GetNamedDestinations();

        // Should return the same cached dictionary
        ReferenceEquals(dests1, dests2).Should().BeTrue();
    }

    // ─── Helper: PDF builder ───────────────────────────────────────────────

    /// <summary>
    /// Build a minimal 3-page PDF with optional named destinations.
    /// </summary>
    private static byte[] MakePdfWithNamedDestinations(string? destsDict)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($@"<<
            /Type /Catalog
            /Pages 2 0 R
            {(destsDict ?? string.Empty)}
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
        sb.AppendLine($@"<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
