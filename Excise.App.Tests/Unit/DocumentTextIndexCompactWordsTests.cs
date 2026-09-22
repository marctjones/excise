using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The document text index keeps compact words and no letters (#1485).
/// </summary>
/// <remarks>
/// <para>These are old-vs-new behaviour pins, not oracle claims. Search runs
/// over each word's text and bounding box; the index used to hold
/// <c>Word</c>s (and, through them and the page caches, every letter of the
/// document) and now holds <see cref="IndexedWord"/>s. Whether that changed
/// what a user gets is decided by comparing the index against a document
/// whose pages were read the ordinary way, word for word and match for match,
/// bitwise, over the extraction-parity corpus.</para>
/// <para>Corpus: <c>test-pdfs/sample-pdfs</c> is checked in and always runs;
/// <c>test-pdfs/smoke</c> is downloaded on demand and joins when present.</para>
/// </remarks>
public sealed class DocumentTextIndexCompactWordsTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public async Task BuildAsync_LeavesNoPageHoldingItsLetters()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-index-letters-{Guid.NewGuid():N}.pdf");
        _files.Add(path);
        TestPdfGenerator.CreateMultiPagePdf(path, PdfDocument.PageLetterCacheCapacity + 3);
        using var document = PdfDocument.Open(path);

        var index = new DocumentTextIndex(document, NullLogger.Instance);
        await index.BuildAsync().WaitAsync(TimeSpan.FromSeconds(30));

        index.IsReady.Should().BeTrue();
        Enumerable.Range(0, index.PageCount).Should().Contain(i => index.GetPageWords(i).Count > 0,
            "the fixture must have text, or retention cannot be observed");
        Enumerable.Range(1, document.PageCount).Should().NotContain(p => document.GetPage(p).HasCachedLetters,
            "building the index must not leave letters cached on the pages it walked");
    }

    /// <summary>
    /// #1768: <see cref="ParityCorpus"/> used to walk up to the first ancestor
    /// holding the TRACKED test-pdfs/sample-pdfs and read test-pdfs/smoke as a
    /// sibling there. In a git worktree that ancestor is the worktree root,
    /// where the gitignored smoke corpus is absent — so 13 files silently
    /// became 3, and every test below compared bitwise output against 3 files
    /// while believing it covered 13. This asserts the resolved count against
    /// an INDEPENDENT count via <see cref="TestRepoLayout"/> directly (not
    /// through <see cref="ParityCorpus"/> itself), so a regression that
    /// reintroduces a bounded or worktree-anchored walk reds even though it
    /// still returns a nonzero, plausible-looking count.
    /// </summary>
    [Fact]
    public void ParityCorpus_ResolvesEveryReachableCorpusDirectory()
    {
        var sampleDir = TestRepoLayout.FindDirectory("test-pdfs", "sample-pdfs");
        sampleDir.Should().NotBeNull("test-pdfs/sample-pdfs is checked in and must always resolve");
        var sampleCount = Directory.GetFiles(sampleDir!, "*.pdf").Length;

        var fixtures = ParityCorpus();
        fixtures.Count.Should().BeGreaterThanOrEqualTo(sampleCount,
            "the corpus must include at least the tracked sample-pdfs files");

        // smoke is gitignored and only downloaded on some machines — the
        // differential only applies where TestRepoLayout can independently
        // see it, so a box without the corpus at all is not falsely reddened.
        var smokeDir = TestRepoLayout.FindDirectory("test-pdfs", "smoke");
        if (smokeDir != null)
        {
            var smokeCount = Directory.GetFiles(smokeDir, "*.pdf").Length;
            smokeCount.Should().BeGreaterThan(0, "a resolved smoke directory with zero files would make this vacuous");
            fixtures.Count.Should().Be(sampleCount + smokeCount,
                $"ParityCorpus() must resolve both sample-pdfs ({sampleCount}) and smoke " +
                $"({smokeCount}) even when run from a worktree — got {fixtures.Count}, which " +
                "means the gitignored smoke corpus was silently dropped (#1768)");
        }
    }

    [Fact]
    public async Task Index_OverTheParityCorpus_HoldsTheWordsAndTextOfAnOrdinaryRead_Bitwise()
    {
        var fixtures = ParityCorpus();
        fixtures.Should().NotBeEmpty("test-pdfs/sample-pdfs is checked in");

        foreach (var pdf in fixtures)
        {
            var name = Path.GetFileName(pdf);
            using var indexed = PdfDocument.Open(pdf);
            using var ordinary = PdfDocument.Open(pdf);
            var index = new DocumentTextIndex(indexed, NullLogger.Instance);
            await index.BuildAsync().WaitAsync(TimeSpan.FromMinutes(1));
            index.IsReady.Should().BeTrue(name);

            for (int i = 0; i < index.PageCount; i++)
            {
                var page = ordinary.GetPage(i + 1);
                index.GetPageText(i).Should().Be(page.Text, $"{name} page {i + 1} text");

                var expected = page.GetWords();
                var actual = index.GetPageWords(i);
                actual.Count.Should().Be(expected.Count, $"{name} page {i + 1} word count");
                for (int w = 0; w < expected.Count; w++)
                {
                    actual[w].Text.Should().Be(expected[w].Text, $"{name} page {i + 1} word {w}");
                    Bits(actual[w].BoundingBox).Should().Be(Bits(expected[w].BoundingBox),
                        $"{name} page {i + 1} word {w} ('{expected[w].Text}') box");
                }
            }
        }
    }

    [Fact]
    public async Task Search_IndexVersusLiveDocument_OverTheParityCorpus_ReturnsIdenticalMatches()
    {
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        var fixtures = ParityCorpus();
        fixtures.Should().NotBeEmpty("test-pdfs/sample-pdfs is checked in");
        var compared = 0;

        foreach (var pdf in fixtures)
        {
            var name = Path.GetFileName(pdf);
            using var indexed = PdfDocument.Open(pdf);
            using var live = PdfDocument.Open(pdf);
            var index = new DocumentTextIndex(indexed, NullLogger.Instance);
            await index.BuildAsync().WaitAsync(TimeSpan.FromMinutes(1));

            // The live search also reports annotation /Contents and form /V
            // hits, which the index never held; compare pages without /Annots.
            var comparable = Enumerable.Range(0, live.PageCount)
                .Where(i => live.GetPage(i + 1).Dictionary.GetOptional("Annots") == null)
                .ToHashSet();

            var sample = SampleWord(index);
            var queries = new List<(string Label, Func<PdfSearchService, object, List<SearchMatch>> Run)>
            {
                ("substring 'the'", (s, src) => Run(s, src, "the")),
                ("case-sensitive 'The'", (s, src) => Run(s, src, "The", caseSensitive: true)),
                ($"whole word '{sample}'", (s, src) => Run(s, src, sample, wholeWords: true)),
                ("regex digits", (s, src) => Run(s, src, @"\d{2,}", regex: true)),
            };

            foreach (var (label, run) in queries)
            {
                var fromIndex = run(service, index).Where(m => comparable.Contains(m.PageIndex)).Select(Row).ToList();
                var fromLive = run(service, live).Where(m => comparable.Contains(m.PageIndex)).Select(Row).ToList();
                fromIndex.Should().Equal(fromLive, $"{name}: {label} — same words, same boxes bitwise, same order");
                compared += fromLive.Count;
            }
        }

        compared.Should().BeGreaterThan(0, "a differential over zero matches compares nothing");
    }

    private static List<SearchMatch> Run(PdfSearchService service, object source, string term,
        bool caseSensitive = false, bool wholeWords = false, bool regex = false) =>
        source switch
        {
            DocumentTextIndex index => service.Search(index, term, caseSensitive, wholeWords, regex),
            PdfDocument document => service.Search(document, term, caseSensitive, wholeWords, regex),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private static string SampleWord(DocumentTextIndex index)
    {
        for (int i = 0; i < index.PageCount; i++)
        {
            var word = index.GetPageWords(i).FirstOrDefault(w => w.Text.Length >= 4);
            if (word.Text != null)
                return word.Text;
        }
        return "the";
    }

    private static string Row(SearchMatch m) =>
        $"{m.PageIndex}|{m.MatchedText}|{BitConverter.DoubleToInt64Bits(m.X):X}|{BitConverter.DoubleToInt64Bits(m.Y):X}|" +
        $"{BitConverter.DoubleToInt64Bits(m.Width):X}|{BitConverter.DoubleToInt64Bits(m.Height):X}|{m.Context}";

    private static string Bits(PdfRectangle r) =>
        $"{BitConverter.DoubleToInt64Bits(r.Left):X}/{BitConverter.DoubleToInt64Bits(r.Bottom):X}/" +
        $"{BitConverter.DoubleToInt64Bits(r.Right):X}/{BitConverter.DoubleToInt64Bits(r.Top):X}";

    private static IReadOnlyList<string> ParityCorpus() =>
        new[] { "sample-pdfs", "smoke" }
            .Select(sub => TestRepoLayout.FindDirectory("test-pdfs", sub))
            .Where(d => d != null)
            .SelectMany(d => Directory.GetFiles(d!, "*.pdf"))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
