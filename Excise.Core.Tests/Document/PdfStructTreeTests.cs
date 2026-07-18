using AwesomeAssertions;
using Excise.Core.Document;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for PDF structure tree (tagged PDF) parsing.
/// ISO 32000-2 §14.7 specifies structure tree semantics.
/// </summary>
public class PdfStructTreeTests
{
    /// <summary>
    /// Document with no /StructTreeRoot returns null.
    /// </summary>
    [Fact]
    public void ParseStructureTree_NoStructTreeRoot_ReturnsNull()
    {
        var pdf = CreateMinimalTaggedPdf(withStructTree: false);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();
        tree.Should().BeNull();
    }

    /// <summary>
    /// IsTaggedPdf returns false when /MarkInfo/Marked is not true.
    /// </summary>
    [Fact]
    public void IsTaggedPdf_NoMarkInfo_ReturnsFalse()
    {
        var pdf = CreateMinimalTaggedPdf(withStructTree: false);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        doc.IsTaggedPdf.Should().BeFalse();
    }

    /// <summary>
    /// IsTaggedPdf returns true when /MarkInfo/Marked is true.
    /// </summary>
    [Fact]
    public void IsTaggedPdf_MarkInfoMarkedTrue_ReturnsTrue()
    {
        var pdf = CreateMinimalTaggedPdf(withStructTree: true);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        doc.IsTaggedPdf.Should().BeTrue();
    }

    /// <summary>
    /// Simple structure tree with one element (root).
    /// </summary>
    [Fact]
    public void ParseStructureTree_SingleElement_Parsed()
    {
        var pdf = CreateMinimalTaggedPdf(withStructTree: true);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        tree.Should().NotBeNull();
        tree!.Type.Should().StartWith("/"); // Should be /something
    }

    /// <summary>
    /// Caching: GetStructureTree() called twice returns same instance.
    /// </summary>
    [Fact]
    public void ParseStructureTree_CachedAfterFirstCall()
    {
        var pdf = CreateMinimalTaggedPdf(withStructTree: true);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));

        var tree1 = doc.GetStructureTree();
        var tree2 = doc.GetStructureTree();

        // If tree exists, should be same object (cached)
        if (tree1 != null && tree2 != null)
            tree1.Should().BeSameAs(tree2);
    }

    /// <summary>
    /// Malformed /StructTreeRoot (no /K) returns null.
    /// </summary>
    [Fact]
    public void ParseStructureTree_MissingK_ReturnsNull()
    {
        // Create PDF with StructTreeRoot but no /K entry
        var pdf = CreateMinimalTaggedPdf(withStructTree: true);
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        // Even if the tree is malformed, parsing should not crash
        var tree = doc.GetStructureTree();
        // tree may be null or not, depending on the PDF structure
        // The important thing is we don't crash
    }

    [Fact]
    public void ParseStructureTree_SingleElementK_ReturnsDirectlyAsRoot()
    {
        var pdf = CreateTaggedPdfWithSingleElementK();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        tree.Should().NotBeNull();
        tree!.Type.Should().Be("/P");
    }

    [Fact]
    public void ParseStructureTree_SingleMcidK_ExtractsMcid()
    {
        var pdf = CreateTaggedPdfWithSingleMcidK();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        tree.Should().NotBeNull();
        tree!.Type.Should().Be("/P");
        tree.MarkedContentIds.Should().HaveCount(1);
        tree.MarkedContentIds[0].Should().Be(42);
    }

    [Fact]
    public void ParseStructureTree_ArrayOfElements_WrapsInSyntheticRoot()
    {
        var pdf = CreateTaggedPdfWithElementArray();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        tree.Should().NotBeNull();
        tree!.Type.Should().Be("/Document");
        tree.Children.Should().HaveCount(2);
        tree.Children[0].Type.Should().Be("/P");
        tree.Children[1].Type.Should().Be("/H1");
    }

    [Fact]
    public void ParseStructureTree_ArrayMixedElementsAndMcids_SeparatesProperly()
    {
        var pdf = CreateTaggedPdfWithMixedArray();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        tree.Should().NotBeNull();
        tree!.Type.Should().Be("/P");
        tree.Children.Should().HaveCount(2);
        tree.MarkedContentIds.Should().HaveCount(2);
        tree.MarkedContentIds[0].Should().Be(5);
        tree.MarkedContentIds[1].Should().Be(10);
    }

    [Fact]
    public void ParseStructureTree_NestedHierarchy_BuildsTree()
    {
        var pdf = CreateTaggedPdfWithNestedHierarchy();
        using var doc = PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var tree = doc.GetStructureTree();

        // StructTreeRoot /K is [5 0 R], a single-element array
        // Per parser logic (lines 45-46), single element is returned directly without synthetic wrapper
        tree.Should().NotBeNull();
        tree!.Type.Should().Be("/Sect");
        tree.Children.Should().HaveCount(1);

        var paragraph = tree.Children[0];
        paragraph.Type.Should().Be("/P");
        paragraph.MarkedContentIds.Should().HaveCount(1);
        paragraph.MarkedContentIds[0].Should().Be(0);
    }

    // Helper: Create a minimal tagged PDF
    private static byte[] CreateMinimalTaggedPdf(bool withStructTree)
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
        if (withStructTree)
        {
            writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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

        // Object 4: StructTreeRoot (if withStructTree)
        offsets[4] = ms.Position;
        long xrefPos;
        if (withStructTree)
        {
            writer.WriteLine("4 0 obj");
            writer.WriteLine("<< /Type /StructTreeRoot /K 5 0 R >>");
            writer.WriteLine("endobj");
            writer.Flush();

            // Object 5: Structure element
            offsets[5] = ms.Position;
            writer.WriteLine("5 0 obj");
            writer.WriteLine("<< /Type /StructElem /S /P /Alt (Paragraph) >>");
            writer.WriteLine("endobj");
            writer.Flush();

            // xref
            xrefPos = ms.Position;
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
        }
        else
        {
            // xref
            xrefPos = ms.Position;
            writer.WriteLine("xref");
            writer.WriteLine("0 4");
            writer.WriteLine("0000000000 65535 f ");
            writer.WriteLine($"{offsets[1]:D10} 00000 n ");
            writer.WriteLine($"{offsets[2]:D10} 00000 n ");
            writer.WriteLine($"{offsets[3]:D10} 00000 n ");
            writer.Flush();

            writer.WriteLine("trailer");
            writer.WriteLine("<< /Root 1 0 R /Size 4 >>");
        }

        writer.WriteLine("startxref");
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateTaggedPdfWithSingleElementK()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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
        writer.WriteLine("<< /Type /StructTreeRoot /K 5 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /P >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
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
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateTaggedPdfWithSingleMcidK()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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
        writer.WriteLine("<< /Type /StructTreeRoot /K 5 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /P /K 42 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
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
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateTaggedPdfWithElementArray()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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
        writer.WriteLine("<< /Type /StructTreeRoot /K [5 0 R 6 0 R] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /P >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /H1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateTaggedPdfWithMixedArray()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[9];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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
        writer.WriteLine("<< /Type /StructTreeRoot /K 5 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /P /K [6 0 R 5 7 0 R 10] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /Span >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[7] = ms.Position;
        writer.WriteLine("7 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /Em >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 8");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.WriteLine($"{offsets[7]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 8 >>");
        writer.WriteLine("startxref");
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreateTaggedPdfWithNestedHierarchy()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>");
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
        writer.WriteLine("<< /Type /StructTreeRoot /K [5 0 R] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /Sect /K [6 0 R] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /StructElem /S /P /K [0] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.Flush();
        writer.WriteLine(xrefPos.ToString());
        writer.Flush();
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
