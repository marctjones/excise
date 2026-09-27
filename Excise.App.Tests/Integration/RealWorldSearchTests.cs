using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.App.Services;
using Excise.TestSupport;
using Xunit;
namespace Excise.App.Tests.Integration;

/// <summary>
/// Search regression test against the multilingual CJK fixture. The synthetic
/// <see cref="PdfSearchService"/> tests pass against PDFs created by
/// <c>TestPdfGenerator</c>, but those don't exercise the embedded-CFF subsets,
/// multi-font runs, browser-flipped Tm, and uniXXXX glyph naming that real
/// PDFs use. User-reported "search doesn't find anything" came from this gap.
/// (The six tests that ran against a 455-page book were deleted under #1768:
/// their book path was the empty string, so they passed while running nothing.)
/// </summary>
public class RealWorldSearchTests
{
    private const string CjkFixtureRelative = "test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf";

    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    // Expected counts are mutool's (`mutool draw -F txt`), not excise's.
    // Every font in the fixture is Type0 Identity-H: the Latin runs are in
    // Noto-Serif, the CJK runs in a separate Noto-Serif-CJK-JP, so each test
    // below exercises a different composite font's CID decode.

    [Fact]
    public void CjkFixture_Search_FindsLatinWord()
    {
        var cjkFixture = RequireFixture();

        var matches = NewService().Search(cjkFixture, "English");

        matches.Should().HaveCount(2, "'English' opens the first text line and ends the last");
        matches.Should().OnlyContain(m => m.PageIndex == 0);
    }

    [Theory]
    [InlineData("狐狸", 2)]           // shared by the simplified and traditional lines
    [InlineData("懒狗", 1)]           // simplified only
    [InlineData("懶狗", 1)]           // traditional only
    [InlineData("简体中文：快速", 1)] // fullwidth colon
    [InlineData("茶色の狐", 1)]
    [InlineData("갈색 여우", 1)]
    public void CjkFixture_Search_FindsCjkTerm(string term, int expected)
    {
        var cjkFixture = RequireFixture();

        var matches = NewService().Search(cjkFixture, term);

        matches.Should().HaveCount(expected, $"mutool extracts '{term}' {expected} time(s)");
        matches.Should().OnlyContain(m => m.PageIndex == 0);
    }

    private static string RequireFixture()
    {
        // Tracked, so present in every checkout: absence is a defect, but a declared one.
        var cjkFixture = TestRepoLayout.FindFile(CjkFixtureRelative);
        Assert.SkipWhen(cjkFixture == null,
            TestRepoLayout.AbsenceReason("CJK fixture", CjkFixtureRelative));
        return cjkFixture!;
    }
}
