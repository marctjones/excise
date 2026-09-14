using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// <see cref="PdfDocument.ResolveStructElementText"/> reads a per-page
/// MCID→text map instead of scanning the page's letters (#1485).
/// </summary>
/// <remarks>
/// <para>The accessibility structure walk resolves every tagged element through
/// this. Its per-element letter scan was only cheap while every page kept its
/// letters for the document's lifetime; with letters bounded, a repeat walk of
/// irs-1040-instructions.pdf cost ~165 ms against ~33 ms. The map replaces the
/// scan, so these tests pin what that must not change: the text is what the
/// letter scan produced, a repeat walk walks nothing, a rewrite re-walks only
/// its own page, and redacted text does not come back through a stale map.</para>
/// <para>Test 1 is an old-vs-new behaviour pin: the reference is the pre-#1485
/// letter scan, kept verbatim as
/// <see cref="PdfDocument.ResolveStructElementTextFromLetters"/>. The
/// independent-oracle checks of MCID text are <see cref="StructElementMcidTextTests"/>.</para>
/// </remarks>
public class StructElementMcidTextMapTests
{
    private const int PageCount = PdfDocument.PageLetterCacheCapacity + 3;

    [Fact]
    public void ResolveStructElementText_OverEveryTaggedFixture_EqualsTheLetterScan()
    {
        var comparedElements = 0;
        var nonEmpty = 0;
        var taggedFixtures = new List<string>();

        foreach (var pdf in TaggedFixtureCandidates())
        {
            PdfDocument doc;
            try { doc = PdfDocument.Open(File.ReadAllBytes(pdf)); }
            catch (Exception) { continue; } // malformed-by-design regression fixtures

            using (doc)
            {
                PdfStructElement? root;
                try { root = doc.GetStructureTree(); }
                catch (Exception) { continue; }
                if (root == null)
                    continue;

                var name = Path.GetFileName(pdf);
                taggedFixtures.Add(name);
                // A separate instance for the reference, so neither path warms
                // the other's caches.
                using var reference = PdfDocument.Open(File.ReadAllBytes(pdf));
                var referenceRoot = reference.GetStructureTree()!;

                var elements = Descendants(root).ToList();
                var referenceElements = Descendants(referenceRoot).ToList();
                referenceElements.Should().HaveCount(elements.Count, name);

                for (int i = 0; i < elements.Count; i++)
                {
                    foreach (int? inherited in new int?[] { null, 1 })
                    {
                        var expected = reference.ResolveStructElementTextFromLetters(referenceElements[i], inherited);
                        var actual = doc.ResolveStructElementText(elements[i], inherited);
                        actual.Should().Be(expected, $"{name} element {i} ({elements[i].Type}), inherited page {inherited}");
                        comparedElements++;
                        if (expected.Length > 0) nonEmpty++;
                    }
                }
            }
        }

        taggedFixtures.Should().Contain("acc-global-compensation-report.pdf",
            "the checked-in tagged fixture must be part of the comparison");
        nonEmpty.Should().BeGreaterThan(0, "a comparison of empty strings compares nothing");
    }

    [Fact]
    public void ResolveStructElementText_RepeatWalk_WalksNoPageAndKeepsNoLetters()
    {
        using var doc = PdfDocument.Open(TaggedDocument(p => $"Alpha{p:D2}", p => $"Body text {p}"));
        var elements = Descendants(doc.GetStructureTree()).ToList();

        var first = Walk(doc, elements);
        var walks = Walks(doc);
        walks.Should().OnlyContain(n => n == 1, "the first walk reads each tagged page exactly once");
        Enumerable.Range(1, doc.PageCount).Should().NotContain(p => doc.GetPage(p).HasCachedLetters,
            "resolving MCID text must not keep the page's letters");

        var second = Walk(doc, elements);

        Walks(doc).Should().Equal(walks, "a repeat walk must not walk any page again");
        second.Should().Equal(first);
        first.Should().Contain(t => t.Contains("Alpha03"));
    }

    [Fact]
    public void ResolveStructElementText_AfterRewritingOnePage_WalksOnlyThatPage_AndServesItsNewText()
    {
        using var doc = PdfDocument.Open(TaggedDocument(p => $"Alpha{p:D2}", p => $"Body text {p}"));
        var elements = Descendants(doc.GetStructureTree()).ToList();
        Walk(doc, elements).Should().Contain(t => t.Contains("Alpha03"));

        var page = doc.GetPage(3);
        var content = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        content.Should().Contain("Alpha03", "the fixture's heading must be a literal string to rewrite");
        page.SetContentStreamBytes(Encoding.Latin1.GetBytes(content.Replace("Alpha03", "Omega03")));
        var walks = Walks(doc);

        var after = Walk(doc, elements);

        after.Should().Contain(t => t.Contains("Omega03"), "the rewritten page's MCID text must be re-read");
        after.Should().NotContain(t => t.Contains("Alpha03"), "the old text must not be served from a stale map");
        var afterWalks = Walks(doc);
        afterWalks[2].Should().Be(walks[2] + 1, "the rewritten page is walked once more");
        afterWalks.Where((_, i) => i != 2).Should().Equal(walks.Where((_, i) => i != 2),
            "every other page keeps its map");
    }

    [Fact]
    public void ResolveStructElementText_AfterRedaction_DoesNotReturnTheRemovedTerm()
    {
        using var doc = PdfDocument.Open(TaggedDocument(p => $"Alpha{p:D2}",
            p => p is 2 or 5 ? "Keep REDACTEDWORD tail" : $"Body text {p}"));
        var elements = Descendants(doc.GetStructureTree()).ToList();
        Walk(doc, elements).Count(t => t.Contains("REDACTEDWORD")).Should().BeGreaterThanOrEqualTo(2);

        doc.RedactText("REDACTEDWORD", drawBlackRect: false).VerifiedRemovals.Should().Be(2);

        var after = Walk(doc, elements);
        after.Should().NotContain(t => t.Contains("REDACTEDWORD"),
            "the structure walk must read the redacted bytes, not a map built before the redaction");
        after.Should().Contain(t => t.Contains("Keep") && t.Contains("tail"),
            "the rest of the redacted element's text is still read");
    }

    private static List<string> Walk(PdfDocument doc, IReadOnlyList<PdfStructElement> elements) =>
        elements.Select(e => doc.ResolveStructElementText(e)).ToList();

    private static List<int> Walks(PdfDocument doc) =>
        Enumerable.Range(1, doc.PageCount).Select(p => doc.GetPage(p).TextWalkCount).ToList();

    private static byte[] TaggedDocument(Func<int, string> heading, Func<int, string> paragraph)
    {
        var builder = PdfDocumentBuilder.Create().Tagged();
        for (int p = 1; p <= PageCount; p++)
        {
            if (p > 1)
                builder.PageBreak();
            builder.Heading(heading(p), 1).Paragraph(paragraph(p));
        }
        return builder.SaveToBytes();
    }

    private static IEnumerable<PdfStructElement> Descendants(PdfStructElement? root, int depth = 0)
    {
        if (root == null || depth > 256)
            yield break;

        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Descendants(child, depth + 1))
            yield return descendant;
    }

    /// <summary>
    /// Every checked-in PDF under test-pdfs (sample-pdfs, pdf20,
    /// generated-regressions) plus the downloaded smoke corpus when present.
    /// Untagged files are skipped by the caller.
    /// </summary>
    private static IEnumerable<string> TaggedFixtureCandidates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "test-pdfs", "sample-pdfs")))
            dir = dir.Parent;
        if (dir == null)
            return [];

        return new[] { "sample-pdfs", "pdf20", "generated-regressions", "smoke" }
            .Select(sub => Path.Combine(dir.FullName, "test-pdfs", sub))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.pdf", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.Ordinal);
    }
}
