using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for <see cref="PdfLink"/> model and <see cref="PdfLinkParser"/>
/// covering internal links, URI links, named destinations, and edge cases.
/// </summary>
public class PdfLinkTests
{
    // ─── PdfLink model tests ────────────────────────────────────────────────

    [Fact]
    public void PdfLink_Constructor_CapturesRectAndPage()
    {
        var rect = new PdfRectangle(10, 20, 100, 120);
        var page = 5;

        var link = new PdfLink(rect, page);

        link.Rect.Should().Be(rect);
        link.DestinationPage.Should().Be(page);
    }

    [Fact]
    public void PdfLink_RectProperties_Accessible()
    {
        var rect = new PdfRectangle(10, 20, 100, 120);
        var link = new PdfLink(rect, 1);

        link.Rect.Left.Should().Be(10);
        link.Rect.Bottom.Should().Be(20);
        link.Rect.Right.Should().Be(100);
        link.Rect.Top.Should().Be(120);
    }

    // ─── PDF builders ───────────────────────────────────────────────────────

    /// <summary>Build a minimal two-page PDF with /Link annotations.</summary>
    private static byte[] MakePdfWithLinks(string page1AnnotsArray, string page2AnnotsArray)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");

        long obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {page1AnnotsArray} >>");
        sb.AppendLine("endobj");

        long obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {page2AnnotsArray} >>");
        sb.AppendLine("endobj");

        long obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ─── Test: basic parsing ────────────────────────────────────────────────

    [Fact]
    public void Parse_PageWithNoAnnots_ReturnsEmpty()
    {
        var pdf = MakePdfWithLinks("[]", "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PageWithNullAnnots_ReturnsEmpty()
    {
        var pdf = MakePdfWithLinks("null", "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AnnotsNotArray_ReturnsEmpty()
    {
        var pdf = MakePdfWithLinks("<< /Name (NotArray) >>", "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    // ─── Test: link annotation filtering ────────────────────────────────────

    [Fact]
    public void Parse_OnlyLinkSubtype_Extracted()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [0 0 100 100] >>
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
            << /Type /Annot /Subtype /Highlight /Rect [60 60 150 80] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(1);
        result[0].Rect.Left.Should().Be(10);
        result[0].Rect.Right.Should().Be(50);
    }

    [Fact]
    public void Parse_NonLinkAnnotations_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [0 0 100 100] >>
            << /Type /Annot /Subtype /Highlight /Rect [60 60 150 80] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_LinkWithoutSubtype_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    // ─── Test: link rectangle ───────────────────────────────────────────────

    [Fact]
    public void Parse_LinkRectCapturing_PreciseCoordinates()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [72.5 100.25 300.75 150.125] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(1);
        result[0].Rect.Left.Should().BeApproximately(72.5, 0.01);
        result[0].Rect.Bottom.Should().BeApproximately(100.25, 0.01);
        result[0].Rect.Right.Should().BeApproximately(300.75, 0.01);
        result[0].Rect.Top.Should().BeApproximately(150.125, 0.01);
    }

    [Fact]
    public void Parse_LinkRectMissing_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_LinkRectFewerThan4Elements_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 20] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    // ─── Test: internal GoTo links ──────────────────────────────────────────

    [Fact]
    public void Parse_LinkWithGoToAction_ResolvesPage()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /A << /S /GoTo /D [4 0 R /XYZ 0 0 0] >> >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(1);
        result[0].DestinationPage.Should().Be(2);
    }

    [Fact]
    public void Parse_LinkWithDestArray_ResolvesPage()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(1);
        result[0].DestinationPage.Should().Be(2);
    }

    [Fact]
    public void Parse_LinkPageNotInMap_NoDestinationPage()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int>(); // Empty map

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        // No destination page resolved, so link is skipped
        result.Should().BeEmpty();
    }

    // ─── Test: URI / dangerous-action links (#625) ──────────────────────────

    [Fact]
    public void Parse_LinkWithUriAction_ResolvesAsExternalUri()
    {
        // #625: URI actions used to be dropped entirely; now they resolve to
        // an ExternalUri-kind link so the UI can offer to open them.
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /A << /S /URI /URI (https://example.com) >> >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().ContainSingle();
        result[0].Kind.Should().Be(PdfLinkKind.ExternalUri);
        result[0].Uri.Should().Be("https://example.com");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Parse_LinkWithUriAction_NonAllowlistedScheme_ResolvesAsDangerous(string uri)
    {
        // #625: file:, javascript:, and any other non-http(s)/mailto scheme
        // must not be treated as a normal, confirmable external link.
        var annotsDef = $@"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /A << /S /URI /URI ({uri}) >> >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().ContainSingle();
        result[0].Kind.Should().Be(PdfLinkKind.Dangerous);
        result[0].Uri.Should().BeNull();
    }

    [Theory]
    [InlineData("Launch")]
    [InlineData("GoToE")]
    [InlineData("GoToR")]
    public void Parse_LinkWithDangerousAction_ResolvesAsDangerous(string actionType)
    {
        // #625: /Launch (external app/file), /GoToE (embedded file), and
        // /GoToR (remote file) actions used to be silently skipped — now
        // they resolve to a Dangerous-kind link so the UI can refuse them
        // with a clear message instead of doing nothing.
        var annotsDef = $@"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /A << /S /{actionType} >> >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().ContainSingle();
        result[0].Kind.Should().Be(PdfLinkKind.Dangerous);
        result[0].DangerousActionType.Should().Be(actionType);
    }

    [Fact]
    public void Parse_LinkWithUnknownAction_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /A << /S /SubmitForm >> >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        // Action types this parser doesn't have opinions about (neither a
        // navigable link nor a recognized dangerous type) are still skipped.
        result.Should().BeEmpty();
    }

    // ─── Test: named destinations ───────────────────────────────────────────

    [Fact]
    public void Parse_LinkWithNamedDest_ResolvesPage()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest /MyDestination >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };
        var namedDests = new Dictionary<string, PdfObject>
        {
            { "MyDestination", new PdfArray { (PdfObject)new PdfReference(4, 0), (PdfObject)new PdfName("XYZ"), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0) } }
        };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, namedDests);

        result.Should().HaveCount(1);
        result[0].DestinationPage.Should().Be(2);
    }

    [Fact]
    public void Parse_LinkWithStringNamedDest_ResolvesPage()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest (StringDest) >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };
        var namedDests = new Dictionary<string, PdfObject>
        {
            { "StringDest", new PdfArray { (PdfObject)new PdfReference(4, 0), (PdfObject)new PdfName("XYZ"), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0) } }
        };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, namedDests);

        result.Should().HaveCount(1);
        result[0].DestinationPage.Should().Be(2);
    }

    [Fact]
    public void Parse_LinkWithUnresolvedNamedDest_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest /UnknownDest >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };
        var namedDests = new Dictionary<string, PdfObject>
        {
            { "OtherDest", new PdfArray { (PdfObject)new PdfReference(4, 0), (PdfObject)new PdfName("XYZ"), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0), (PdfObject)new PdfInteger(0) } }
        };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, namedDests);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_LinkNoNamedDestsMap_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest /MyDest >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        // Without namedDests map, named destination cannot be resolved
        result.Should().BeEmpty();
    }

    // ─── Test: multiple links ───────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleLinks_AllExtracted()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
            << /Type /Annot /Subtype /Link /Rect [60 60 150 80] /Dest [4 0 R /XYZ 0 0 0] >>
            << /Type /Annot /Subtype /Link /Rect [200 200 250 250] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(3);
        result[0].Rect.Left.Should().Be(10);
        result[1].Rect.Left.Should().Be(60);
        result[2].Rect.Left.Should().Be(200);
    }

    [Fact]
    public void Parse_MixedLinksAndOtherAnnots_OnlyLinksExtracted()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [0 0 100 100] >>
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [4 0 R /XYZ 0 0 0] >>
            << /Type /Annot /Subtype /Highlight /Rect [60 60 150 80] >>
            << /Type /Annot /Subtype /Link /Rect [200 200 250 250] /Dest [4 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(2);
    }

    // ─── Test: invalid destination structures ───────────────────────────────

    [Fact]
    public void Parse_LinkDestNotArray_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest (NotArray) >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_LinkDestArrayEmpty_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_LinkDestArrayFirstElementNotRef_Skipped()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [123 /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks(annotsDef, "[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().BeEmpty();
    }

    // ─── Test: links in later pages ────────────────────────────────────────

    [Fact]
    public void Parse_LinksOnSecondPage_Extracted()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest [3 0 R /XYZ 0 0 0] >>
        ]";
        var pdf = MakePdfWithLinks("[]", annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(2).Dictionary;
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };

        var result = PdfLinkParser.Parse(doc, pageDict, pageRefMap, null);

        result.Should().HaveCount(1);
        result[0].DestinationPage.Should().Be(1);
    }
}
