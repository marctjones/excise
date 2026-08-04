using System.Reflection;
using AwesomeAssertions;
using Excise.Core.Fonts;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// The CFF standard strings table (Adobe CFF spec, Appendix A) held <b>244</b>
/// entries instead of <b>391</b> and was misaligned from <b>SID 151</b> onward.
///
/// SIDs 0-150 were correct, which is exactly why it survived: plain ASCII text
/// resolved fine and only accented and small-cap glyphs broke. Five names
/// ("onesuperior", "twosuperior", "threesuperior", "minus", "multiply") each
/// appeared three times, and the whole Latin-1 accented block (SID 171-228)
/// plus every small-cap/oldstyle name (229-390) was missing.
///
/// NOT ONLY A RENDERING BUG. TextExtractor builds glyph-name to Unicode through
/// CffParser.Parse, so a wrong SID is wrong EXTRACTED TEXT. CLAUDE.md's rule —
/// "redaction completeness is bounded by extraction coverage" — makes that a
/// redaction-security defect: text excise cannot read is text excise cannot
/// redact, and it reports success anyway.
///
/// These assertions are hermetic (no corpus, no font file) because the defect
/// was in a hand-maintained data table, and a table is exactly the thing that
/// can be checked directly.
/// </summary>
public class CffStandardStringsTests
{
    private static string[] Table()
    {
        var field = typeof(CffParser).GetField(
            "StandardStrings", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("the standard-strings table must still exist");
        return (string[])field!.GetValue(null)!;
    }

    [Fact]
    public void HasExactlyTheSpecifiedNumberOfStandardStrings()
    {
        Table().Should().HaveCount(391,
            "the CFF spec defines SID 0-390; a short table makes every SID past its end " +
            "resolve to null, and SIDs above 390 must fall through to the font's own " +
            "String INDEX instead");
    }

    /// <summary>
    /// The specific corruption: a truncated table is one failure mode, a
    /// DUPLICATED run is the other, and duplicates are what shifted every
    /// subsequent SID.
    /// </summary>
    [Fact]
    public void ContainsNoDuplicateNames()
    {
        var table = Table();
        var duplicates = table
            .Select((name, sid) => (name, sid))
            .GroupBy(x => x.name)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} at SIDs {string.Join(",", g.Select(x => x.sid))}")
            .ToArray();

        duplicates.Should().BeEmpty(
            "each standard string appears exactly once; the old table repeated five of " +
            "them three times each, which is what misaligned everything from SID 151");
    }

    /// <summary>
    /// Anchors at and around the break. SID 150 was the last correct entry, so
    /// asserting 149/150 as well proves the fix did not shift the good prefix.
    /// </summary>
    [Theory]
    [InlineData(0, ".notdef")]
    [InlineData(149, "germandbls")]   // last stretch that was already right
    [InlineData(150, "onesuperior")]
    [InlineData(151, "logicalnot")]   // was "twosuperior" — the first wrong SID
    [InlineData(171, "Aacute")]       // start of the missing Latin-1 block
    [InlineData(228, "zcaron")]       // end of it
    [InlineData(229, "exclamsmall")]  // start of the missing small-cap block
    [InlineData(390, "Semibold")]     // the last standard string
    public void ResolvesKnownSidsToTheirSpecifiedNames(int sid, string expected)
    {
        Table()[sid].Should().Be(expected, $"SID {sid} is {expected} in CFF Appendix A");
    }

    /// <summary>
    /// The accented names must be reachable, since those are the ones whose
    /// absence turned extracted text into nothing. udieresis is the glyph the
    /// pdf.js issue4573 fixture renders and extracts.
    /// </summary>
    [Theory]
    [InlineData("udieresis")]
    [InlineData("Odieresis")]
    [InlineData("eacute")]
    [InlineData("ccedilla")]
    [InlineData("agrave")]
    public void ContainsTheAccentedNamesWhoseAbsenceLostText(string name)
    {
        Table().Should().Contain(name,
            $"'{name}' resolved to null, so a glyph using it extracted as nothing");
    }
}
