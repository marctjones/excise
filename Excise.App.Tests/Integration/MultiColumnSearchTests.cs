using System.IO;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// #924 — DEFAULT and REGEX search must find text that is visible on the page.
///
/// `PdfSearchService` used to read <see cref="PdfPage.Text"/> as its haystack
/// for substring and regex search, and `GetWords()` only for whole-words-only
/// mode. `page.Text` drops content on multi-column pages (#899), so on a dense
/// government instruction booklet the user got:
///
///     default (substring)  -> NOT FOUND
///     regex                -> NOT FOUND
///     whole words only     -> found
///
/// i.e. the workaround for search being broken was to turn on a setting that
/// sounds like it would match FEWER things.
///
/// WHY THESE TESTS NEED THE CORPUS
///
/// The defect only appears where text assembly actually loses content, which
/// takes a real multi-column layout. A synthetic PDF from TestPdfGenerator
/// emits text with explicit spaces and a single column, so `page.Text` and the
/// word list agree and a synthetic test passes both before AND after the fix —
/// it cannot tell them apart. So these skip when the fixture is absent rather
/// than pretending to cover it.
///
/// Measured on page 117 of irs-1040-instructions.pdf before the fix:
/// letters=3928, page.Text=2885 — 1043 characters lost.
/// </summary>
public class MultiColumnSearchTests
{
    /// <summary>
    /// Phrases known to be present in the letter stream and absent from
    /// `page.Text` on this page. The last one spans word boundaries, which is
    /// the case a single-word test cannot distinguish.
    /// </summary>
    public static TheoryData<string> MissingPhrases() => new()
    {
        "insurance company",
        "Form 1095-A",
        "net premium tax credit",
    };

    [Theory]
    [MemberData(nameof(MissingPhrases))]
    public void DefaultSearch_FindsTextThatPageTextDropped(string phrase)
    {
        var path = FindFixture();
        Assert.SkipWhen(path == null,
            "irs-1040-instructions.pdf not present (gitignored corpus); run scripts/download-smoke-corpus.sh");

        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(117);
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var matches = service.SearchInPage(page, phrase, pageIndex: 116);

        matches.Should().NotBeEmpty(
            $"'{phrase}' is visible on page 117 and present in the page's words, so the " +
            "DEFAULT search mode must find it. Reading page.Text instead loses 1043 of " +
            "3928 characters on this page (#899), which is what made find-in-document " +
            "silently fail on multi-column documents (#924)");
    }

    /// <summary>
    /// The control that gives the test above its meaning: whole-words-only mode
    /// always read the complete word list, so it found these all along. If this
    /// ever fails, the fix broke the path that was already correct rather than
    /// repairing the one that was not.
    /// </summary>
    [Theory]
    [MemberData(nameof(MissingPhrases))]
    public void WholeWordsSearch_StillFindsThem(string phrase)
    {
        var path = FindFixture();
        Assert.SkipWhen(path == null, "corpus fixture not present");

        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(117);
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        // Whole-words mode matches a single token, so use the first word of the
        // phrase — the point here is the mode, not the phrase.
        var firstWord = phrase.Split(' ')[0];
        service.SearchInPage(page, firstWord, wholeWordsOnly: true, pageIndex: 116)
            .Should().NotBeEmpty(
                "whole-words mode reads GetWords() and was the one mode that worked; " +
                "the fix must not regress it");
    }

    /// <summary>
    /// A match must still carry a usable highlight rectangle. Building the
    /// haystack from words makes the spans exact rather than re-derived, so a
    /// zero-size or negative box here means the span arithmetic is wrong.
    /// </summary>
    [Fact]
    public void AMatchCarriesAPositiveHighlightRectangle()
    {
        var path = FindFixture();
        Assert.SkipWhen(path == null, "corpus fixture not present");

        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(117);
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var match = service.SearchInPage(page, "net premium tax credit", pageIndex: 116)
            .FirstOrDefault();

        match.Should().NotBeNull();
        match!.Width.Should().BeGreaterThan(0,
            "a phrase spanning several words must span their bounding boxes");
        match.Height.Should().BeGreaterThan(0);
    }

    private static string? FindFixture()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "smoke",
                "irs-1040-instructions.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
