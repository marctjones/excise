using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the accessibility MCID→letter bridge (#776):
/// <see cref="PdfDocument.ResolveStructElementText"/> gathers a tagged element's
/// real body glyphs by matching its /MCID marked-content references against the
/// page's extracted <see cref="Text.Letter"/>s, and each Letter carries the
/// /MCID of the marked-content span it was drawn inside.
///
/// <para>
/// The fixtures are authored with <see cref="PdfDocumentBuilder"/>'s tagging
/// path, which emits <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC ... EMC</c> content and
/// a structure tree of <c>/MCR</c> references — and crucially NO
/// <c>/ActualText</c>, so the only way to read the heading/paragraph text in
/// structure order is the MCID bridge. The assertions compare against the
/// literal strings passed into the builder, which are an oracle independent of
/// excise's own extraction (a wrong glyph decode fails the test, it does not
/// silently self-confirm).
/// </para>
/// </summary>
public class StructElementMcidTextTests
{
    private static PdfStructElement? FindByType(PdfStructElement? element, string type)
    {
        if (element == null)
            return null;
        if (element.Type == type)
            return element;
        foreach (var child in element.Children)
        {
            var found = FindByType(child, type);
            if (found != null)
                return found;
        }
        return null;
    }

    [Fact]
    public void HeadingWithoutActualText_ResolvesRealBodyTextViaMcid()
    {
        using var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
            .Tagged()
            .Heading("Quarterly Report", 1)
            .Paragraph("Revenue rose sharply this period.")
            .SaveToBytes());

        var root = doc.GetStructureTree();
        root.Should().NotBeNull();

        var heading = FindByType(root, "/H1");
        heading.Should().NotBeNull("the tagged document has an H1 heading element");
        // The heading carries no /ActualText — the whole point of the bridge.
        heading!.ActualText.Should().BeNullOrEmpty();

        doc.ResolveStructElementText(heading).Trim()
            .Should().Be("Quarterly Report",
                "the element's real glyphs are reachable via its /MCID references");

        var paragraph = FindByType(root, "/P");
        paragraph.Should().NotBeNull();
        doc.ResolveStructElementText(paragraph!).Trim()
            .Should().Be("Revenue rose sharply this period.");
    }

    [Fact]
    public void ExtractedLetters_CarryTheMcidOfTheirMarkedContentSpan()
    {
        using var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
            .Tagged()
            .Heading("Alpha", 1)
            .Paragraph("Beta")
            .SaveToBytes());

        var letters = doc.GetPage(1).Letters;

        // Every glyph in this tagged page was drawn inside an MCID span.
        letters.Where(l => !char.IsWhiteSpace(l.Value[0]))
            .Should().OnlyContain(l => l.MarkedContentId != null,
                "all body glyphs on a fully-tagged page are inside a marked-content sequence");

        // The "Alpha" glyphs share one MCID; the "Beta" glyphs share a different
        // one — proving the per-span nesting is tracked, not a single global id.
        int? AlphaMcid(char c) => letters.First(l => l.Value == c.ToString()).MarkedContentId;
        var alphaMcids = "Alpha".Select(AlphaMcid).Distinct().ToList();
        alphaMcids.Should().ContainSingle().Which.Should().NotBeNull();

        int betaMcid = letters.First(l => l.Value == "B").MarkedContentId!.Value;
        betaMcid.Should().NotBe(alphaMcids[0]!.Value,
            "distinct structure elements get distinct MCIDs");
    }

    [Fact]
    public void ResolveStructElementText_UntaggedElement_ReturnsEmpty()
    {
        using var doc = PdfDocument.Open(PdfDocumentBuilder.Create()
            .Paragraph("Plain untagged content")
            .SaveToBytes());

        // A hand-built element that references no marked content resolves to
        // empty rather than throwing.
        var orphan = new PdfStructElement("/H1");
        doc.ResolveStructElementText(orphan).Should().BeEmpty();
    }
}
