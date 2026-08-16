using System.Linq;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// The #990 gate: ISO 32000-2 §8.4.5 Table 58 lets an ExtGState set the text
/// state's FONT and SIZE —
///
/// <code>/Font [ &lt;font-ref&gt; &lt;size&gt; ]</code>
///
/// — exactly the two parameters <c>Tf</c> sets.
///
/// <para><b>Why this was a defect and not a gap.</b> <c>SkiaRenderer</c>
/// implemented it; NEITHER content parser did. So for a producer that selects
/// fonts through <c>gs</c>, the renderer drew one font while the text model
/// measured glyph widths and decoded character codes through the PREVIOUS one:
/// wrong glyph cells (the geometry redaction removes on) and the wrong
/// <c>/ToUnicode</c>. #980's differential could not see it, because it compares
/// the two parsers to each other and they were wrong the same way — the #983
/// shape, one operator over.</para>
///
/// <para>It is fixed in <see cref="ContentStreamWalker"/>, which is the single
/// content-stream state machine since #992, so both sinks get it from one
/// implementation rather than two that must agree.</para>
///
/// <para>The fixture makes the font change OBSERVABLE without relying on the
/// size: /F1 is Helvetica and the ExtGState font is Courier, whose widths are a
/// flat 600 against Helvetica's proportional table. A parser that ignored
/// <c>/Font</c> would keep Helvetica's metrics and put the glyphs somewhere
/// else.</para>
/// </summary>
public class ExtGStateFontTests
{
    private const string ExtGStateResources =
        "/ExtGState << /GS1 << /Font [ 6 0 R 12 ] >> >>";

    private const string CourierObject =
        "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>\nendobj\n";

    private static PdfPage BuildPage(string content) =>
        PdfDocument.Open(ContentStreamFixture.Build(
            content,
            extraObjects: CourierObject,
            extraResources: ExtGStateResources)).GetPage(1);

    /// <summary>
    /// <c>gs</c> carrying a Table 58 <c>/Font</c> must select that font's
    /// METRICS, in both sinks. Compared against the same text shown after an
    /// explicit <c>/F2 12 Tf</c> — the other spelling of the same operation —
    /// so the assertion is "the two spellings agree", not a transcribed number.
    /// </summary>
    [Fact]
    public void ExtGStateFont_SelectsTheFontsMetrics_InBothSinks()
    {
        const string viaGs =
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm /GS1 gs (iiii) Tj ET";
        const string viaTf =
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm /F2 12 Tf (iiii) Tj ET";

        var gsLetters = Letters(viaGs);
        var tfLetters = Letters(viaTf);

        gsLetters.Should().HaveCount(4);
        gsLetters.Select(l => l.Width).Should().Equal(tfLetters.Select(l => l.Width),
            "§8.4.5 Table 58: /Font in an ExtGState sets the same font Tf does, "
            + "so the glyph advances must be Courier's either way (#990)");

        Box(viaGs).Should().Be(Box(viaTf),
            "and the operator bounding box redaction intersects against must "
            + "move with it, not stay on the previous font's metrics");
    }

    /// <summary>
    /// The control. Without the <c>gs</c>, the letters keep Helvetica's
    /// proportional widths — so the assertion above is not satisfied by every
    /// font producing the same answer.
    /// </summary>
    [Fact]
    public void WithoutTheExtGState_TheMetricsAreTheOriginalFonts()
    {
        const string plain = "BT /F1 12 Tf 1 0 0 1 72 700 Tm (iiii) Tj ET";
        const string viaGs = "BT /F1 12 Tf 1 0 0 1 72 700 Tm /GS1 gs (iiii) Tj ET";

        Letters(plain).First().Width.Should().NotBeApproximately(
            Letters(viaGs).First().Width, 1e-6,
            "Helvetica's 'i' is 222/1000 em and Courier's is 600/1000 — if these "
            + "matched, the test above would prove nothing");
    }

    /// <summary>
    /// A font set through <c>gs</c> is graphics state (§8.4.1 Table 52), so
    /// <c>Q</c> restores it. This is #990's half of #983 and could not be
    /// written until <c>/Font</c> was implemented — the parameter has to be
    /// settable before "does Q restore it?" means anything.
    /// </summary>
    [Fact]
    public void ExtGStateFont_SetInsideQ_DoesNotSurviveQ()
    {
        const string prologue = "BT /F1 12 Tf 1 0 0 1 72 700 Tm (iiii) Tj ET\n";
        const string probe = "BT 1 0 0 1 72 600 Tm (iiii) Tj ET";

        var neutral = Letters(prologue + probe);
        var bracketed = Letters(prologue + "q /GS1 gs Q\n" + probe);
        var leaked = Letters(prologue + "/GS1 gs\n" + probe);

        // Sensitivity first: if the probe cannot see the font change, the
        // restore assertion below is vacuous.
        leaked.Last().Width.Should().NotBeApproximately(neutral.Last().Width, 1e-6,
            "the probe must be able to observe a gs-set font, or this proves nothing");

        bracketed.Select(l => l.Width).Should().Equal(neutral.Select(l => l.Width),
            "§8.4.1 Table 52: Q restores the font, however it was set (#983/#990)");
    }

    private static IReadOnlyList<Letter> Letters(string content) =>
        new TextExtractor(BuildPage(content)) { IncludeFormFieldValues = false }
            .ExtractLetters();

    private static PdfRectangle? Box(string content)
    {
        var page = BuildPage(content);
        return new ContentStreamParser(page.GetContentStreamBytes(), page).Parse()
            .Operators.Single(op => op.Name == "Tj").BoundingBox;
    }
}
