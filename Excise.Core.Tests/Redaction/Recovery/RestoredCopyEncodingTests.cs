using System.Linq;
using AwesomeAssertions;
using Excise.Core.Fonts;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1644 — what the DRAWN layer of a restored copy can carry.
///
/// <para>The reconstruction is offered as evidence of what a redaction leaked,
/// so it must not alter what it reports. It printed <c>?conceded?</c> where the
/// recovered text was <c>"conceded"</c>, because it flattened to printable
/// ASCII — while WinAnsi, the encoding the page's own font uses, contains those
/// quotes at 0x93/0x94. The old rule was rejecting characters the encoding
/// has.</para>
/// </summary>
public class RestoredCopyEncodingTests
{
    [Theory]
    [InlineData('“', 0x93)]  // “  the character that produced ?conceded?
    [InlineData('”', 0x94)]  // ”
    [InlineData('—', 0x97)]  // —  em dash
    [InlineData('…', 0x85)]  // …  ellipsis
    [InlineData('é', 0xE9)]  // é  Latin-1 high is its own byte
    [InlineData('A', 0x41)]
    public void CharactersWinAnsiHas_AreEncodedNotSubstituted(char c, int expected)
    {
        WinAnsiEncoding.TryMap(c, out var b).Should().BeTrue($"WinAnsi has U+{(int)c:X4}");
        b.Should().Be((byte)expected);
    }

    /// <summary>
    /// ⚠️ The half of #1644 that is NOT fixed, pinned so it cannot be forgotten.
    /// A WinAnsi simple font has no byte for these, and no mapping invents one —
    /// it needs an embedded Type0 subset. What matters is that the loss is
    /// COUNTED, so the reconstruction can say so instead of quietly printing
    /// question marks.
    /// </summary>
    [Theory]
    [InlineData("日本語")]          // CJK
    [InlineData("Привет")]          // Cyrillic
    [InlineData("Ελληνικά")]        // Greek
    [InlineData("Łódź")]            // U+0100+ Latin
    public void CharactersWinAnsiLacks_AreCountedAsLost(string text)
    {
        WinAnsiEncoding.Encode(text, out var lost);
        lost.Should().BeGreaterThan(0,
            "the caller must be able to report that the drawn layer understates the recovery");
    }

    [Fact]
    public void AMixedString_LosesOnlyWhatItMust()
    {
        var bytes = WinAnsiEncoding.Encode("“Smith” 日本", out var lost);

        lost.Should().Be(2, "only the two CJK characters have no WinAnsi form");
        bytes[0].Should().Be(0x93, "the opening quote is encoded, not substituted");
        bytes[6].Should().Be(0x94);
    }

    /// <summary>
    /// The regression in its original shape: an all-ASCII-plus-quotes string,
    /// which is what a US court filing produces, must survive intact.
    /// </summary>
    [Fact]
    public void TheManafortShape_SurvivesWithNoLoss()
    {
        var bytes = WinAnsiEncoding.Encode(
            "“conceded” that he discussed a Ukraine peace plan", out var lost);

        lost.Should().Be(0);
        bytes.Count(b => b == (byte)'?').Should().Be(0, "nothing was substituted");
    }
}
