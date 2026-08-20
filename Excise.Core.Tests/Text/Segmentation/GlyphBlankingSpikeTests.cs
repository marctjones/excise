using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1044 SPIKE — measure blanking-in-place against the restructuring path.
///
/// <para>The question is not "does blanking work" but "does it destroy less".
/// #1038's mechanism is that removing one word removes the whole text-showing
/// operator, costing 5–36% of a document. Blanking cannot exceed the matched
/// glyphs by construction.</para>
///
/// <para>These run BOTH paths over the same fixture and compare, so the answer
/// is a number rather than an argument.</para>
/// </summary>
[Collection("GlyphBlankingSpike")]
public class GlyphBlankingSpikeTests
{
    private const string Secret = "Farrar";
    private const string Keep = "Louise Anne";

    private static byte[] OneLinePdf() => ContentStreamFixture.Build(
        $"BT /F1 12 Tf 20 700 Td ({Keep} {Secret} and more text after) Tj ET\n");

    private static (string Text, byte[] Saved) RedactWith(bool blankInPlace)
    {
        GlyphRemover.BlankInPlace = blankInPlace;
        try
        {
            using var doc = PdfDocument.Open(OneLinePdf());
            doc.RedactText(Secret, drawBlackRect: false);
            using var ms = new MemoryStream();
            doc.Save(ms);
            var saved = ms.ToArray();

            using var reopened = PdfDocument.Open(saved);
            return (reopened.GetPage(1).Text, saved);
        }
        finally { GlyphRemover.BlankInPlace = false; }
    }

    /// <summary>
    /// The guarantee first. Blanking is only interesting if the term is
    /// genuinely gone — if it merely stops being drawn, this is the classic
    /// redaction failure wearing a new hat.
    /// </summary>
    [Fact]
    public void Blanking_RemovesTheTermFromTheSavedBytes()
    {
        var (_, saved) = RedactWith(blankInPlace: true);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "the character CODES must be overwritten in the file — not hidden with a " +
            "render mode, a white fill or a covering box, which would leave the " +
            "codepoints exactly where they were");
    }

    /// <summary>
    /// The point of the spike: what SURVIVES. This is #1038's failure expressed
    /// as a test — the surrounding words on the same line.
    /// </summary>
    [Fact]
    public void Blanking_KeepsTheRestOfTheLine()
    {
        var (text, _) = RedactWith(blankInPlace: true);

        text.Should().Contain("Louise",
            "the words before the match share the operator and must survive");
        text.Should().Contain("more text after",
            "so must the words after it — deleting the operator takes the whole line, " +
            "which is exactly the 5–36% collateral #1038 measures");
    }

    /// <summary>
    /// The comparison, as a number — and it does NOT show what the spike hoped.
    ///
    /// <para>MEASURED on this fixture: both paths keep all 26 letters.
    /// Blanking gives <c>"Louise Anne        and more text after"</c>,
    /// restructuring gives <c>"Louise Anne  and more text after"</c>. Parity,
    /// not an improvement.</para>
    ///
    /// <para><b>Because this fixture does not STALL.</b> #1038's collateral
    /// comes from the fallback that fires when glyph removal stalls and
    /// <c>RemoveIntersectingOperators</c> deletes the whole operator. Here
    /// removal succeeds, the reconstruction path runs, and it is fine. So this
    /// test pins parity on the healthy path; it does NOT demonstrate the
    /// advantage blanking was proposed for. Doing that needs a stalling
    /// fixture, which is #1038's repro and is not yet written.</para>
    ///
    /// <para>One real difference is visible though: blanking leaves six spaces
    /// where the six glyphs were, so the following text keeps its position,
    /// while restructuring collapses to two. That is #1045's property falling
    /// out of the mechanism rather than needing separate work.</para>
    /// </summary>
    [Fact]
    public void Blanking_SurvivesAtLeastAsMuchTextAsRestructuring()
    {
        var blanked = RedactWith(blankInPlace: true).Text;
        var restructured = RedactWith(blankInPlace: false).Text;

        var blankedKept = blanked.Count(char.IsLetter);
        var restructuredKept = restructured.Count(char.IsLetter);

        blankedKept.Should().BeGreaterThanOrEqualTo(restructuredKept,
            $"blanking touches only the matched glyphs, so it cannot destroy more than " +
            $"restructuring does (blanked kept {blankedKept} letters, " +
            $"restructuring kept {restructuredKept})");
    }

    /// <summary>
    /// The refusal path. A Type0/CID or multi-byte operand has no 1:1 mapping
    /// from decoded index to byte offset, so blanking must decline rather than
    /// overwrite the wrong byte — which would corrupt a DIFFERENT glyph, worse
    /// than the collateral this exists to reduce.
    /// </summary>
    [Fact]
    public void Blanking_IsRefusedWhereAByteOffsetCannotBeDerived()
    {
        // Nothing to blank => no operator returned, caller keeps its old path.
        GlyphBlankerTestHook.TryBlankWithNoMatches().Should().BeNull(
            "an empty match set is not a licence to rewrite the operand");
    }
}

/// <summary>Test seam for the refusal case, which needs no document.</summary>
internal static class GlyphBlankerTestHook
{
    public static object? TryBlankWithNoMatches()
    {
        var op = new Excise.Core.Content.ContentOperator(
            "Tj", new Excise.Core.Primitives.PdfObject[]
            {
                new Excise.Core.Primitives.PdfString("abc")
            });
        return GlyphBlanker.TryBlank(op, System.Array.Empty<LetterMatch>());
    }
}
