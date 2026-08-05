using AwesomeAssertions;
using System;
using Excise.Core.Fonts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// #657 — CFF2 is refused, and this pins that the refusal is CLEAN rather than
/// merely present.
///
/// #657 described two different failure modes for one input — <c>CffParser</c>
/// returning null and <c>CffSubsetter</c> throwing — which is what made this
/// worth testing rather than assuming: a null falling through to a fallback can
/// produce SILENTLY WRONG glyphs, and #892 had just added a last-resort
/// <c>/gNNNN</c> numeric-name route a null CFF map could plausibly drop into.
/// For a redaction tool a confidently drawn wrong glyph is worse than a missing
/// one, because it looks exactly as convincing as the right one.
///
/// Measured, the two do not disagree and both are safe. The parser does return
/// null for major version 2. The subsetter's throw is INTERNAL — <c>Subset</c>
/// wraps the parse in a catch-all that returns the original bytes unchanged
/// ("we'd rather not break the font than partially subset"), so no exception
/// crosses the API boundary. The cost of CFF2 is a larger embedded font, not
/// wrong glyphs and not a crash.
///
/// The corpus-side half of #657 (all 4,159 PDFs / 83,862 streams scanned; the
/// files carrying CFF2 bytes render at parity with mutool) lives in
/// Excise.Rendering.Tests/Fonts/Cff2RefusalTests.cs. Together they say: the
/// refusal costs no page, and it cannot fabricate a glyph.
/// </summary>
public class Cff2RefusalTests
{
    /// <summary>
    /// The discriminating pair. The SAME font bytes are parsed twice, differing
    /// in exactly one byte — the header's major version.
    ///
    /// This construction is the point. Asserting only that a synthetic CFF2 stub
    /// returns null would also pass on a parser that returned null for
    /// everything; the version-1 arm proves the rejection is caused by the
    /// version and nothing else.
    /// </summary>
    [Fact]
    public void FlippingOnlyTheMajorVersion_TurnsAGoodFontIntoARefusal()
    {
        var cff1 = RealCff();
        cff1[0].Should().Be(1, "the fixture must be a CFF1 font for this comparison to mean anything");

        var parsedV1 = CffParser.Parse(cff1);
        parsedV1.Should().NotBeNull("the unmodified fixture is a valid CFF1 font and must parse");
        parsedV1!.GlyphNames.Should().NotBeEmpty(
            "a real parse produces a name→GID map — this is what the CFF2 path must NOT fabricate");

        var cff2 = (byte[])cff1.Clone();
        cff2[0] = 2; // the only difference

        CffParser.Parse(cff2).Should().BeNull(
            "CFF2 restructures the Top DICT and drops the charset CFF1's name→GID map is built " +
            "from. Parsing it as CFF1 would not fail loudly, it would return a map of WRONG " +
            "glyph indices — every /Differences name resolving to a plausible, incorrect glyph");
    }

    /// <summary>
    /// The subsetter's half — and a correction to what #657 assumed.
    ///
    /// The issue reported that <c>CffSubsetter</c> "throws
    /// InvalidOperationException('CFF2 not supported')". The throw is real, but
    /// it is INTERNAL: <c>Subset</c> wraps the whole parse in a catch-all that
    /// returns the original bytes unchanged ("we'd rather not break the font
    /// than partially subset"). No exception ever crosses the API boundary.
    ///
    /// So the two halves of #657 do not actually disagree, and both are safe:
    /// the parser yields no name→GID map, the subsetter yields an unsubsetted
    /// font. The cost of CFF2 is a larger embedded font — not wrong glyphs, not
    /// a crash. That is what makes the refusal acceptable to keep.
    ///
    /// Asserting byte-equality rather than "does not throw": a subsetter that
    /// returned a TRUNCATED font would also not throw, and would be the bad
    /// outcome — a font program silently stripped of glyphs it still declares.
    /// </summary>
    [Fact]
    public void TheSubsetterReturnsTheFontUntouched_RatherThanBreakingIt()
    {
        var original = RealCff();
        original[0] = 2; // CFF2

        var result = CffSubsetter.Subset(original, new HashSet<int> { 0, 1, 2 });

        result.Should().Equal(original,
            "an unsupported version must leave the font program exactly as it was. Returning a " +
            "partially-built subset would ship a font declaring glyphs whose charstrings are " +
            "gone — which does not throw, does not log in Release, and renders as blanks");
    }

    /// <summary>
    /// The control for the test above. A CFF1 font must actually BE subsetted,
    /// or "returns the input unchanged" would be the behaviour for every input
    /// and the CFF2 assertion would prove nothing.
    /// </summary>
    [Fact]
    public void ACff1Font_IsGenuinelySubsetted()
    {
        var original = RealCff();

        var result = CffSubsetter.Subset(original, new HashSet<int> { 0, 1, 2 });

        result.Should().NotEqual(original,
            "Inconsolata has a full glyph set and subsetting to three glyphs must change the " +
            "bytes — otherwise the CFF2 test above is satisfied by a no-op subsetter");
        result.Length.Should().BeLessThan(original.Length,
            "a three-glyph subset of a full font must be smaller");
    }

    /// <summary>
    /// The raw CFF table of Inconsolata (SIL OFL), embedded as a test resource —
    /// the same accessor CffSubsetterTests uses. A checked-in real font is what
    /// makes the one-byte comparison in this file discriminating: a synthetic
    /// stub would return null for reasons unrelated to its version.
    /// </summary>
    private static byte[] RealCff()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Inconsolata.cff", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
