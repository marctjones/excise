using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1632 step 4 and steps 10-11, on real documents: the number of hits Find shows for a term
/// is the number of occurrences a term redaction reports removing, and the saved copy, reopened,
/// has no hit in Find and none for an independent reader (mutool).
/// </summary>
/// <remarks>
/// The fixture-by-construction version of this rule is
/// <see cref="WholeWordSearchRedactionParityTests"/>; this one runs the real corpus, where
/// ligatures, kerned TJ arrays and wrapped lines occur. A hyphen-wrapped occurrence is split
/// across two lines: Find does not show it and redaction reports it as not removed, so the
/// counts agree and the wrapped case is asserted through the run's own flag, not skipped.
/// </remarks>
public sealed class CorpusFindVersusRedactTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-find-vs-redact-{Guid.NewGuid():N}");

    public CorpusFindVersusRedactTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("irs-w4.pdf", "Withholding", false)]
    [InlineData("irs-w4.pdf", "employer", false)]
    [InlineData("irs-w4.pdf", "Form W-4", false)]
    [InlineData("irs-w4.pdf", "(a)", false)]
    [InlineData("irs-w4.pdf", "$", false)]
    [InlineData("irs-w9.pdf", "Taxpayer", false)]
    [InlineData("irs-w9.pdf", "Form W-9", false)]
    [InlineData("irs-w9.pdf", "TIN", true)]
    [InlineData("irs-w9.pdf", "an", true)]
    [InlineData("scotus-trump-v-anderson.pdf", "Trump", false)]
    [InlineData("scotus-trump-v-anderson.pdf", "Section 3", false)]
    [InlineData("scotus-trump-v-anderson.pdf", "(1)", false)]
    [InlineData("state-ds11-passport.pdf", "Passport", false)]
    public void FindShowsWhatRedactionRemoves_AndTheSavedCopyIsClean(string file, string term, bool caseSensitive)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var input = TestRepoLayout.FindFile("test-pdfs", "smoke", file);
        Assert.SkipWhen(input == null, TestRepoLayout.AbsenceReason(file, "test-pdfs/smoke/" + file));

        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        var findHits = service.Search(input!, term, caseSensitive).Count;
        findHits.Should().BeGreaterThan(0, "the control: Find sees the term before the redaction");

        var output = Path.Combine(_tempDir, "out.pdf");
        var result = TermRedactionRunner.Execute(new TermRedactionRequest(
            input!, output, term, RedactionOptions.Default with { CaseSensitive = caseSensitive }));

        result.Count.Should().Be(findHits,
            "Find and redaction are the same rule: what Find shows is what redaction reports removing");

        // Reopened: Find cannot locate it (this reader is excise's own), and neither can mutool
        // (independent; CLAUDE.md rule 4). A wrapped occurrence is excluded by its own flag.
        service.Search(output, term, caseSensitive).Should().BeEmpty("Find on the saved copy");
        if (!result.HasUnremovedWrappedOccurrence)
        {
            using var saved = PdfDocument.Open(output);
            for (var page = 1; page <= saved.PageCount; page++)
            {
                var text = MutoolTextExtractor.ExtractPage(output, page);
                text.Should().NotBeNull($"mutool reads page {page} of the saved copy");
                text!.Contains(term, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    .Should().BeFalse($"mutool still reads '{term}' on page {page} of the saved copy");
            }
        }
    }
}
