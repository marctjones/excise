using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// The #983 gate: <c>q</c> saves and <c>Q</c> restores the §8.4.1 Table 52
/// TEXT state parameters — font and size (<c>Tf</c>), <c>Tc</c>, <c>Tw</c>,
/// <c>Tz</c>, <c>TL</c>, <c>Ts</c>, <c>Tr</c>.
///
/// <para><b>Why this is not a differential.</b> Neither content parser did
/// this, and they did not do it in the SAME way, so <see
/// cref="ParserDifferentialTests"/> — which asks only whether the two agree —
/// passed over it. A differential is structurally blind to a shared defect.
/// This gate therefore checks each machine against a PROPERTY instead of
/// against its twin:</para>
///
/// <list type="number">
/// <item><b>Restore:</b> a parameter set inside a <c>q … Q</c> bracket must not
/// be observable after the <c>Q</c> — the run after the bracket must be
/// byte-identical to a run where the bracket set nothing.</item>
/// <item><b>Sensitivity (the control that makes 1 non-vacuous):</b> the SAME
/// parameter set WITHOUT a bracket must change the observation. Without this,
/// a probe that simply cannot see the parameter would satisfy 1 forever.</item>
/// </list>
///
/// <para><b>What this gate can catch:</b> any Table 52 parameter that leaks
/// past <c>Q</c>, or is restored in one machine and not the other, in
/// ContentStreamParser's operator bounds/decoded text and in TextExtractor's
/// letters. Restoration failures in the font-DERIVED state (ToUnicode/CID maps
/// that travel with <c>Tf</c>) are caught by the two-font row, which decodes
/// and measures through the restored font.</para>
///
/// <para><b>What it CANNOT catch</b>, stated so a green run is not read as more
/// than it is:</para>
/// <list type="bullet">
/// <item>A parameter missing from the enumeration below. The list is
/// transcribed from Table 52 by hand; nothing derives it from the spec.</item>
/// <item><c>Tr</c> (text rendering mode). NEITHER machine tracks it, so no
/// observable moves and the sensitivity control cannot be satisfied. Pinned
/// explicitly by <see cref="TextRenderingMode_IsThisGatesBlindSpot"/> rather
/// than left as prose.</item>
/// <item>An ExtGState <c>/Font</c> (§8.4.5 Table 58) — it sets the same two
/// parameters <c>Tf</c> does, and NEITHER parser implements it (only
/// <c>SkiaRenderer</c> does), so a row for it could not satisfy the
/// sensitivity control. That is a gap in <c>gs</c>, not in q/Q, and is out of
/// #983's scope.</item>
/// <item>Whether the SHARED answer is the RIGHT answer. Both machines agreeing
/// with each other about q/Q is exactly the defect this file exists for, and
/// both agreeing with this file's author is the same shape one level up. The
/// independent-oracle half lives in
/// <see cref="GraphicsStateTextParameterOracleTests"/>, which asks mutool.</item>
/// <item>The RENDERER. <c>SkiaRenderer</c> is a third state machine with the
/// same defect — its <c>GraphicsState.Clone()</c> omits <c>TextState</c>
/// entirely and <c>SaveState</c>/<c>RestoreState</c> never touch
/// <c>_textState</c> — but it is fixed under its own issue, because changing it
/// moves pixels and therefore the rendering differentials and corpus
/// expectation manifests. See #986.</item>
/// </list>
/// </summary>
public class GraphicsStateTextParameterTests
{
    // Sets the base text state, then shows a reference line. Nothing here is
    // re-set by the probe, so anything the bracket leaks survives into it.
    private const string Prologue =
        "BT /F1 12 Tf 14 TL 1 0 0 1 72 700 Tm (base) Tj ET\n";

    // Deliberately carries NO Tf/TL/Tc/Tw/Tz/Ts of its own: it must inherit
    // whatever state survives the bracket. The space exercises Tw, the T*
    // exercises TL, and BT resets only the text MATRIX (§9.4.1), not the state.
    private const string Probe =
        "BT 1 0 0 1 72 600 Tm (a b) Tj T* (c) Tj ET";

    /// <summary>
    /// One row per Table 52 text-state parameter that either machine tracks.
    /// </summary>
    [Theory]
    [InlineData("/F1 36 Tf", "font size")]
    [InlineData("/F2 12 Tf", "font selection — Courier's widths differ from Helvetica's")]
    [InlineData("5 Tc", "character spacing")]
    [InlineData("9 Tw", "word spacing")]
    [InlineData("250 Tz", "horizontal scaling")]
    [InlineData("40 TL", "leading, observed through the probe's T*")]
    [InlineData("11 Ts", "text rise")]
    // Nested brackets must restore LIFO: the inner Q returns to the outer
    // bracket's state, the outer Q to the prologue's.
    [InlineData("/F1 30 Tf q /F1 44 Tf 3 Tc Q", "nested q/Q, restored twice")]
    // The `"` operator sets Tw and Tc as a side effect (§9.4.3); bracketed, it
    // must leave neither behind.
    // The empty string keeps the bracket glyph-free so the observations line
    // up; `"` still sets both spacing parameters before showing nothing.
    [InlineData("BT 1 0 0 1 72 650 Tm 7 3 () \" ET", "Tw/Tc set by the \" operator")]
    public void TextStateParameter_SetInsideQ_DoesNotSurviveQ(string setter, string because)
    {
        var neutral = Observe(Prologue + Probe);
        var bracketed = Observe(Prologue + "q " + setter + " Q\n" + Probe);
        var leaked = Observe(Prologue + setter + "\n" + Probe);

        // 2. Sensitivity FIRST: if the probe cannot see this parameter at all,
        //    the restore assertion below is vacuous and must not be trusted.
        leaked.Letters.Should().NotBe(neutral.Letters,
            $"the probe must be able to observe {because} through TextExtractor, "
            + "or this row proves nothing about restoring it");
        leaked.Operators.Should().NotBe(neutral.Operators,
            $"the probe must be able to observe {because} through ContentStreamParser");

        // 1. Restore.
        bracketed.Letters.Should().Be(neutral.Letters,
            $"§8.4.1 Table 52: Q must restore {because} — TextExtractor");
        bracketed.Operators.Should().Be(neutral.Operators,
            $"§8.4.1 Table 52: Q must restore {because} — ContentStreamParser");
    }

    /// <summary>
    /// The nested case, read from the INSIDE: the inner <c>Q</c> must return to
    /// the outer bracket's state, not to the prologue's and not to the innermost.
    /// The row above only proves the outermost <c>Q</c> lands correctly, which a
    /// stack that popped everything at once would also satisfy.
    /// </summary>
    [Fact]
    public void NestedBrackets_InnerQ_RestoresTheOuterBracketsState()
    {
        // The probe sits INSIDE the outer bracket, after the inner Q, so it must
        // see 30pt — not the prologue's 12 and not the inner 44.
        var actual = Observe(
            Prologue + "q /F1 30 Tf q /F1 44 Tf Q\n" + Probe + "\nQ");
        var expected = Observe(Prologue + "q /F1 30 Tf\n" + Probe + "\nQ");
        var wrongOuter = Observe(Prologue + "q\n" + Probe + "\nQ");

        actual.Letters.Should().Be(expected.Letters,
            "the inner Q restores the state saved by the inner q — 30pt");
        actual.Operators.Should().Be(expected.Operators);
        actual.Letters.Should().NotBe(wrongOuter.Letters,
            "popping past the inner q would land on the prologue's 12pt instead");
    }

    /// <summary>
    /// An unbalanced <c>Q</c> must not throw or corrupt the text state — the
    /// snapshot is only restored when there is one to restore.
    /// </summary>
    [Fact]
    public void UnbalancedQ_LeavesTheTextStateAlone()
    {
        var unbalanced = Observe("Q Q\n" + Prologue + "Q Q\n" + Probe);
        var balanced = Observe(Prologue + Probe);

        unbalanced.Letters.Should().Be(balanced.Letters);
        unbalanced.Operators.Should().Be(balanced.Operators);
    }

    /// <summary>
    /// A realistic producer shape: a differently-styled run bracketed in q/Q
    /// with TEXT inside it. The bracketed run's own letters are expected to
    /// differ; only the tail is compared. This is the case #983 was filed for —
    /// the parametrised rows above keep the bracket text-free so the letter
    /// lists line up, which is a cleaner property but a less realistic stream.
    /// </summary>
    [Fact]
    public void StyledRunBracketedInQ_DoesNotRestyleTheDocumentAfterIt()
    {
        const string styledRun =
            "q /F1 28 Tf 4 Tc 6 Ts BT 1 0 0 1 72 650 Tm (STYLED) Tj ET Q\n";

        var withRun = Observe(Prologue + styledRun + Probe);
        var withoutRun = Observe(Prologue + Probe);

        // The probe emits exactly four letters — 'a', ' ', 'b' then 'c' after
        // the T* — and they are the last four in both documents.
        LastLines(withRun.Letters, 4).Should().Be(LastLines(withoutRun.Letters, 4),
            "the styled run's font size, Tc and Ts must die at its Q");
        LastLines(withRun.Operators, 2).Should().Be(LastLines(withoutRun.Operators, 2),
            "and the operator bounds redaction reads must die with them");
    }

    /// <summary>
    /// <c>Tr</c> is this gate's stated blind spot, pinned rather than described:
    /// neither machine tracks text rendering mode, so setting it changes no
    /// observable and the sensitivity control above could never be satisfied for
    /// it. If this test ever FAILS, some machine started tracking Tr — add it to
    /// the q/Q snapshot (GlyphRemover.TextStateTracker already carries it) and
    /// promote it to a row in the theory above.
    /// </summary>
    [Fact]
    public void TextRenderingMode_IsThisGatesBlindSpot()
    {
        var neutral = Observe(Prologue + Probe);
        var leaked = Observe(Prologue + "1 Tr\n" + Probe);

        leaked.Letters.Should().Be(neutral.Letters,
            "Tr is invisible to TextExtractor, so its restore cannot be gated here");
        leaked.Operators.Should().Be(neutral.Operators,
            "Tr is invisible to ContentStreamParser, so its restore cannot be gated here");
    }

    // ---------------------------------------------------------------
    // Observation
    // ---------------------------------------------------------------

    private readonly record struct Observation(string Letters, string Operators);

    /// <summary>
    /// Both machines' full observable output over the same bytes, rendered as
    /// text so a failure names the glyph that moved. Positions are formatted to
    /// four decimals: the fixtures are exact rational arithmetic, so this is a
    /// formatting choice, not a tolerance.
    /// </summary>
    private static Observation Observe(string content)
    {
        using var doc = PdfDocument.Open(ParityFixture.Build(
            content,
            extraObjects:
                "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>\nendobj\n",
            extraFontResources: "/F2 6 0 R"));
        var page = doc.GetPage(1);

        var letters = new TextExtractor(page) { IncludeFormFieldValues = false }.ExtractLetters();
        var lettersText = new StringBuilder();
        foreach (var l in letters)
        {
            // Every number goes through F(), which is invariant, so the
            // interpolation itself carries no culture.
            lettersText.Append(
                $"{l.Value}|{l.FontName}|{F(l.FontSize)}|{F(l.StartX)},{F(l.StartY)}|"
                + $"{F(l.GlyphRectangle.Left)},{F(l.GlyphRectangle.Bottom)},"
                + $"{F(l.GlyphRectangle.Right)},{F(l.GlyphRectangle.Top)}|{F(l.Width)}\n");
        }

        var stream = new ContentStreamParser(page.GetContentStreamBytes(), page).Parse();
        var opsText = new StringBuilder();
        foreach (var op in stream.Operators)
        {
            if (op.Name is not ("Tj" or "TJ" or "'" or "\"")) continue;
            // A show-operator that produced no glyphs (an empty string) is not
            // an observation — skipping it lets a fixture set state through `"`
            // without adding a line the neutral run cannot have.
            if (string.IsNullOrEmpty(op.TextContent)) continue;
            var box = op.BoundingBox;
            opsText.Append(
                $"{op.Name}|{op.TextContent}|"
                + (box is null
                    ? "none"
                    : $"{F(box.Value.Left)},{F(box.Value.Bottom)},{F(box.Value.Right)},{F(box.Value.Top)}")
                + "\n");
        }

        return new Observation(lettersText.ToString(), opsText.ToString());
    }

    private static string F(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>The last <paramref name="count"/> non-empty lines.</summary>
    private static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThanOrEqualTo(count,
            "the fixture must actually produce the run being compared");
        return string.Join('\n', lines.Skip(lines.Length - count));
    }
}
