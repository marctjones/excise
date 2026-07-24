using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must find words split by invisible/optional separators (#726):
/// soft hyphen U+00AD (justified text), zero-width space/non-joiner/joiner
/// U+200B–U+200D, zero-width no-break space U+FEFF, and text using
/// non-breaking space U+00A0 where the user types a plain space. The
/// fixture's /ToUnicode maps character codes to the split spelling so
/// extraction yields the invisible code point; only separator folding in
/// MatchingNormalization can bridge it to the typed needle. Sibling of
/// CanonicalAccentSearchTests (#724) / HarakatNiqqudSearchTests (#725).
/// </summary>
public class InvisibleSeparatorSearchTests
{
    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    private static int[] SplitSecret(int separator) =>
        new[] { (int)'s', (int)'e', separator, (int)'c', (int)'r', (int)'e', (int)'t' };

    [Theory]
    [InlineData(0x00AD)]
    [InlineData(0x200B)]
    [InlineData(0x200C)]
    [InlineData(0x200D)]
    [InlineData(0xFEFF)]
    public void SearchInPage_PlainNeedle_FindsWordSplitByInvisibleChar(int separator)
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(SplitSecret(separator)));
        var page = doc.GetPage(1);

        // Anti-vacuity: the page carries the split spelling, not the plain one.
        page.Text.Should().Contain(char.ConvertFromUtf32(separator));
        page.Text.Should().NotContain("secret");

        var matches = NewService().SearchInPage(page, "secret", pageIndex: 0);

        matches.Should().NotBeEmpty(
            $"a plain needle must find a word split by U+{separator:X4}");
        matches[0].Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
    }

    [Fact]
    public void SearchInPage_PlainSpaceNeedle_FindsNonBreakingSpaceText()
    {
        var scalars = "top".Select(c => (int)c)
            .Append(0x00A0)
            .Concat("secret".Select(c => (int)c)).ToArray();
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        page.Text.Should().Contain("\u00A0");

        var matches = NewService().SearchInPage(page, "top secret", pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a needle typed with a plain space must find text stored with U+00A0");
    }

    [Fact]
    public void SearchInPage_WholeWord_PlainNeedle_FindsSoftHyphenatedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(SplitSecret(0x00AD)));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "secret", wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must fold the separator out of both the word and the needle");
    }

    [Fact]
    public void SearchInPage_RealHyphen_IsNotFolded()
    {
        // Scope guard: "secret" must not find "se-cret" — a real hyphen is
        // visible content.
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(SplitSecret('-')));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "secret", pageIndex: 0);

        matches.Should().BeEmpty(
            "only the soft hyphen is an optional separator; U+002D keeps its identity");
    }
}
