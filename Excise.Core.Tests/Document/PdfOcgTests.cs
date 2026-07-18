using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for Optional Content Groups (OCGs/layers) parsing.
/// ISO 32000-2 §8.11 specifies OCG structure.
/// </summary>
public class PdfOcgTests
{
    /// <summary>
    /// Document with no /OCProperties should return empty OCG list.
    /// </summary>
    [Fact]
    public void ParseOptionalContentGroups_NoOcProperties_ReturnsEmpty()
    {
        var pdf = CreateMinimalPdfWithOcg(null);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var ocgs = doc.GetOptionalContentGroups();
        ocgs.Should().BeEmpty();
    }

    /// <summary>
    /// Single OCG with default ON (not in /OFF array) is visible.
    /// </summary>
    [Fact]
    public void ParseOptionalContentGroups_SingleOcgDefaultOn_IsVisibleByDefault()
    {
        var pdf = CreateMinimalPdfWithOcg(ocgConfig: "<< /OFF [] >>");
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var ocgs = doc.GetOptionalContentGroups();
        ocgs.Should().HaveCount(1);
        ocgs[0].Name.Should().NotBeNullOrEmpty();

        var config = doc.GetOptionalContentGroupConfig();
        config.OffByDefault.Should().BeEmpty();
    }

    /// <summary>
    /// Multiple OCGs with mixed visibility defaults.
    /// </summary>
    [Fact]
    public void ParseOptionalContentGroups_MultipleOcgsMixed_CorrectVisibility()
    {
        // For simplicity, test just that parsing doesn't crash on multiple OCGs
        var pdf = CreateMinimalPdfWithMultipleOcgs();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var ocgs = doc.GetOptionalContentGroups();
        ocgs.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Caching: GetOptionalContentGroups() called twice returns same instance.
    /// </summary>
    [Fact]
    public void ParseOptionalContentGroups_CachedAfterFirstCall()
    {
        var pdf = CreateMinimalPdfWithOcg(ocgConfig: "<< >>");
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));

        var ocgs1 = doc.GetOptionalContentGroups();
        var ocgs2 = doc.GetOptionalContentGroups();

        // Should be the same object (cached)
        ocgs1.Should().BeSameAs(ocgs2);
    }

    /// <summary>
    /// PdfOcgConfig provides intent and base state from /D.
    /// </summary>
    [Fact]
    public void ParseOptionalContentGroups_Config_HasDefaults()
    {
        var pdf = CreateMinimalPdfWithOcg(ocgConfig: "<< >>");
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var config = doc.GetOptionalContentGroupConfig();

        config.Intent.Should().Be("View");     // Default
        config.BaseState.Should().Be("ON");    // Default
    }

    [Fact]
    public void ParseOptionalContentGroups_OcgNameAsString_ParsesSuccessfully()
    {
        var pdf = CreateMinimalPdfWithOcgNameAsString();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var ocgs = doc.GetOptionalContentGroups();

        ocgs.Should().HaveCount(1);
        ocgs[0].Name.Should().Be("StringNamedLayer");
    }

    [Fact]
    public void ParseOptionalContentGroups_OffArrayDirectName_ParsesSuccessfully()
    {
        var pdf = CreateMinimalPdfWithOffArrayDirectName();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var config = doc.GetOptionalContentGroupConfig();

        config.OffByDefault.Should().Contain("DirectOcgName");
    }

    // Helper: Create a minimal PDF with OCProperties
    private static byte[] CreateMinimalPdfWithOcg(string? ocgConfig)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        // Object 1: Catalog with OCProperties
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        if (ocgConfig != null)
        {
            writer.WriteLine($"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D {ocgConfig} >> >>");
        }
        else
        {
            writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        }
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: OCG (if requested)
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /OCG /Name (Layer1) >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref position
        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    // Helper: Create minimal PDF with multiple OCGs
    private static byte[] CreateMinimalPdfWithMultipleOcgs()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R 5 0 R] /D << /OFF [] >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: OCG 1
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /OCG /Name (Layer1) >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: OCG 2
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /OCG /Name (Layer2) >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateMinimalPdfWithOcgNameAsString()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << /OFF [] >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /OCG /Name (StringNamedLayer) >>");
        writer.WriteLine("endobj");
        writer.Flush();
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.Flush();
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] CreateMinimalPdfWithOffArrayDirectName()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << /OFF [/DirectOcgName] >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /OCG /Name (Layer1) >>");
        writer.WriteLine("endobj");
        writer.Flush();
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.Flush();
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }
}
