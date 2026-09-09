using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle evidence for individual content-stream OPERATORS
/// (<c>test-pdfs/manifests/pdf-spec-registry/sections/operators.json</c>).
///
/// Those 73 capabilities were graded <c>implemented</c> off
/// <c>OperatorCoverageTests.AuthoritativeOperatorInventory_AllStandardOperators_ParseAndRoundTrip</c>
/// alone — excise parsing its own writer's output and excise's own render
/// smoke test. That is the shape CLAUDE.md's "no-self-oracle" rule exists
/// for: it proves excise is internally consistent, not that any operator's
/// VISUAL EFFECT is correct.
///
/// Every assertion below instead renders a minimal fixture that isolates one
/// operator's spec-mandated effect, renders it with excise AND with mutool
/// and/or pdftocairo (two engines that share no code with excise or with each
/// other), and compares either raw pixel color or ink-bbox geometry. Where the
/// oracle-observable quantity is a RATIO or DELTA (Tz's scale factor, TL's
/// line-to-line shift) rather than an absolute pixel position, the assertion
/// compares that ratio/delta across engines — which stays valid across
/// platform font substitution, the same reasoning
/// <see cref="GraphicsStateTextParameterRenderingTests"/> and
/// <see cref="NegativeFontSizeTests"/> use.
///
/// <para><b>Tw (operator-051) was investigated and deliberately left
/// unclaimed</b> — see the comment above where its test would be. Two
/// independent fixtures against mutool both showed real excise bugs (dropped
/// intra-string spacing without an explicit <c>/Widths</c> array; a
/// font-size double-scale that pushes glyphs off-page WITH one) rather than
/// confirming the capability. Reporting that honestly is the point of this
/// file's no-self-oracle mandate — a capability is not "verified" by
/// switching to whichever fixture happens to pass.</para>
/// </summary>
public class OperatorVerificationParityTests : IDisposable
{
    private const int Dpi = 72;
    private readonly List<string> _temp = new();

    // ── f (nonzero) / f* (even-odd) fill rules — operator-021 / operator-023 ──

    /// <summary>
    /// Two <c>re</c> rectangles wound the SAME direction (as every <c>re</c>
    /// always is, per §8.5.2.1) nested one inside the other. Under the
    /// NONZERO winding rule both contribute the same sign, so the inner
    /// square's winding number is 2 (nonzero) and the whole outer square
    /// paints solid — no hole. This is spec-mandated behaviour, not a
    /// SkiaSharp choice, so mutool and pdftocairo must show the same thing.
    /// </summary>
    [Fact]
    public void NonzeroFill_F_DonutIsSolid_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(PageFixture(DonutPath + "f", 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 200);

        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        IsInk(mutool!, 100, 100).Should().BeTrue("mutool: nonzero winding fills the inner square too");
        IsInk(cairo!, 100, 100).Should().BeTrue("pdftocairo agrees");
        IsInk(excise, 100, 100).Should().BeTrue(
            "the donut's centre must be painted under the nonzero rule: two same-direction " +
            "windings do not cancel, so `f` must not leave a hole the way `f*` would");
    }

    /// <summary>
    /// The same two nested, same-direction rectangles, filled with <c>f*</c>
    /// instead. Under EVEN-ODD, the centre is crossed twice (even) so it is
    /// EXCLUDED, and a ring-shaped hole appears — the textbook nonzero-vs-
    /// even-odd distinguishing fixture (§8.5.3.3.2/3).
    /// </summary>
    [Fact]
    public void EvenOddFill_FStar_DonutHasHole_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(PageFixture(DonutPath + "f*", 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 200);

        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        // Guard: the ring itself must still be inked in all three, or a
        // renderer that painted nothing at all would pass the hole check for
        // the wrong reason.
        IsInk(mutool!, 100, 170).Should().BeTrue("mutool: guard — the ring band must be painted");
        IsInk(cairo!, 100, 170).Should().BeTrue("pdftocairo: guard — the ring band must be painted");
        IsInk(excise, 100, 170).Should().BeTrue("excise: guard — the ring band must be painted");

        IsInk(mutool!, 100, 100).Should().BeFalse("mutool: even-odd excludes the doubly-wound centre");
        IsInk(cairo!, 100, 100).Should().BeFalse("pdftocairo agrees");
        IsInk(excise, 100, 100).Should().BeFalse(
            "the donut's centre must be a HOLE under the even-odd rule — a renderer that " +
            "cannot distinguish `f` from `f*` would paint this solid");
    }

    // ── W (nonzero clip) / W* (even-odd clip) — operator-029 / operator-030 ──

    [Fact]
    public void Clip_W_NonzeroWindingConfinesSubsequentFill_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        const string content = "q 70 70 60 60 re W n 20 20 160 160 re f Q";
        var path = WriteTemp(PageFixture(content, 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 200);

        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        // Guard: something must actually paint inside the clip.
        IsInk(mutool!, 100, 100).Should().BeTrue("mutool: guard — inside the clip must be painted");
        IsInk(cairo!, 100, 100).Should().BeTrue("pdftocairo: guard — inside the clip must be painted");

        IsInk(mutool!, 30, 30).Should().BeFalse("mutool: outside the clip rect must stay unpainted");
        IsInk(cairo!, 30, 30).Should().BeFalse("pdftocairo agrees");
        IsInk(excise, 100, 100).Should().BeTrue("excise: guard — inside the clip must be painted");
        IsInk(excise, 30, 30).Should().BeFalse(
            "`W` must confine the later `f` to the clip path — a renderer that ignored `W` " +
            "would paint the full 160x160 rectangle, reaching this corner");
    }

    [Fact]
    public void Clip_WStar_EvenOddConfinesSubsequentFill_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        const string content = "q " + DonutPath + "W* n 20 20 160 160 re f Q";
        var path = WriteTemp(PageFixture(content, 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 200);

        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        // Guard: the ring band must be painted in all three.
        IsInk(mutool!, 100, 170).Should().BeTrue("mutool: guard — the ring band must be painted");
        IsInk(cairo!, 100, 170).Should().BeTrue("pdftocairo: guard — the ring band must be painted");
        IsInk(excise, 100, 170).Should().BeTrue("excise: guard — the ring band must be painted");

        IsInk(mutool!, 100, 100).Should().BeFalse("mutool: the even-odd clip excludes the centre");
        IsInk(cairo!, 100, 100).Should().BeFalse("pdftocairo agrees");
        // (10, 10) is genuinely outside the OUTER rectangle (device x<20, so
        // PDF x=10 < the outer rect's left edge at 20) — unlike (30, 30),
        // which is device-inside the outer rect and PDF-outside the inner
        // one, i.e. squarely IN the ring and correctly inked (first draft of
        // this test used (30, 30) and failed here on mutool's OWN bitmap —
        // the oracle logic was wrong, not excise).
        IsInk(mutool!, 10, 10).Should().BeFalse("mutool: outside the outer clip boundary entirely");
        IsInk(cairo!, 10, 10).Should().BeFalse("pdftocairo agrees");

        IsInk(excise, 100, 100).Should().BeFalse(
            "`W*` clips with the even-odd rule: the fill must not reach the donut's hole");
        IsInk(excise, 10, 10).Should().BeFalse("nor the region entirely outside the outer clip boundary");
    }

    // ── d (dash pattern) + S (stroke) — operator-008 / operator-019 ──────────

    /// <summary>
    /// A dashed [10 5] stroke must paint measurably LESS ink along its length
    /// than the identical solid stroke — an oracle-observable consequence of
    /// <c>d</c> that a renderer which ignores the dash array (painting a
    /// solid line regardless) would fail. Compared as a RATIO against mutool
    /// so platform stroke anti-aliasing differences don't confound it.
    /// </summary>
    [Fact]
    public void DashPattern_D_ReducesStrokeInkVsSolid_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var solidPath = WriteTemp(PageFixture("4 w 20 100 m 180 100 l S", 200, 200));
        var dashedPath = WriteTemp(PageFixture("[10 5] 0 d 4 w 20 100 m 180 100 l S", 200, 200));

        using var mutoolSolid = MutoolReferenceRenderer.RenderPage(solidPath, 1, Dpi);
        using var mutoolDashed = MutoolReferenceRenderer.RenderPage(dashedPath, 1, Dpi);
        Assert.SkipWhen(mutoolSolid == null || mutoolDashed == null, "mutool could not render a fixture");

        var mutoolSolidCols = InkedColumnCount(mutoolSolid!, 20, 180, 96, 104);
        var mutoolDashedCols = InkedColumnCount(mutoolDashed!, 20, 180, 96, 104);
        mutoolSolidCols.Should().BeGreaterThan(140, "guard: mutool's solid stroke must span nearly the whole line");
        mutoolDashedCols.Should().BeLessThan((int)(mutoolSolidCols * 0.9),
            "guard: mutool must actually show fewer inked columns for the dashed stroke");

        using var exciseSolid = RenderWithExcise(solidPath, 200);
        using var exciseDashed = RenderWithExcise(dashedPath, 200);
        var exciseSolidCols = InkedColumnCount(exciseSolid, 20, 180, 96, 104);
        var exciseDashedCols = InkedColumnCount(exciseDashed, 20, 180, 96, 104);

        exciseSolidCols.Should().BeGreaterThan(140, "excise's solid stroke must span nearly the whole line");
        exciseDashedCols.Should().BeLessThan((int)(exciseSolidCols * 0.9),
            "`d`'s dash array must reduce the painted length — a renderer that ignored the " +
            "dash pattern (or applied it only to `S`'s geometry without gaps) would paint the " +
            "same column count as the solid stroke");

        var mutoolRatio = mutoolDashedCols / (double)mutoolSolidCols;
        var exciseRatio = exciseDashedCols / (double)exciseSolidCols;
        exciseRatio.Should().BeApproximately(mutoolRatio, 0.25,
            "the fraction of the line left inked by a [10 5] dash (~2/3 in theory) should agree " +
            "between excise and an independent renderer, not just both be 'somewhat less'");
    }

    // ── J (line cap) — operator-005 ───────────────────────────────────────────

    /// <summary>
    /// A round cap (<c>1 J</c>) extends painted ink half a line-width PAST the
    /// segment's nominal endpoint (a semicircle, §8.4.3.3); a butt cap
    /// (<c>0 J</c>) does not. The DELTA between the two caps' rightmost inked
    /// column is the oracle-observable signal, compared across engines.
    /// </summary>
    [Fact]
    public void LineCap_J_RoundExtendsPastButt_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var buttPath = WriteTemp(PageFixture("0 J 8 w 20 100 m 100 100 l S", 200, 200));
        var roundPath = WriteTemp(PageFixture("1 J 8 w 20 100 m 100 100 l S", 200, 200));

        using var mutoolButt = MutoolReferenceRenderer.RenderPage(buttPath, 1, Dpi);
        using var mutoolRound = MutoolReferenceRenderer.RenderPage(roundPath, 1, Dpi);
        Assert.SkipWhen(mutoolButt == null || mutoolRound == null, "mutool could not render a fixture");

        var mutoolButtRight = RightmostInkedColumn(mutoolButt!, 96, 104);
        var mutoolRoundRight = RightmostInkedColumn(mutoolRound!, 96, 104);
        mutoolButtRight.Should().BeGreaterThan(0, "guard: mutool must paint the butt-capped line");
        var mutoolDelta = mutoolRoundRight - mutoolButtRight;
        mutoolDelta.Should().BeGreaterThan(1, "guard: mutool's round cap must extend past its butt cap");

        using var exciseButt = RenderWithExcise(buttPath, 200);
        using var exciseRound = RenderWithExcise(roundPath, 200);
        var exciseButtRight = RightmostInkedColumn(exciseButt, 96, 104);
        var exciseRoundRight = RightmostInkedColumn(exciseRound, 96, 104);
        exciseButtRight.Should().BeGreaterThan(0, "excise must paint the butt-capped line");
        var exciseDelta = exciseRoundRight - exciseButtRight;

        exciseDelta.Should().BeGreaterThan(1,
            "`1 J` (round cap) must extend the stroke's ink past `0 J` (butt cap) — a renderer " +
            "that ignores line cap style would show the same rightmost column for both");
        Math.Abs(exciseDelta - mutoolDelta).Should().BeLessThanOrEqualTo(3,
            "the round cap's extension is half the 8pt line width (~4px @72dpi); excise's " +
            "extension should agree with mutool's, not just be 'also greater than zero'");
    }

    // ── g/G, rg/RG, cs/CS + sc/SC + scn/SCN — colour operators ───────────────
    // operator-038 (g), operator-040 (rg), operator-032 (cs), operator-035 (sc),
    // operator-036 (scn)

    /// <summary>
    /// Four fill patches, each set through a DIFFERENT colour-setting operator
    /// combination, sampled for actual RGB pixel value against mutool and
    /// pdftocairo. This is the strongest oracle available for a colour
    /// operator: not ink presence, but the resolved COLOUR.
    /// </summary>
    [Fact]
    public void FillColorOperators_G_RG_Cs_Sc_Scn_MatchIndependentRendererPixelColor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        const string content =
            "0.25 g 0 0 50 100 re f "                    // patch 0: g (DeviceGray)
          + "1 0 0 rg 50 0 50 100 re f "                  // patch 1: rg (DeviceRGB)
          + "/DeviceGray cs 0.75 sc 100 0 50 100 re f "   // patch 2: cs + sc
          + "/DeviceRGB cs 0 1 0 scn 150 0 50 100 re f";  // patch 3: cs + scn
        var path = WriteTemp(PageFixture(content, 200, 100));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 100);
        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        AssertPatchColorMatches(excise, mutool!, cairo!, 25, 50, "g", 64, 64, 64);
        AssertPatchColorMatches(excise, mutool!, cairo!, 75, 50, "rg", 255, 0, 0);
        AssertPatchColorMatches(excise, mutool!, cairo!, 125, 50, "cs+sc", 191, 191, 191);
        AssertPatchColorMatches(excise, mutool!, cairo!, 175, 50, "cs+scn", 0, 255, 0);
    }

    /// <summary>
    /// The stroke-side mirror: <c>G</c>, <c>RG</c>, <c>CS</c>+<c>SC</c>,
    /// <c>CS</c>+<c>SCN</c> — operator-037, operator-039, operator-031,
    /// operator-033/034.
    /// </summary>
    [Fact]
    public void StrokeColorOperators_G_RG_Cs_Sc_Scn_MatchIndependentRendererPixelColor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        const string content =
            "6 w "
          + "0.25 G 25 20 m 25 80 l S "                    // patch 0: G (DeviceGray)
          + "1 0 0 RG 75 20 m 75 80 l S "                   // patch 1: RG (DeviceRGB)
          + "/DeviceGray CS 0.75 SC 125 20 m 125 80 l S "   // patch 2: CS + SC
          + "/DeviceRGB CS 0 1 0 SCN 175 20 m 175 80 l S";  // patch 3: CS + SCN
        var path = WriteTemp(PageFixture(content, 200, 100));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 100);
        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        AssertPatchColorMatches(excise, mutool!, cairo!, 25, 50, "G", 64, 64, 64);
        AssertPatchColorMatches(excise, mutool!, cairo!, 75, 50, "RG", 255, 0, 0);
        AssertPatchColorMatches(excise, mutool!, cairo!, 125, 50, "CS+SC", 191, 191, 191);
        AssertPatchColorMatches(excise, mutool!, cairo!, 175, 50, "CS+SCN", 0, 255, 0);
    }

    // ── Tr (text render mode) — operator-055 ─────────────────────────────────

    /// <summary>
    /// <c>3 Tr</c> (invisible) must paint NO ink at all — the render mode
    /// PDF viewers use for the invisible OCR text layer over a scanned image.
    /// A renderer that ignores <c>Tr</c> and always fills text would leak ink
    /// here. The control fixture (<c>0 Tr</c>, default fill) proves the same
    /// string DOES paint ink when the mode says to.
    /// </summary>
    [Fact]
    public void TextRenderMode_Tr3Invisible_PaintsNoInk_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var visiblePath = WriteTemp(TextFixture("0 Tr (SECRET) Tj"));
        var invisiblePath = WriteTemp(TextFixture("3 Tr (SECRET) Tj"));

        using var mutoolVisible = MutoolReferenceRenderer.RenderPage(visiblePath, 1, Dpi);
        using var mutoolInvisible = MutoolReferenceRenderer.RenderPage(invisiblePath, 1, Dpi);
        Assert.SkipWhen(mutoolVisible == null || mutoolInvisible == null, "mutool could not render a fixture");

        AnyInk(mutoolVisible!).Should().BeTrue("guard: mutool's control fixture (Tr 0) must show ink");
        AnyInk(mutoolInvisible!).Should().BeFalse("mutool: Tr 3 must paint nothing");

        using var exciseVisible = RenderWithExcise(visiblePath, 120);
        using var exciseInvisible = RenderWithExcise(invisiblePath, 120);

        AnyInk(exciseVisible).Should().BeTrue("excise's control fixture (Tr 0) must show ink");
        AnyInk(exciseInvisible).Should().BeFalse(
            "`3 Tr` selects the invisible text render mode (§9.3.6, Table 106) — a renderer " +
            "that always fills glyphs regardless of Tr would leak ink here");
    }

    // ── Tz (horizontal scaling) — operator-052 ────────────────────────────────

    /// <summary>
    /// <c>50 Tz</c> must halve the glyph run's advance width relative to
    /// <c>100 Tz</c>. Compared as a RATIO (not absolute width) so platform
    /// font substitution for the non-embedded base-14 font doesn't confound
    /// the comparison — the same reasoning <see cref="NegativeFontSizeTests"/>
    /// documents.
    /// </summary>
    [Fact]
    public void HorizontalScaling_Tz_HalvesWidth_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var fullPath = WriteTemp(TextFixture("100 Tz (WWWWWWWW) Tj"));
        var halfPath = WriteTemp(TextFixture("50 Tz (WWWWWWWW) Tj"));

        using var mutoolFull = MutoolReferenceRenderer.RenderPage(fullPath, 1, Dpi);
        using var mutoolHalf = MutoolReferenceRenderer.RenderPage(halfPath, 1, Dpi);
        Assert.SkipWhen(mutoolFull == null || mutoolHalf == null, "mutool could not render a fixture");

        var mutoolFullWidth = InkWidth(mutoolFull!);
        var mutoolHalfWidth = InkWidth(mutoolHalf!);
        mutoolFullWidth.Should().BeGreaterThan(20, "guard: mutool must paint a non-trivial run");
        var mutoolRatio = mutoolHalfWidth / (double)mutoolFullWidth;

        using var exciseFull = RenderWithExcise(fullPath, 120);
        using var exciseHalf = RenderWithExcise(halfPath, 120);
        var exciseFullWidth = InkWidth(exciseFull);
        var exciseHalfWidth = InkWidth(exciseHalf);
        exciseFullWidth.Should().BeGreaterThan(20, "excise must paint a non-trivial run");
        var exciseRatio = exciseHalfWidth / (double)exciseFullWidth;

        exciseRatio.Should().BeApproximately(0.5, 0.12,
            "`50 Tz` must scale the run's advance to roughly half of `100 Tz`'s");
        exciseRatio.Should().BeApproximately(mutoolRatio, 0.12,
            "the scaling ratio excise applies must agree with mutool's independent rendering, " +
            "not merely be 'also smaller'");
    }

    // ── Tw (word spacing) — operator-051 — DELIBERATELY NOT CLAIMED ──────────
    //
    // A differential fixture for `Tw` was built and run against mutool
    // (3 interior spaces x 20 Tw should widen "A A A A" by ~60pt). It
    // surfaced a REAL excise bug rather than confirming the capability:
    //
    //   1. Against a bare base-14 font (no /Widths in the font dict, which
    //      base-14 fonts are not required to carry, §9.6.2.2): excise's
    //      delta was 0px. `SkiaRenderer.Text.cs`'s `needsExplicitSpacing`
    //      (the path that inserts Tc/Tw BETWEEN glyphs within one `Tj` call)
    //      is gated on `currentFont.Widths != null`, and `PdfFontResolver`
    //      never populates `Widths` without an explicit `/Widths` array —
    //      so intra-string Tw is silently dropped for the single most common
    //      simple-font declaration in real PDFs.
    //   2. Retried with an explicit `/Widths` array (exercising the
    //      "authoritative" `drawWithPdfWidths` path instead): excise's
    //      spaced string collapsed to NARROWER than the unspaced one
    //      (measured: normal width 83px, spaced width 15px — the later
    //      glyphs are pushed off-page). Root cause, `SkiaRenderer.Text.cs`
    //      near `cursor += (w / 1000f + spacing * sizeSign) * effectiveSize;`
    //      (in the per-glyph draw loop for the `/Widths`-array path): Tc/Tw
    //      are unscaled text-space units per §9.4.4 and must NOT be
    //      multiplied by font size — the surrounding comment says exactly
    //      that — but the code multiplies the whole `(w/1000 + spacing)` sum
    //      by `effectiveSize`, so a `20 Tw` at 24pt advances the cursor by
    //      ~480 units instead of 20, shoving every glyph after the first
    //      space off the page. The SEPARATE end-of-string cursor advance
    //      used for what follows (`width += spaceCount * WordSpacing *
    //      xScale` near the bottom of `RenderText`) does NOT have this bug —
    //      only the intra-string glyph positions do.
    //
    // Per this file's no-self-oracle mandate, a capability is not "verified"
    // by picking whichever fixture happens to pass. Both fixtures independently
    // show `Tw` visibly broken against mutool, so operator-051's render mode is
    // left unclaimed here rather than wired to a fixture that would hide it.
    // Not a redaction/extraction defect: `ContentStreamWalker` — the sink
    // redaction and text extraction actually consume — applies word spacing to
    // glyph advances in its own, unaffected code path (`_wordSpacing`).
    // Flagged separately for a follow-up fix; out of this task's scope (test
    // authorship + registry wiring only).

    // ── TL (leading) + T* (next line) — operator-053 / operator-060 ──────────

    /// <summary>
    /// <c>30 TL</c> followed by <c>T*</c> must move the pen down exactly 30
    /// units for the next line (§9.4.3) — measured as the vertical shift
    /// between two lines' ink, which is independent of the substituted font's
    /// horizontal metrics.
    /// </summary>
    [Fact]
    public void Leading_TL_And_NextLine_TStar_ShiftsByLeadingAmount_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = "BT /F1 24 Tf 30 TL 1 0 0 1 20 190 Tm (Line1) Tj T* (Line2) Tj ET";
        var path = WriteTemp(PageFixture(content, 200, 240, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the fixture");

        var mutoolLine1Top = TopInkedRow(mutool!, 0, 55);
        var mutoolLine2Top = TopInkedRow(mutool!, 55, 110);
        Assert.SkipWhen(mutoolLine1Top < 0 || mutoolLine2Top < 0, "mutool did not paint both lines where expected");
        var mutoolDelta = mutoolLine2Top - mutoolLine1Top;
        mutoolDelta.Should().BeGreaterThan(10, "guard: mutool's second line must sit visibly below the first");

        using var excise = RenderWithExcise(path, 240);
        var exciseLine1Top = TopInkedRow(excise, 0, 55);
        var exciseLine2Top = TopInkedRow(excise, 55, 110);
        exciseLine1Top.Should().BeGreaterThanOrEqualTo(0, "excise must paint the first line");
        exciseLine2Top.Should().BeGreaterThanOrEqualTo(0, "excise must paint the second line");
        var exciseDelta = exciseLine2Top - exciseLine1Top;

        Math.Abs(exciseDelta - 30).Should().BeLessThanOrEqualTo(6,
            "`T*` must move the pen down by exactly the `TL` leading (30pt @72dpi = 30px)");
        Math.Abs(exciseDelta - mutoolDelta).Should().BeLessThanOrEqualTo(6,
            "excise's line-to-line shift must agree with mutool's independent measurement");
    }

    // ── sh (shading) — operator-043 ───────────────────────────────────────────

    /// <summary>
    /// A Type 2 (axial) shading painted directly via <c>sh</c>, red at the
    /// left edge fading to blue at the right (§8.7.4.5.3). The oracle-visible
    /// signal is the SIGN of the colour gradient — R decreasing and B
    /// increasing left-to-right — which any conforming shading implementation
    /// must reproduce, not excise-specific interpolation.
    /// </summary>
    [Fact]
    public void AxialShading_Sh_GradientDirection_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(AxialShadingFixture());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path, 100);
        Assert.SkipWhen(mutool == null || cairo == null, "an oracle failed to render the fixture");

        var mutoolLeft = SampleColor(mutool!, 15, 50);
        var mutoolRight = SampleColor(mutool!, 185, 50);
        mutoolLeft.R.Should().BeGreaterThan(mutoolRight.R, "guard: mutool must show R decreasing left-to-right");
        mutoolRight.B.Should().BeGreaterThan(mutoolLeft.B, "guard: mutool must show B increasing left-to-right");

        var cairoLeft = SampleColor(cairo!, 15, 50);
        var cairoRight = SampleColor(cairo!, 185, 50);
        cairoLeft.R.Should().BeGreaterThan(cairoRight.R, "guard: pdftocairo agrees on R direction");
        cairoRight.B.Should().BeGreaterThan(cairoLeft.B, "guard: pdftocairo agrees on B direction");

        var exciseLeft = SampleColor(excise, 15, 50);
        var exciseRight = SampleColor(excise, 185, 50);
        exciseLeft.R.Should().BeGreaterThan(exciseRight.R,
            "`sh` must paint the axial shading with R fading out left-to-right per the /Function");
        exciseRight.B.Should().BeGreaterThan(exciseLeft.B,
            "and B fading in left-to-right — a renderer that ignored /Function evaluation " +
            "(e.g. painting a flat average colour) would show neither gradient");
        exciseLeft.R.Should().BeGreaterThan((byte)150, "the left edge must be strongly red, near C0");
        exciseRight.B.Should().BeGreaterThan((byte)150, "the right edge must be strongly blue, near C1");
    }

    // ── fixtures ───────────────────────────────────────────────────────────

    private const string DonutPath = "20 20 160 160 re 70 70 60 60 re ";

    private static byte[] PageFixture(string content, int w, int h, params (string Name, string Base)[] fonts)
    {
        var resources = fonts.Length == 0
            ? ""
            : "/Font << " + string.Join(" ", System.Linq.Enumerable.Select(fonts, (f, i) => $"/{f.Name} {4 + i} 0 R")) + " >>";
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {w} {h}] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents {4 + fonts.Length} 0 R "
                + $"/Resources << {resources} >> >>\nendobj\n",
        };
        foreach (var (_, baseFont) in fonts)
            objects.Add($"{4 + objects.Count - 3} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} >>\nendobj\n");
        objects.Add($"{4 + fonts.Length} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        return Assemble(objects);
    }

    /// <summary>200x120 page, Helvetica /F1 at 24pt, text positioned at (20,60). The
    /// caller supplies the operators between <c>Tf</c> and <c>ET</c>.</summary>
    private static byte[] TextFixture(string trailingOps)
    {
        var content = $"BT /F1 24 Tf 1 0 0 1 20 60 Tm {trailingOps} ET";
        return PageFixture(content, 200, 120, ("F1", "Helvetica"));
    }

    /// <summary>200x100 page painting a full-page axial shading (red -> blue, left -> right) via <c>sh</c>.</summary>
    private static byte[] AxialShadingFixture()
    {
        const string content = "q 0 0 200 100 re W n /Sh1 sh Q";
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 100] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 6 0 R "
                + "/Resources << /Shading << /Sh1 4 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] "
                + "/Function 5 0 R /Extend [true true] >>\nendobj\n",
            "5 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>\nendobj\n",
            $"6 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };
        return Assemble(objects);
    }

    /// <summary>
    /// The SAME 73-operator content stream as
    /// <c>Excise.Core.Tests/Content/OperatorCoverageTests.cs::
    /// AuthoritativeOperatorInventory_AllStandardOperators_ParseAndRoundTrip</c>
    /// (kept byte-identical deliberately -- one inventory, not two that could
    /// drift), wrapped in a minimal real PDF and round-tripped through excise,
    /// with an INDEPENDENT tool (qpdf) confirming every operator token is
    /// still present in the content stream afterward -- not excise's own
    /// parser checking excise's own writer, which is all the existing
    /// AuthoritativeOperatorInventory test can ever prove (kind=unit, cited
    /// for every operator's parse/preserve/write mode; this is the
    /// kind=differential counterpart).
    ///
    /// One test genuinely covers all 73 operators' parse/preserve/write
    /// claims in a single execution -- the same "one test, cited broadly"
    /// shape the existing self-test already uses, just with an oracle this
    /// time. Undefined resource names (/GS1, /Sh1, /Im1, /F1, /CS0, /CS1)
    /// are deliberately left undeclared in Resources: this checks operator
    /// SURVIVAL in the content-stream bytes, not resource resolution or
    /// rendering, and qpdf's structural check does not require resources to
    /// resolve.
    /// </summary>
    [Fact]
    public void AuthoritativeOperatorInventory_SurvivesRoundTrip_ConfirmedByQpdf()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var content = string.Join("\n", new[]
        {
            "q 1 0 0 1 10 10 cm 2 w 1 J 1 j 10 M [3 2] 0 d 1.0 ri 1 i /GS1 gs",
            "10 10 m 20 20 l 30 0 40 10 50 20 c 5 5 v 6 6 y h 0 0 10 10 re",
            "S s f F f* B B* b b* W n W*",
            "/CS0 CS /CS1 cs 0.1 G 0.2 g 0.1 0.2 0.3 RG 0.4 0.5 0.6 rg " +
            "0 0 0 1 K 0 0 0 1 k 0.5 SC 0.5 SCN 0.5 sc 0.5 scn",
            "/Sh1 sh",
            "BT /F1 12 Tf 14 TL 1 Tc 2 Tw 100 Tz 0 Tr 1 Ts 10 20 Td 5 6 TD " +
            "1 0 0 1 7 8 Tm T* (a) Tj [(b) -10 (c)] TJ (d) ' 1 2 (e) \" ET",
            "/P <</MCID 0>> BDC /Span BMC EMC EMC /Pt 1 MP /Tg /Val DP BX /Unknown EX",
            "750 0 d0 750 0 0 0 700 700 d1",
            "/Im1 Do",
            "Q",
        });

        var expectedOperators = new[]
        {
            "q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re",
            "S","s","f","F","f*","B","B*","b","b*","W","n","W*",
            "CS","cs","G","g","RG","rg","K","k","SC","SCN","sc","scn",
            "sh",
            "BT","Tf","TL","Tc","Tw","Tz","Tr","Ts","Td","TD","Tm","T*","Tj","TJ","'","\"","ET",
            "BDC","BMC","EMC","MP","DP","BX","EX",
            "d0","d1","Do","Q",
        };

        var contentBytes = Encoding.ASCII.GetBytes(content);
        var pdf = Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        });

        var before = WriteTemp(pdf);
        var beforeTokens = QpdfContentStreamTokens(before);
        foreach (var op in expectedOperators)
            beforeTokens.Should().Contain(op,
                $"guard: qpdf's own decompressed view of the fixture must contain '{op}' before excise touches it");

        byte[] saved;
        using (var doc = PdfDocument.Open(pdf))
            saved = doc.SaveToBytes();

        var after = WriteTemp(saved);
        QpdfReferenceTool.Check(after)?.Success.Should().BeTrue(
            "qpdf must independently accept what excise wrote as structurally valid");

        var afterTokens = QpdfContentStreamTokens(after);
        foreach (var op in expectedOperators)
            afterTokens.Should().Contain(op,
                $"operator '{op}' must survive an open-save round trip, confirmed by qpdf's own decompressed " +
                "view of the saved content stream -- not by excise re-parsing its own output");
    }

    /// <summary>Every whitespace-delimited token in qpdf's decompressed content-stream
    /// dump of page 1 -- an independent view of what operators/operands the saved
    /// file actually contains, not excise's own tokenizer.</summary>
    private static HashSet<string> QpdfContentStreamTokens(string pdfPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("qpdf")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--qdf");
        psi.ArgumentList.Add("--object-streams=disable");
        psi.ArgumentList.Add(pdfPath);
        psi.ArgumentList.Add("-");

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        return stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static SKBitmap RenderWithExcise(string path, int pageHeightPoints)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private static bool IsInk(SKBitmap bmp, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return false;
        var c = bmp.GetPixel(x, y);
        return c.Red < 240 || c.Green < 240 || c.Blue < 240;
    }

    private static bool AnyInk(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y)) return true;
        return false;
    }

    /// <summary>Count of columns in [<paramref name="xMin"/>, <paramref name="xMax"/>) that have
    /// any inked pixel within rows [<paramref name="yMin"/>, <paramref name="yMax"/>).</summary>
    private static int InkedColumnCount(SKBitmap bmp, int xMin, int xMax, int yMin, int yMax)
    {
        int count = 0;
        for (int x = Math.Max(0, xMin); x < Math.Min(bmp.Width, xMax); x++)
        {
            for (int y = Math.Max(0, yMin); y < Math.Min(bmp.Height, yMax); y++)
            {
                if (IsInk(bmp, x, y)) { count++; break; }
            }
        }
        return count;
    }

    private static int RightmostInkedColumn(SKBitmap bmp, int yMin, int yMax)
    {
        for (int x = bmp.Width - 1; x >= 0; x--)
            for (int y = Math.Max(0, yMin); y < Math.Min(bmp.Height, yMax); y++)
                if (IsInk(bmp, x, y)) return x;
        return -1;
    }

    /// <summary>Width of the overall ink bounding box across the whole bitmap.</summary>
    private static int InkWidth(SKBitmap bmp)
    {
        int minX = bmp.Width, maxX = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y)) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
        return maxX < 0 ? 0 : maxX - minX + 1;
    }

    /// <summary>Topmost inked row within [<paramref name="yMin"/>, <paramref name="yMax"/>), or -1 if none.</summary>
    private static int TopInkedRow(SKBitmap bmp, int yMin, int yMax)
    {
        for (int y = Math.Max(0, yMin); y < Math.Min(bmp.Height, yMax); y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y)) return y;
        return -1;
    }

    /// <summary>Average colour over a small patch centred at (x, y), avoiding single-pixel AA noise.</summary>
    private static (int R, int G, int B) SampleColor(SKBitmap bmp, int x, int y)
    {
        long r = 0, g = 0, b = 0; int n = 0;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
                var c = bmp.GetPixel(px, py);
                r += c.Red; g += c.Green; b += c.Blue; n++;
            }
        return n == 0 ? (255, 255, 255) : ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static void AssertPatchColorMatches(
        SKBitmap excise, SKBitmap mutool, SKBitmap cairo, int x, int y, string label,
        int expectedR, int expectedG, int expectedB)
    {
        var mCol = SampleColor(mutool, x, y);
        var cCol = SampleColor(cairo, x, y);
        var eCol = SampleColor(excise, x, y);

        // Guard: the oracles themselves must show roughly the expected colour,
        // or this row would prove nothing about excise.
        mCol.R.Should().BeInRange(expectedR - 30, expectedR + 30, $"mutool {label}: guard on R");
        mCol.G.Should().BeInRange(expectedG - 30, expectedG + 30, $"mutool {label}: guard on G");
        mCol.B.Should().BeInRange(expectedB - 30, expectedB + 30, $"mutool {label}: guard on B");

        eCol.R.Should().BeInRange(expectedR - 30, expectedR + 30, $"excise {label}: R must match the resolved colour");
        eCol.G.Should().BeInRange(expectedG - 30, expectedG + 30, $"excise {label}: G must match the resolved colour");
        eCol.B.Should().BeInRange(expectedB - 30, expectedB + 30, $"excise {label}: B must match the resolved colour");

        Math.Abs(eCol.R - mCol.R).Should().BeLessThan(35, $"{label}: excise R must agree with mutool's independent render");
        Math.Abs(eCol.R - cCol.R).Should().BeLessThan(35, $"{label}: excise R must agree with pdftocairo's independent render");
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-opverify-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { /* best effort */ } }
        GC.SuppressFinalize(this);
    }
}
