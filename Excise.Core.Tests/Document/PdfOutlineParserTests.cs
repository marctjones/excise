using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;
namespace Excise.Core.Tests.Document;

/// <summary>
/// Outline + link parsing against real-world PDFs. The Pragmatic title
/// has both a multi-level table of contents and back-of-book index
/// entries that are clickable — exactly the workflow this code is for.
/// </summary>
public class PdfOutlineParserTests
{
    private readonly ITestOutputHelper _out;
    public PdfOutlineParserTests(ITestOutputHelper o) { _out = o; }

    private const string PragmaticBook =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    [Fact]
    public void BuildPageRefMap_CyclicPagesTree_DoesNotRecurseForever()
    {
        var pdf = Encoding.ASCII.GetBytes("""
            %PDF-1.7
            1 0 obj
            << /Type /Catalog /Pages 2 0 R >>
            endobj
            2 0 obj
            << /Type /Pages /Kids [2 0 R] /Count 1 >>
            endobj
            xref
            0 3
            0000000000 65535 f
            0000000009 00000 n
            0000000058 00000 n
            trailer
            << /Size 3 /Root 1 0 R >>
            startxref
            115
            %%EOF
            """);

        using var doc = PdfDocument.Open(pdf);

        PdfOutlineParser.BuildPageRefMap(doc).Should().BeEmpty(
            "malformed /Pages cycles should be ignored instead of overflowing the stack");
    }

    [Fact]
    public void PragmaticBook_OutlineHasMultiLevelStructure()
    {
        if (!File.Exists(PragmaticBook)) return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(PragmaticBook));
        var outline = PdfOutlineParser.Parse(doc);

        outline.Should().NotBeEmpty(
            "the book ships with a table of contents in /Outlines");

        // Top-level should include the major sections like "Chapter 1" or
        // similar. Just check titles are non-empty and at least one node
        // has children (multi-level structure).
        foreach (var node in outline)
            node.Title.Should().NotBeNullOrWhiteSpace();

        outline.Any(n => n.Children.Count > 0).Should().BeTrue(
            "outline should have at least one node with sub-entries");

        _out.WriteLine($"top-level nodes: {outline.Count}");
        foreach (var n in outline.Take(8))
            _out.WriteLine($"  '{n.Title}' → page {n.PageNumber} ({n.Children.Count} children)");
    }

    [Fact]
    public void PragmaticBook_OutlineDestinations_ResolveToValidPages()
    {
        if (!File.Exists(PragmaticBook)) return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(PragmaticBook));
        var outline = PdfOutlineParser.Parse(doc);

        // Most ToC entries resolve to a page number in [1, PageCount].
        int resolved = 0, total = 0;
        Visit(outline, n =>
        {
            total++;
            if (n.PageNumber.HasValue) resolved++;
            if (n.PageNumber.HasValue)
                n.PageNumber.Value.Should().BeInRange(1, doc.PageCount,
                    $"outline node '{n.Title}' has out-of-range page number {n.PageNumber}");
        });

        _out.WriteLine($"resolved {resolved}/{total} outline destinations");
        resolved.Should().BeGreaterThanOrEqualTo(total / 2,
            "at least half of outline entries should resolve to real pages");
    }

    private static void Visit(System.Collections.Generic.IReadOnlyList<PdfOutlineItem> nodes,
        System.Action<PdfOutlineItem> visit)
    {
        foreach (var n in nodes)
        {
            visit(n);
            Visit(n.Children, visit);
        }
    }

    [Fact]
    public void PragmaticBook_PageLinks_ResolveToValidPages()
    {
        if (!File.Exists(PragmaticBook)) return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(PragmaticBook));

        // Pull links from the table-of-contents pages (early in the doc,
        // typically pages 7-15ish for a 455-page book) — those are the
        // ones a user clicks on.
        int totalLinks = 0;
        for (int p = 1; p <= 30 && p <= doc.PageCount; p++)
        {
            var links = doc.GetPage(p).GetLinks();
            foreach (var link in links)
            {
                totalLinks++;
                link.DestinationPage.Should().BeInRange(1, doc.PageCount,
                    $"link on page {p} points to invalid page {link.DestinationPage}");
                link.Rect.Width.Should().BeGreaterThan(0);
                link.Rect.Height.Should().BeGreaterThan(0);
            }
        }

        _out.WriteLine($"resolved {totalLinks} link annotations across pages 1-30");
        totalLinks.Should().BeGreaterThan(0,
            "the book's TOC pages should have clickable links to chapters");
    }
}
