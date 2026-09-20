using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Second batch of independent-oracle evidence for content-stream operators
/// (<c>sections/operators.json</c> <c>pdf.20.content.operator-*</c> and their
/// twin <c>sections/renderer-requirements.json</c> <c>pdf20.op.*</c> rows).
///
/// Every capability covered here was graded <c>implemented</c>: its only
/// executable contract was a excise-renders-excise unit test. Per CLAUDE.md's
/// no-self-oracle rule that proves internal consistency, not correctness, so
/// each test below renders a minimal fixture isolating ONE operator's
/// spec-mandated visual effect with excise AND with <c>mutool draw</c> (and
/// <c>pdftocairo</c> where a second opinion is cheap), then compares the
/// oracle-observable quantity.
///
/// <para>Three rules every test here follows, learned from the defects
/// CLAUDE.md records:</para>
/// <list type="bullet">
/// <item>The property is asserted about the ORACLE first ("fixture sanity").
/// If mutool does not show the effect either, the fixture proves nothing
/// about the operator and a green result would be inert (#1527).</item>
/// <item>Where the operator's effect is only observable as a DIFFERENCE
/// (line width, text rise, a kern), two fixtures differing in exactly that
/// operand are rendered and the DELTA is compared across engines — which
/// survives platform font substitution and Skia's own scan conversion
/// (#1011).</item>
/// <item>A negative control is included wherever "the operator did nothing"
/// would otherwise look identical to "the operator worked": <c>n</c> is
/// compared against <c>f</c>, <c>b</c> against <c>B</c>, <c>BDC</c> against
/// no marked content at all.</item>
/// </list>
/// </summary>
public partial class OperatorVerificationParityTests
{
    // ── w — operator-004 / renderer.requirement-026 ─────────────────────────

    /// <summary>
    /// §8.4.3.2: <c>w</c> sets the stroke width in user space, so the inked
    /// band around a horizontal line must grow by exactly the width increase.
    /// Rendered at 72 dpi one point is one pixel, so a 2pt line and a 14pt
    /// line differ by 12px of ink height — measured independently by mutool
    /// and by excise, and the two measurements must agree.
    /// </summary>
    [Fact]
    public void LineWidth_w_ThickensTheStrokedBand_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var thin = WriteTemp(StrokedLineFixture("2 w"));
        var thick = WriteTemp(StrokedLineFixture("14 w"));

        using var muThin = MutoolReferenceRenderer.RenderPage(thin, 1, Dpi);
        using var muThick = MutoolReferenceRenderer.RenderPage(thick, 1, Dpi);
        Assert.SkipWhen(muThin == null || muThick == null, "mutool could not render the line-width fixtures");

        int mutoolDelta = InkHeight(muThick!) - InkHeight(muThin!);
        mutoolDelta.Should().BeInRange(8, 16,
            "guard: mutool must show the 14pt stroke ~12px taller than the 2pt one, or this fixture does not test `w`");

        using var exThin = RenderWithExcise(thin, 200);
        using var exThick = RenderWithExcise(thick, 200);
        int exciseDelta = InkHeight(exThick) - InkHeight(exThin);

        Math.Abs(exciseDelta - mutoolDelta).Should().BeLessThanOrEqualTo(2,
            $"`w` must widen the stroked band by the requested amount: excise measured {exciseDelta}px, mutool {mutoolDelta}px");
    }

    private static byte[] StrokedLineFixture(string widthOp) =>
        PageFixture($"0 G {widthOp} 20 100 m 180 100 l S", 200, 200);

    // ── M — operator-007 ────────────────────────────────────────────────────

    /// <summary>
    /// §8.4.3.5: at a sharp join the miter length divided by the line width is
    /// compared against the miter limit; above the limit the join is converted
    /// to a bevel. The fixture's apex has a miter ratio of ~2.54, so
    /// <c>10 M</c> keeps the spike and <c>1 M</c> clamps it — the topmost
    /// inked row moves DOWN by roughly (2.54-1)·w/2 ≈ 9px. A renderer that
    /// ignored <c>M</c> entirely would show the same top row for both.
    /// </summary>
    [Fact]
    public void MiterLimit_M_ClampsASharpJoinToABevel_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var spike = WriteTemp(MiterJoinFixture("10 M"));
        var bevel = WriteTemp(MiterJoinFixture("1 M"));

        using var muSpike = MutoolReferenceRenderer.RenderPage(spike, 1, Dpi);
        using var muBevel = MutoolReferenceRenderer.RenderPage(bevel, 1, Dpi);
        Assert.SkipWhen(muSpike == null || muBevel == null, "mutool could not render the miter-limit fixtures");

        int mutoolClamp = TopInkedRow(muBevel!, 0, 200) - TopInkedRow(muSpike!, 0, 200);
        mutoolClamp.Should().BeGreaterThan(3,
            "guard: mutool must show the low miter limit pulling the apex down, or this fixture does not test `M`");

        using var exSpike = RenderWithExcise(spike, 200);
        using var exBevel = RenderWithExcise(bevel, 200);
        int exciseClamp = TopInkedRow(exBevel, 0, 200) - TopInkedRow(exSpike, 0, 200);

        Math.Abs(exciseClamp - mutoolClamp).Should().BeLessThanOrEqualTo(3,
            $"`M` must clamp the miter to a bevel at the same threshold mutool uses: excise pulled the apex down {exciseClamp}px, mutool {mutoolClamp}px");
    }

    private static byte[] MiterJoinFixture(string limitOp) =>
        PageFixture($"0 G 12 w 0 j {limitOp} 40 20 m 100 160 l 160 20 l S", 200, 200);

    // ── ri, i — operator-009 / -010, requirement-031 / -032 ─────────────────

    /// <summary>
    /// §8.6.5.8 (<c>ri</c>) and §10.7.2 (<c>i</c>) are hints: a renderer must
    /// accept them and must not let them change DeviceRGB output. The two
    /// fixtures differ only by <c>/RelativeColorimetric ri 0.5 i</c> in front
    /// of an identical fill. Both engines must paint the identical colour for
    /// both fixtures — and, critically, excise must still PAINT (an operand
    /// mis-parse that swallowed the following <c>rg</c> would show up as the
    /// wrong colour or no ink at all, which is what this pins).
    /// </summary>
    [Fact]
    public void RenderingIntent_ri_AndFlatness_i_AreOutputNeutral_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string fill = "0.2 0.4 0.8 rg 40 40 120 120 re f";
        var plain = WriteTemp(PageFixture(fill, 200, 200));
        var hinted = WriteTemp(PageFixture("/RelativeColorimetric ri 0.5 i " + fill, 200, 200));

        using var muPlain = MutoolReferenceRenderer.RenderPage(plain, 1, Dpi);
        using var muHinted = MutoolReferenceRenderer.RenderPage(hinted, 1, Dpi);
        Assert.SkipWhen(muPlain == null || muHinted == null, "mutool could not render the ri/i fixtures");

        var muPlainColour = SampleColor(muPlain!, 100, 100);
        var muHintedColour = SampleColor(muHinted!, 100, 100);
        muPlainColour.B.Should().BeGreaterThan(muPlainColour.R + 40,
            "guard: mutool must paint the fixture's predominantly-blue fill");
        muHintedColour.Should().Be(muPlainColour,
            "guard: mutool treats `ri`/`i` as output-neutral, so its two renders must be identical");

        using var exPlain = RenderWithExcise(plain, 200);
        using var exHinted = RenderWithExcise(hinted, 200);
        var exPlainColour = SampleColor(exPlain, 100, 100);
        var exHintedColour = SampleColor(exHinted, 100, 100);

        exHintedColour.Should().Be(exPlainColour,
            "`ri` and `i` are rendering hints: they must not change the painted colour");
        Math.Abs(exHintedColour.R - muHintedColour.R).Should().BeLessThan(20, "excise must agree with mutool on R");
        Math.Abs(exHintedColour.G - muHintedColour.G).Should().BeLessThan(20, "excise must agree with mutool on G");
        Math.Abs(exHintedColour.B - muHintedColour.B).Should().BeLessThan(20, "excise must agree with mutool on B");
    }

    // ── gs — operator-011, requirement-033, requirement-005 ─────────────────

    /// <summary>
    /// §8.4.5 Table 58: <c>gs</c> loads named graphics-state parameters.
    /// <c>/ca</c> (constant fill alpha) and <c>/LW</c> (line width) are the two
    /// with the most direct visual consequence, and both are asserted here so
    /// a renderer that merely looked the name up without applying the entries
    /// cannot pass. The 0.5-alpha black square must composite to mid grey over
    /// the white page — mutool computes the same value independently — and the
    /// <c>/LW 14</c> stroke must be as thick as an explicit <c>14 w</c>.
    /// </summary>
    [Fact]
    public void ExtGState_gs_AppliesFillAlphaAndLineWidth_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content =
            "0 0 0 rg 20 130 160 50 re f " +
            "q /GS1 gs 20 40 160 50 re f Q";
        var alphaPath = WriteTemp(ResourcePageFixture(
            content, 200, 200,
            "/ExtGState << /GS1 5 0 R >>",
            "5 0 obj\n<< /Type /ExtGState /ca 0.5 >>\nendobj\n"));

        using var muAlpha = MutoolReferenceRenderer.RenderPage(alphaPath, 1, Dpi);
        Assert.SkipWhen(muAlpha == null, "mutool could not render the ExtGState alpha fixture");

        var muOpaque = SampleColor(muAlpha!, 100, 45);   // y=155pt -> row 45
        var muHalf = SampleColor(muAlpha!, 100, 135);    // y=65pt  -> row 135
        muOpaque.R.Should().BeLessThan(60, "guard: mutool's opaque square must be black");
        muHalf.R.Should().BeInRange(100, 180, "guard: mutool must composite the /ca 0.5 square to mid grey");

        using var exAlpha = RenderWithExcise(alphaPath, 200);
        var exOpaque = SampleColor(exAlpha, 100, 45);
        var exHalf = SampleColor(exAlpha, 100, 135);

        exOpaque.R.Should().BeLessThan(60, "the square painted without `gs` must stay opaque black");
        Math.Abs(exHalf.R - muHalf.R).Should().BeLessThan(25,
            $"`gs` must apply /ca: excise composited to {exHalf.R}, mutool to {muHalf.R}");

        // /LW through gs must equal an explicit `w` of the same value.
        var viaGs = WriteTemp(ResourcePageFixture(
            "0 G /GS1 gs 20 100 m 180 100 l S", 200, 200,
            "/ExtGState << /GS1 5 0 R >>",
            "5 0 obj\n<< /Type /ExtGState /LW 14 >>\nendobj\n"));
        using var muLw = MutoolReferenceRenderer.RenderPage(viaGs, 1, Dpi);
        Assert.SkipWhen(muLw == null, "mutool could not render the ExtGState line-width fixture");
        int mutoolLw = InkHeight(muLw!);
        mutoolLw.Should().BeInRange(11, 17, "guard: mutool must apply /LW 14 from the ExtGState");

        using var exLw = RenderWithExcise(viaGs, 200);
        Math.Abs(InkHeight(exLw) - mutoolLw).Should().BeLessThanOrEqualTo(2,
            $"`gs` must apply /LW: excise stroked {InkHeight(exLw)}px, mutool {mutoolLw}px");
    }

    // ── c — operator-014, requirement-036 ───────────────────────────────────

    /// <summary>
    /// §8.5.2.2: <c>c</c> takes two control points that are NOT on the curve.
    /// The fixture's control points sit at y=180 but the curve's own apex is
    /// at y=145 (the cubic's midpoint is (P0+3P1+3P2+P3)/8). A renderer that
    /// mistook the control points for on-curve vertices would ink up to row
    /// 20 instead of row 55. Both engines must place the apex at the same row.
    /// </summary>
    [Fact]
    public void CubicBezier_c_ApexFollowsTheCurveNotTheControlPoints_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture("0 0 0 rg 40 40 m 40 180 160 180 160 40 c f", 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the Bézier fixture");

        int mutoolTop = TopInkedRow(mutool!, 0, 200);
        mutoolTop.Should().BeInRange(45, 65,
            "guard: mutool must place the curve's apex near y=145pt (row 55), not at the control points' y=180pt");

        using var excise = RenderWithExcise(path, 200);
        int exciseTop = TopInkedRow(excise, 0, 200);

        Math.Abs(exciseTop - mutoolTop).Should().BeLessThanOrEqualTo(3,
            $"`c` must interpolate the cubic: excise's apex row {exciseTop}, mutool's {mutoolTop}");
    }

    // ── re — operator-018, requirement-040 ──────────────────────────────────

    /// <summary>
    /// §8.5.2.1: <c>x y w h re</c> appends a complete rectangular subpath with
    /// its lower-left corner at (x,y). The filled box must therefore occupy
    /// exactly x..x+w by y..y+h in user space; at 72 dpi that is a pixel box
    /// whose four edges both engines must report identically. Asserting the
    /// absolute box (not just agreement) also catches the two engines sharing
    /// an origin-flip mistake.
    /// </summary>
    [Fact]
    public void Rectangle_re_PlacesTheBoxAtTheRequestedCorner_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture("0 0 0 rg 30 50 100 70 re f", 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the rectangle fixture");

        var m = InkBox(mutool!);
        m.MinX.Should().BeInRange(29, 31, "guard: mutool must put the box's left edge at x=30pt");
        m.MinY.Should().BeInRange(79, 81, "guard: mutool must put the box's top edge at row 200-120=80");
        m.Width.Should().BeInRange(99, 101, "guard: mutool's box must be 100pt wide");
        m.Height.Should().BeInRange(69, 71, "guard: mutool's box must be 70pt tall");

        using var excise = RenderWithExcise(path, 200);
        var e = InkBox(excise);

        Math.Abs(e.MinX - m.MinX).Should().BeLessThanOrEqualTo(1, "left edge must match mutool's");
        Math.Abs(e.MinY - m.MinY).Should().BeLessThanOrEqualTo(1, "top edge must match mutool's");
        Math.Abs(e.Width - m.Width).Should().BeLessThanOrEqualTo(1, "width must match mutool's");
        Math.Abs(e.Height - m.Height).Should().BeLessThanOrEqualTo(1, "height must match mutool's");
    }

    // ── s — operator-020, requirement-042 ───────────────────────────────────

    /// <summary>
    /// §8.5.3.1: <c>s</c> is <c>h S</c> — it closes the subpath before
    /// stroking. The fixture is an open inverted V; under <c>S</c> the bottom
    /// edge between the two endpoints is never drawn, under <c>s</c> it is.
    /// The midpoint of that closing edge is the discriminator, and mutool must
    /// agree on both halves of the comparison.
    /// </summary>
    [Fact]
    public void CloseAndStroke_s_DrawsTheClosingSegmentUnlike_S_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var open = WriteTemp(OpenVFixture("S"));
        var closed = WriteTemp(OpenVFixture("s"));

        using var muOpen = MutoolReferenceRenderer.RenderPage(open, 1, Dpi);
        using var muClosed = MutoolReferenceRenderer.RenderPage(closed, 1, Dpi);
        Assert.SkipWhen(muOpen == null || muClosed == null, "mutool could not render the open/closed stroke fixtures");

        // (100, 160) is the midpoint of the closing edge y=40pt.
        IsInk(muOpen!, 100, 160).Should().BeFalse("guard: under `S` mutool must leave the unclosed edge blank");
        IsInk(muClosed!, 100, 160).Should().BeTrue("guard: under `s` mutool must draw the closing edge");

        using var exOpen = RenderWithExcise(open, 200);
        using var exClosed = RenderWithExcise(closed, 200);

        IsInk(exOpen, 100, 160).Should().BeFalse(
            "`S` must not close the subpath — a renderer that closed it would ink the bottom edge");
        IsInk(exClosed, 100, 160).Should().BeTrue(
            "`s` must close the subpath before stroking, drawing the segment back to the start point");
    }

    private static byte[] OpenVFixture(string paintOp) =>
        PageFixture($"0 G 6 w 40 40 m 100 160 l 160 40 l {paintOp}", 200, 200);

    // ── F — operator-022, requirement-044 ───────────────────────────────────

    /// <summary>
    /// §8.5.3.1 Table 60: <c>F</c> is an obsolete synonym for <c>f</c> that a
    /// conforming reader shall still honour. The two fixtures differ only in
    /// the operator's case; mutool paints both identically and so must excise
    /// — a reader that dropped <c>F</c> as unknown would produce an empty page.
    /// </summary>
    [Fact]
    public void UppercaseFill_F_IsASynonymForLowercase_f_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var lower = WriteTemp(PageFixture("0 0 0 rg 40 40 120 120 re f", 200, 200));
        var upper = WriteTemp(PageFixture("0 0 0 rg 40 40 120 120 re F", 200, 200));

        using var muUpper = MutoolReferenceRenderer.RenderPage(upper, 1, Dpi);
        Assert.SkipWhen(muUpper == null, "mutool could not render the `F` fixture");
        var muBox = InkBox(muUpper!);
        muBox.Width.Should().BeInRange(118, 122, "guard: mutool must fill the 120pt square for `F`");

        using var exLower = RenderWithExcise(lower, 200);
        using var exUpper = RenderWithExcise(upper, 200);

        InkBox(exUpper).Should().Be(InkBox(exLower),
            "`F` must paint exactly what `f` paints");
        var e = InkBox(exUpper);
        Math.Abs(e.MinX - muBox.MinX).Should().BeLessThanOrEqualTo(1, "excise's `F` box must match mutool's");
        Math.Abs(e.Width - muBox.Width).Should().BeLessThanOrEqualTo(1, "excise's `F` box must match mutool's");
    }

    // ── B / B* — operator-024 / -025, requirement-046 / -047 ────────────────

    /// <summary>
    /// §8.5.3.1: <c>B</c> fills with the nonzero rule then strokes;
    /// <c>B*</c> fills with the even-odd rule then strokes. On the shared
    /// donut path (two same-wound rectangles) the fill rule decides whether
    /// the inner square is solid, and the trailing stroke — which neither
    /// <c>f</c> nor <c>f*</c> would draw — must appear on the inner border in
    /// BOTH cases. Asserting the stroke is what separates this from the
    /// already-verified <c>f</c>/<c>f*</c> rows.
    /// </summary>
    [Fact]
    public void FillThenStroke_B_And_BStar_DifferOnlyInTheFillRule_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var nonzero = WriteTemp(StrokedDonutFixture("B"));
        var evenOdd = WriteTemp(StrokedDonutFixture("B*"));

        using var muNonzero = MutoolReferenceRenderer.RenderPage(nonzero, 1, Dpi);
        using var muEvenOdd = MutoolReferenceRenderer.RenderPage(evenOdd, 1, Dpi);
        Assert.SkipWhen(muNonzero == null || muEvenOdd == null, "mutool could not render the B/B* fixtures");

        AssertDonutFillRule(muNonzero!, muEvenOdd!, "mutool");

        using var exNonzero = RenderWithExcise(nonzero, 200);
        using var exEvenOdd = RenderWithExcise(evenOdd, 200);
        AssertDonutFillRule(exNonzero, exEvenOdd, "excise");
    }

    // ── b / b* — operator-026 / -027, requirement-048 / -049 ────────────────

    /// <summary>
    /// §8.5.3.1: <c>b</c> is <c>h B</c>. On an OPEN path the difference from
    /// <c>B</c> is visible: both fill the implicitly-closed region, but only
    /// <c>b</c> strokes the closing segment. The fixture strokes in blue over
    /// a black fill so the closing segment is identifiable by colour rather
    /// than by mere presence of ink.
    /// </summary>
    [Fact]
    public void CloseFillStroke_b_StrokesTheClosingSegmentUnlike_B_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var withoutClose = WriteTemp(OpenTriangleFixture("B"));
        var withClose = WriteTemp(OpenTriangleFixture("b"));

        using var muOpen = MutoolReferenceRenderer.RenderPage(withoutClose, 1, Dpi);
        using var muClosed = MutoolReferenceRenderer.RenderPage(withClose, 1, Dpi);
        Assert.SkipWhen(muOpen == null || muClosed == null, "mutool could not render the b/B fixtures");

        // (100, 160) is on the bottom edge y=40pt, midway between the endpoints.
        IsBlueish(SampleColor(muOpen!, 100, 160)).Should().BeFalse(
            "guard: under `B` mutool must not stroke the unclosed bottom edge");
        IsBlueish(SampleColor(muClosed!, 100, 160)).Should().BeTrue(
            "guard: under `b` mutool must stroke the closing segment in the stroke colour");

        using var exOpen = RenderWithExcise(withoutClose, 200);
        using var exClosed = RenderWithExcise(withClose, 200);

        IsBlueish(SampleColor(exOpen, 100, 160)).Should().BeFalse(
            "`B` must not close the subpath, so the bottom edge carries only the black fill");
        IsBlueish(SampleColor(exClosed, 100, 160)).Should().BeTrue(
            "`b` must close the subpath before filling and stroking, painting the closing segment in the stroke colour");
    }

    /// <summary>
    /// <c>b*</c> is <c>h B*</c>: the same close-fill-stroke as <c>b</c> but
    /// with the even-odd fill rule, so on the donut path the inner square is
    /// a hole. Verified against mutool exactly as the <c>B</c>/<c>B*</c> pair
    /// is.
    /// </summary>
    [Fact]
    public void CloseFillStroke_bStar_UsesTheEvenOddRuleUnlike_b_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var nonzero = WriteTemp(StrokedDonutFixture("b"));
        var evenOdd = WriteTemp(StrokedDonutFixture("b*"));

        using var muNonzero = MutoolReferenceRenderer.RenderPage(nonzero, 1, Dpi);
        using var muEvenOdd = MutoolReferenceRenderer.RenderPage(evenOdd, 1, Dpi);
        Assert.SkipWhen(muNonzero == null || muEvenOdd == null, "mutool could not render the b/b* fixtures");

        AssertDonutFillRule(muNonzero!, muEvenOdd!, "mutool");

        using var exNonzero = RenderWithExcise(nonzero, 200);
        using var exEvenOdd = RenderWithExcise(evenOdd, 200);
        AssertDonutFillRule(exNonzero, exEvenOdd, "excise");
    }

    private static byte[] StrokedDonutFixture(string paintOp) =>
        PageFixture($"0 0 0 rg 0 0 1 RG 4 w {DonutPath}{paintOp}", 200, 200);

    private static byte[] OpenTriangleFixture(string paintOp) =>
        PageFixture($"0 0 0 rg 0 0 1 RG 6 w 40 40 m 100 160 l 160 40 l {paintOp}", 200, 200);

    /// <summary>
    /// Both halves of a nonzero/even-odd painting pair, asserted about one
    /// engine: the nonzero render has a solid centre, the even-odd render has
    /// a hole, and BOTH carry the stroke on the inner square's border (which
    /// is what makes this a B/b assertion rather than an f/f* one).
    /// </summary>
    private static void AssertDonutFillRule(SKBitmap nonzero, SKBitmap evenOdd, string who)
    {
        IsInk(nonzero, 100, 100).Should().BeTrue(
            $"{who}: under the nonzero rule the inner square's winding number is 2, so the donut fills solid");
        IsInk(evenOdd, 100, 100).Should().BeFalse(
            $"{who}: under the even-odd rule the inner square is a hole");

        // x=70pt is the inner rectangle's left border; the stroke straddles it.
        IsBlueish(SampleColor(evenOdd, 70, 100)).Should().BeTrue(
            $"{who}: the trailing stroke must paint the inner border — without it this fixture would only prove `f*`");
        IsBlueish(SampleColor(nonzero, 70, 100)).Should().BeTrue(
            $"{who}: the trailing stroke must paint the inner border over the solid fill too");
    }

    private static bool IsBlueish((int R, int G, int B) c) => c.B > c.R + 40 && c.B > c.G + 40;

    // ── n — operator-028 ────────────────────────────────────────────────────

    /// <summary>
    /// §8.5.3.1: <c>n</c> ends the path without painting it. With a black fill
    /// AND a thick black stroke colour set, a renderer that treated <c>n</c>
    /// as either <c>f</c> or <c>S</c> would ink the page; the correct result
    /// is a completely blank raster. The <c>f</c> control proves the fixture
    /// would otherwise have painted, so "blank" cannot come from a broken
    /// fixture.
    /// </summary>
    [Fact]
    public void NoOpPathPaint_n_LeavesThePageBlank_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var noop = WriteTemp(PaintedSquareFixture("n"));
        var control = WriteTemp(PaintedSquareFixture("f"));

        using var muNoop = MutoolReferenceRenderer.RenderPage(noop, 1, Dpi);
        using var muControl = MutoolReferenceRenderer.RenderPage(control, 1, Dpi);
        Assert.SkipWhen(muNoop == null || muControl == null, "mutool could not render the `n` fixtures");

        AnyInk(muControl!).Should().BeTrue("guard: the control fixture must paint, or `n` proves nothing");
        AnyInk(muNoop!).Should().BeFalse("guard: mutool must treat `n` as a no-op paint");

        using var exControl = RenderWithExcise(control, 200);
        using var exNoop = RenderWithExcise(noop, 200);

        AnyInk(exControl).Should().BeTrue("the control fixture must paint in excise too");
        AnyInk(exNoop).Should().BeFalse(
            "`n` must consume the path without filling or stroking it");
    }

    private static byte[] PaintedSquareFixture(string paintOp) =>
        PageFixture($"0 0 0 rg 0 G 10 w 40 40 120 120 re {paintOp}", 200, 200);

    // ── K / k — operator-041 / -042, requirement-063 / -064 ─────────────────

    /// <summary>
    /// §8.6.4.4: <c>k</c> (fill) and <c>K</c> (stroke) set DeviceCMYK. The
    /// exact RGB a renderer produces depends on its CMYK conversion — Skia's
    /// and MuPDF's differ — so this asserts the CHANNEL ORDERING that any
    /// correct conversion must produce for a pure primary: CMYK(0,1,1,0) is
    /// red (R dominant) and CMYK(1,0,0,0) is cyan (G and B dominant). A
    /// renderer that mixed up the operand order, or dropped the black
    /// component, fails on ordering alone.
    /// </summary>
    [Fact]
    public void CmykColor_k_And_K_ProduceTheRightHue_MatchesMutoolOrdering()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content =
            "0 1 1 0 k 20 110 160 70 re f " +
            "1 0 0 0 K 14 w 40 40 120 40 re S";
        var path = WriteTemp(PageFixture(content, 200, 200));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the CMYK fixture");

        var muFill = SampleColor(mutool!, 100, 55);    // inside the k-filled band
        var muStroke = SampleColor(mutool!, 40, 140);  // on the K-stroked border
        muFill.R.Should().BeGreaterThan(muFill.G + 60, "guard: mutool must render CMYK(0,1,1,0) as red");
        muFill.R.Should().BeGreaterThan(muFill.B + 60, "guard: mutool must render CMYK(0,1,1,0) as red");
        muStroke.G.Should().BeGreaterThan(muStroke.R + 60, "guard: mutool must render CMYK(1,0,0,0) as cyan");
        muStroke.B.Should().BeGreaterThan(muStroke.R + 60, "guard: mutool must render CMYK(1,0,0,0) as cyan");

        using var excise = RenderWithExcise(path, 200);
        var exFill = SampleColor(excise, 100, 55);
        var exStroke = SampleColor(excise, 40, 140);

        exFill.R.Should().BeGreaterThan(exFill.G + 60, "`k` must render CMYK(0,1,1,0) as red, as mutool does");
        exFill.R.Should().BeGreaterThan(exFill.B + 60, "`k` must render CMYK(0,1,1,0) as red, as mutool does");
        exStroke.G.Should().BeGreaterThan(exStroke.R + 60, "`K` must render CMYK(1,0,0,0) as cyan, as mutool does");
        exStroke.B.Should().BeGreaterThan(exStroke.R + 60, "`K` must render CMYK(1,0,0,0) as cyan, as mutool does");
    }

    // ── Ts — operator-056, requirement-078 ──────────────────────────────────

    /// <summary>
    /// §9.4.4: <c>Ts</c> raises the baseline by its operand in unscaled text
    /// units. Two fixtures identical but for <c>0 Ts</c> / <c>20 Ts</c> must
    /// differ in topmost inked row by 20px at 72 dpi; the delta is compared
    /// across engines so platform font substitution (which changes the cap
    /// height, not the rise) cannot affect the result.
    /// </summary>
    [Fact]
    public void TextRise_Ts_LiftsTheBaseline_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var flat = WriteTemp(RiseFixture(0));
        var raised = WriteTemp(RiseFixture(20));

        using var muFlat = MutoolReferenceRenderer.RenderPage(flat, 1, Dpi);
        using var muRaised = MutoolReferenceRenderer.RenderPage(raised, 1, Dpi);
        Assert.SkipWhen(muFlat == null || muRaised == null, "mutool could not render the text-rise fixtures");

        int muFlatTop = TopInkedRow(muFlat!, 0, 120);
        int muRaisedTop = TopInkedRow(muRaised!, 0, 120);
        Assert.SkipWhen(muFlatTop < 0 || muRaisedTop < 0, "mutool painted no glyph in one of the rise fixtures");
        int mutoolRise = muFlatTop - muRaisedTop;
        mutoolRise.Should().BeInRange(16, 24, "guard: mutool must lift the glyph by the 20pt rise");

        using var exFlat = RenderWithExcise(flat, 120);
        using var exRaised = RenderWithExcise(raised, 120);
        int exFlatTop = TopInkedRow(exFlat, 0, 120);
        int exRaisedTop = TopInkedRow(exRaised, 0, 120);
        exFlatTop.Should().BeGreaterThanOrEqualTo(0, "excise must paint the unraised glyph");
        exRaisedTop.Should().BeGreaterThanOrEqualTo(0, "excise must paint the raised glyph");

        Math.Abs((exFlatTop - exRaisedTop) - mutoolRise).Should().BeLessThanOrEqualTo(3,
            $"`Ts` must lift the baseline by its operand: excise lifted {exFlatTop - exRaisedTop}px, mutool {mutoolRise}px");
    }

    private static byte[] RiseFixture(int rise) =>
        PageFixture($"BT /F1 24 Tf 1 0 0 1 20 40 Tm {rise} Ts (H) Tj ET", 200, 120, ("F1", "Helvetica"));

    // ── Td — operator-057, requirement-079 ──────────────────────────────────

    /// <summary>
    /// §9.4.2: <c>tx ty Td</c> moves to the start of the next line, offset
    /// from the START OF THE CURRENT LINE — not from the current pen position.
    /// The fixture shows one glyph, applies <c>30 -40 Td</c>, and shows
    /// another; the second glyph must be 30pt right and 40pt down of the
    /// FIRST glyph's origin, which is where a pen-relative implementation
    /// would differ (it would add the first glyph's advance too). Both offsets
    /// are compared against mutool's independent measurement.
    /// </summary>
    [Fact]
    public void TextPosition_Td_OffsetsFromTheLineStart_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture(
            "BT /F1 24 Tf 1 0 0 1 20 200 Tm (H) Tj 30 -40 Td (H) Tj ET",
            200, 240, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the Td fixture");

        var mu = TwoLineOffsets(mutool!, 240);
        Assert.SkipWhen(mu == null, "mutool did not paint both Td lines where expected");
        mu!.Value.Dx.Should().BeInRange(26, 34, "guard: mutool must shift the second glyph 30pt right");
        mu.Value.Dy.Should().BeInRange(36, 44, "guard: mutool must shift the second glyph 40pt down");

        using var excise = RenderWithExcise(path, 240);
        var ex = TwoLineOffsets(excise, 240);
        ex.Should().NotBeNull("excise must paint both Td lines");

        Math.Abs(ex!.Value.Dx - mu.Value.Dx).Should().BeLessThanOrEqualTo(3,
            $"`Td`'s tx must match mutool's: excise {ex.Value.Dx}px, mutool {mu.Value.Dx}px");
        Math.Abs(ex.Value.Dy - mu.Value.Dy).Should().BeLessThanOrEqualTo(3,
            $"`Td`'s ty must match mutool's: excise {ex.Value.Dy}px, mutool {mu.Value.Dy}px");
    }

    // ── TD — operator-058, requirement-080 ──────────────────────────────────

    /// <summary>
    /// §9.4.2: <c>TD</c> is <c>Td</c> plus a side effect — it sets the leading
    /// to <c>-ty</c>. The fixture uses <c>0 -40 TD</c> and then a bare
    /// <c>T*</c>: the third line can only land 40pt below the second if
    /// <c>TD</c> stored the leading. A reader that implemented <c>TD</c> as a
    /// plain <c>Td</c> would leave the leading at its default 0 and stack the
    /// third glyph on the second.
    /// </summary>
    [Fact]
    public void TextPosition_TD_AlsoSetsTheLeading_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture(
            "BT /F1 24 Tf 1 0 0 1 20 200 Tm (H) Tj 0 -40 TD (H) Tj T* (H) Tj ET",
            200, 240, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the TD fixture");

        // Bands around the three expected baselines y=200, 160, 120 -> rows 40, 80, 120.
        int muLine2 = TopInkedRow(mutool!, 60, 100);
        int muLine3 = TopInkedRow(mutool!, 100, 140);
        Assert.SkipWhen(muLine2 < 0 || muLine3 < 0, "mutool did not paint the TD/T* lines where expected");
        muLine3.Should().BeGreaterThan(muLine2,
            "guard: mutool's `T*` must move down by the leading `TD` set — if TD did not set it, there would be no third line");

        using var excise = RenderWithExcise(path, 240);
        int exLine1 = TopInkedRow(excise, 20, 60);
        int exLine2 = TopInkedRow(excise, 60, 100);
        int exLine3 = TopInkedRow(excise, 100, 140);
        exLine1.Should().BeGreaterThanOrEqualTo(0, "excise must paint the first line");
        exLine2.Should().BeGreaterThanOrEqualTo(0, "excise must paint the `TD` line");
        exLine3.Should().BeGreaterThanOrEqualTo(0,
            "excise must paint a third line — its absence would mean `TD` did not set the leading for `T*`");

        Math.Abs((exLine3 - exLine2) - (muLine3 - muLine2)).Should().BeLessThanOrEqualTo(3,
            "the leading `TD` set must produce the same `T*` step mutool measures");
        Math.Abs((exLine2 - exLine1) - (exLine3 - exLine2)).Should().BeLessThanOrEqualTo(3,
            "`TD`'s own move and the leading it stored must be the same 40pt");
    }

    // ── Tm — operator-059, requirement-081 ──────────────────────────────────

    /// <summary>
    /// §9.4.2: <c>Tm</c> REPLACES the text matrix (it does not concatenate),
    /// and its a/d entries scale the glyphs. The fixture pair differs only in
    /// that matrix — <c>1 0 0 1</c> versus <c>2 0 0 2</c> at the same origin —
    /// so the painted glyph must be twice as tall while its left edge stays
    /// put. The height RATIO is compared across engines, which is immune to
    /// the font substitution that would break an absolute comparison.
    /// </summary>
    [Fact]
    public void TextMatrix_Tm_ScalesTheGlyphInPlace_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var unit = WriteTemp(MatrixFixture("1 0 0 1 30 40"));
        var doubled = WriteTemp(MatrixFixture("2 0 0 2 30 40"));

        using var muUnit = MutoolReferenceRenderer.RenderPage(unit, 1, Dpi);
        using var muDoubled = MutoolReferenceRenderer.RenderPage(doubled, 1, Dpi);
        Assert.SkipWhen(muUnit == null || muDoubled == null, "mutool could not render the Tm fixtures");

        var muUnitBox = InkBox(muUnit!);
        var muDoubledBox = InkBox(muDoubled!);
        muUnitBox.Height.Should().BeGreaterThan(0, "guard: mutool must paint the unscaled glyph");
        double mutoolRatio = (double)muDoubledBox.Height / muUnitBox.Height;
        mutoolRatio.Should().BeInRange(1.7, 2.3, "guard: mutool must double the glyph height under `2 0 0 2 Tm`");

        using var exUnit = RenderWithExcise(unit, 200);
        using var exDoubled = RenderWithExcise(doubled, 200);
        var exUnitBox = InkBox(exUnit);
        var exDoubledBox = InkBox(exDoubled);
        exUnitBox.Height.Should().BeGreaterThan(0, "excise must paint the unscaled glyph");

        double exciseRatio = (double)exDoubledBox.Height / exUnitBox.Height;
        exciseRatio.Should().BeApproximately(mutoolRatio, 0.25,
            $"`Tm`'s scale must match mutool's: excise scaled by {exciseRatio:F2}, mutool by {mutoolRatio:F2}");
        Math.Abs(exDoubledBox.MinX - exUnitBox.MinX).Should().BeLessThanOrEqualTo(4,
            "`Tm` sets the origin absolutely, so both glyphs must start at the same x");
    }

    private static byte[] MatrixFixture(string matrix) =>
        PageFixture($"BT /F1 24 Tf {matrix} Tm (H) Tj ET", 200, 200, ("F1", "Helvetica"));

    // ── Tj — operator-061, requirement-083 ──────────────────────────────────

    /// <summary>
    /// §9.4.3: <c>Tj</c> shows a string at the current text position. The
    /// absolute glyph shapes depend on which Helvetica each engine
    /// substitutes, so what is compared is the INK EXTENT: both engines must
    /// start the run at the text origin and must run to within 15% of the same
    /// width. A reader that dropped the operator, or that showed the string
    /// somewhere else, fails either half.
    /// </summary>
    [Fact]
    public void ShowText_Tj_PaintsTheRunAtTheTextOrigin_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture(
            "BT /F1 36 Tf 1 0 0 1 20 40 Tm (Hamburg) Tj ET", 300, 100, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the Tj fixture");

        var mu = InkBox(mutool!);
        mu.Width.Should().BeGreaterThan(80, "guard: mutool must paint a seven-glyph run at 36pt");
        mu.MinX.Should().BeInRange(15, 30, "guard: mutool must start the run at the text origin x=20pt");

        using var excise = RenderWithExcise(path, 100);
        var ex = InkBox(excise);

        ex.Width.Should().BeGreaterThan(0, "`Tj` must paint the string");
        Math.Abs(ex.MinX - mu.MinX).Should().BeLessThanOrEqualTo(6,
            $"`Tj` must start the run where mutool starts it: excise x={ex.MinX}, mutool x={mu.MinX}");
        ((double)ex.Width).Should().BeApproximately(mu.Width, mu.Width * 0.15,
            $"the painted run's width must be within 15% of mutool's: excise {ex.Width}px, mutool {mu.Width}px");
    }

    // ── TJ — operator-062, requirement-084 ──────────────────────────────────

    /// <summary>
    /// §9.4.3: a NUMBER inside a <c>TJ</c> array subtracts (n/1000)·Tfs from
    /// the pen position. The two fixtures differ only in that number — 0
    /// versus -2000, which at 24pt is a 48pt widening — so the ink-extent
    /// delta must be ~48px and must match mutool's own measurement. (This is
    /// the §9.4.3 branch that stayed wrong for months after §9.4.2 was fixed;
    /// see CLAUDE.md's Limitations entry. It is pinned here against an oracle
    /// as well as against the spec.)
    /// </summary>
    [Fact]
    public void ShowTextArray_TJ_AppliesTheArrayAdjustment_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var unkerned = WriteTemp(KernFixture(0));
        var kerned = WriteTemp(KernFixture(-2000));

        using var muUnkerned = MutoolReferenceRenderer.RenderPage(unkerned, 1, Dpi);
        using var muKerned = MutoolReferenceRenderer.RenderPage(kerned, 1, Dpi);
        Assert.SkipWhen(muUnkerned == null || muKerned == null, "mutool could not render the TJ fixtures");

        int mutoolDelta = InkWidth(muKerned!) - InkWidth(muUnkerned!);
        mutoolDelta.Should().BeInRange(40, 56,
            "guard: mutool must widen the run by (2000/1000)*24 = 48pt for the -2000 adjustment");

        using var exUnkerned = RenderWithExcise(unkerned, 100);
        using var exKerned = RenderWithExcise(kerned, 100);
        int exciseDelta = InkWidth(exKerned) - InkWidth(exUnkerned);

        Math.Abs(exciseDelta - mutoolDelta).Should().BeLessThanOrEqualTo(4,
            $"`TJ`'s adjustment must move the pen by the spec amount: excise widened {exciseDelta}px, mutool {mutoolDelta}px");
    }

    private static byte[] KernFixture(int adjustment) =>
        PageFixture($"BT /F1 24 Tf 1 0 0 1 20 40 Tm [(AB) {adjustment} (CD)] TJ ET",
            300, 100, ("F1", "Helvetica"));

    // ── ' — operator-063, requirement-085 ───────────────────────────────────

    /// <summary>
    /// §9.4.3: <c>'</c> is <c>T*</c> followed by <c>Tj</c> — it moves to the
    /// next line BEFORE showing. The fixture sets <c>40 TL</c> and shows two
    /// strings, the second with <c>'</c>; a reader that treated <c>'</c> as a
    /// bare <c>Tj</c> would paint both on one line. The line step is compared
    /// against mutool's.
    /// </summary>
    [Fact]
    public void NextLineShowText_Apostrophe_StepsDownByTheLeading_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(PageFixture(
            "BT /F1 24 Tf 40 TL 1 0 0 1 20 200 Tm (H) Tj (H) ' ET",
            200, 240, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the `'` fixture");

        var mu = TwoLineOffsets(mutool!, 240);
        Assert.SkipWhen(mu == null, "mutool did not paint both `'` lines where expected");
        mu!.Value.Dy.Should().BeInRange(36, 44, "guard: mutool must step the `'` line down by the 40pt leading");

        using var excise = RenderWithExcise(path, 240);
        var ex = TwoLineOffsets(excise, 240);
        ex.Should().NotBeNull(
            "excise must paint a second line — its absence would mean `'` showed the string without the line step");

        Math.Abs(ex!.Value.Dy - mu.Value.Dy).Should().BeLessThanOrEqualTo(3,
            $"`'` must step down by `TL`: excise {ex.Value.Dy}px, mutool {mu.Value.Dy}px");
    }

    // ── " — operator-064, requirement-086 ───────────────────────────────────

    /// <summary>
    /// §9.4.3: <c>aw ac string "</c> sets the word spacing to <c>aw</c> and
    /// the character spacing to <c>ac</c>, then behaves like <c>'</c>. Both
    /// halves are asserted: the line step (which distinguishes it from
    /// <c>Tj</c>) and the widening a nonzero <c>ac</c> must cause (which
    /// distinguishes it from a <c>'</c> that ignored its operands). The
    /// widening is compared as a DELTA against mutool, not as an absolute
    /// width, because the glyph advances come from whichever Helvetica each
    /// engine substituted.
    /// </summary>
    [Fact]
    public void NextLineShowText_Quote_SetsSpacingAndStepsDown_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var tight = WriteTemp(QuoteFixture(0, 0));
        var spaced = WriteTemp(QuoteFixture(0, 6));

        using var muTight = MutoolReferenceRenderer.RenderPage(tight, 1, Dpi);
        using var muSpaced = MutoolReferenceRenderer.RenderPage(spaced, 1, Dpi);
        Assert.SkipWhen(muTight == null || muSpaced == null, "mutool could not render the `\"` fixtures");

        var muOffsets = TwoLineOffsets(muTight!, 240);
        Assert.SkipWhen(muOffsets == null, "mutool did not paint both `\"` lines where expected");
        muOffsets!.Value.Dy.Should().BeInRange(36, 44, "guard: mutool must step the `\"` line down by the 40pt leading");

        // The second line is the one `"` shows; measure only its band.
        int muTightWidth = InkWidthInRows(muTight!, 60, 120);
        int muSpacedWidth = InkWidthInRows(muSpaced!, 60, 120);
        int mutoolWidening = muSpacedWidth - muTightWidth;
        mutoolWidening.Should().BeGreaterThan(10,
            "guard: mutool must widen the `\"` line when ac=6 — four inter-glyph gaps at 6pt each");

        using var exTight = RenderWithExcise(tight, 240);
        using var exSpaced = RenderWithExcise(spaced, 240);

        var exOffsets = TwoLineOffsets(exTight, 240);
        exOffsets.Should().NotBeNull(
            "excise must paint a second line — `\"` steps down before showing, like `'`");
        Math.Abs(exOffsets!.Value.Dy - muOffsets.Value.Dy).Should().BeLessThanOrEqualTo(3,
            $"`\"` must step down by `TL`: excise {exOffsets.Value.Dy}px, mutool {muOffsets.Value.Dy}px");

        int exciseWidening = InkWidthInRows(exSpaced, 60, 120) - InkWidthInRows(exTight, 60, 120);
        Math.Abs(exciseWidening - mutoolWidening).Should().BeLessThanOrEqualTo(6,
            $"`\"`'s ac operand must set the character spacing: excise widened the line {exciseWidening}px, mutool {mutoolWidening}px");
    }

    private static byte[] QuoteFixture(int wordSpacing, int charSpacing) =>
        PageFixture(
            $"BT /F1 24 Tf 40 TL 1 0 0 1 20 200 Tm (HH) Tj {wordSpacing} {charSpacing} (HHHHH) \" ET",
            200, 240, ("F1", "Helvetica"));

    // ── BDC / EMC — operator-068 / -069, requirement-090 / -091 ─────────────

    /// <summary>
    /// §14.6: a marked-content sequence carries no graphical meaning — content
    /// inside <c>BDC … EMC</c> must render exactly as it would without them,
    /// and the operators' operands (a tag and a property dictionary) must not
    /// be mistaken for painting operands. The two fixtures differ only by the
    /// bracketing pair and must produce identical rasters in excise, as they
    /// do in mutool.
    /// </summary>
    [Fact]
    public void MarkedContent_BDC_EMC_DoNotAlterRendering_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string paint = "0 0 0 rg 40 40 120 120 re f";
        var bare = WriteTemp(PageFixture(paint, 200, 200));
        var marked = WriteTemp(PageFixture(
            "/Span << /ActualText (marked) >> BDC " + paint + " EMC", 200, 200));

        using var muBare = MutoolReferenceRenderer.RenderPage(bare, 1, Dpi);
        using var muMarked = MutoolReferenceRenderer.RenderPage(marked, 1, Dpi);
        Assert.SkipWhen(muBare == null || muMarked == null, "mutool could not render the marked-content fixtures");

        var muBox = InkBox(muMarked!);
        muBox.Should().Be(InkBox(muBare!),
            "guard: mutool must render the marked and unmarked fixtures identically");
        muBox.Width.Should().BeGreaterThan(0, "guard: the fixture must actually paint something");

        using var exBare = RenderWithExcise(bare, 200);
        using var exMarked = RenderWithExcise(marked, 200);

        InkBox(exMarked).Should().Be(InkBox(exBare),
            "`BDC`/`EMC` are transparent to rendering: the marked fixture must raster identically to the bare one");
        var ex = InkBox(exMarked);
        Math.Abs(ex.MinX - muBox.MinX).Should().BeLessThanOrEqualTo(1, "excise's marked-content render must match mutool's");
        Math.Abs(ex.Width - muBox.Width).Should().BeLessThanOrEqualTo(1, "excise's marked-content render must match mutool's");
    }

    /// <summary>
    /// §8.11.3.3: content inside <c>/OC … BDC … EMC</c> whose optional-content
    /// group is in the default configuration's <c>/OFF</c> array shall not be
    /// painted. This is the one case where <c>BDC</c> is NOT transparent, so
    /// it is the strongest available assertion about the operator: excise must
    /// suppress the hidden square while still painting the unmarked marker
    /// (which proves the page rendered at all), and mutool must agree on both.
    /// </summary>
    [Fact]
    public void MarkedContent_BDC_WithAnOffOptionalContentGroup_HidesTheContent_MatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(OptionalContentFixture());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        Assert.SkipWhen(mutool == null, "mutool could not render the optional-content fixture");

        IsInk(mutool!, 100, 100).Should().BeFalse(
            "guard: mutool must hide the square inside the OFF optional-content group");
        IsInk(mutool!, 40, 20).Should().BeTrue(
            "guard: mutool must still paint the unmarked marker, or the page simply failed to render");

        using var excise = RenderWithExcise(path, 200);

        IsInk(excise, 40, 20).Should().BeTrue(
            "the unmarked marker must paint — otherwise 'hidden' below would be indistinguishable from a blank page");
        IsInk(excise, 100, 100).Should().BeFalse(
            "content inside `/OC … BDC` whose OCG is in the default configuration's /OFF array must not be painted");
    }

    /// <summary>
    /// 200x200 page: a black square wrapped in <c>/OC /oc1 BDC … EMC</c> whose
    /// group is OFF by default, plus an unmarked blue marker near the top edge
    /// that must always paint.
    /// </summary>
    private static byte[] OptionalContentFixture()
    {
        const string content =
            "0 0 0 rg /OC /oc1 BDC 40 40 120 120 re f EMC " +
            "0 0 1 rg 20 170 40 20 re f";
        return ResourcePageFixture(
            content, 200, 200,
            "/Properties << /oc1 5 0 R >>",
            new[] { "5 0 obj\n<< /Type /OCG /Name (Hidden) >>\nendobj\n" },
            "/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] >> >>");
    }

    // ── shared fixtures and helpers for this batch ──────────────────────────

    /// <summary>
    /// A one-page fixture with an arbitrary page <c>/Resources</c> dictionary
    /// body and any number of extra indirect objects, which must number
    /// themselves from 5 upward (1-4 are catalog, page tree, page, contents).
    /// <paramref name="catalogExtra"/> is spliced into the catalog dictionary,
    /// which is where <c>/OCProperties</c> has to live.
    /// </summary>
    private static byte[] ResourcePageFixture(
        string content, int w, int h, string resources,
        IEnumerable<string>? extraObjects = null, string catalogExtra = "")
    {
        var objects = new List<string>
        {
            $"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {w} {h}] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << {resources} >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };
        if (extraObjects != null) objects.AddRange(extraObjects);
        return Assemble(objects);
    }

    private static byte[] ResourcePageFixture(
        string content, int w, int h, string resources, params string[] extraObjects) =>
        ResourcePageFixture(content, w, h, resources, (IEnumerable<string>)extraObjects);

    /// <summary>Height of the overall ink bounding box across the whole bitmap.</summary>
    private static int InkHeight(SKBitmap bmp)
    {
        int minY = bmp.Height, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y)) { if (y < minY) minY = y; if (y > maxY) maxY = y; break; }
        return maxY < 0 ? 0 : maxY - minY + 1;
    }

    /// <summary>Ink bounding box of the whole bitmap, in raster pixels.</summary>
    private static (int MinX, int MinY, int Width, int Height) InkBox(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y))
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Width of the ink bounding box restricted to rows [yMin, yMax).</summary>
    private static int InkWidthInRows(SKBitmap bmp, int yMin, int yMax)
    {
        int minX = bmp.Width, maxX = -1;
        for (int y = Math.Max(0, yMin); y < Math.Min(bmp.Height, yMax); y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp, x, y)) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
        return maxX < 0 ? 0 : maxX - minX + 1;
    }

    /// <summary>
    /// For the two-line text fixtures (first line near y=200pt, second near
    /// y=160pt on a 240pt page): the second line's offset from the first, in
    /// pixels, as (right, down). Null when either line painted nothing.
    /// </summary>
    private static (int Dx, int Dy)? TwoLineOffsets(SKBitmap bmp, int pageHeight)
    {
        int firstTop = TopInkedRow(bmp, pageHeight - 220, pageHeight - 180);
        int secondTop = TopInkedRow(bmp, pageHeight - 180, pageHeight - 140);
        if (firstTop < 0 || secondTop < 0) return null;
        int firstLeft = LeftmostInkedColumn(bmp, pageHeight - 220, pageHeight - 180);
        int secondLeft = LeftmostInkedColumn(bmp, pageHeight - 180, pageHeight - 140);
        if (firstLeft < 0 || secondLeft < 0) return null;
        return (secondLeft - firstLeft, secondTop - firstTop);
    }

    private static int LeftmostInkedColumn(SKBitmap bmp, int yMin, int yMax)
    {
        for (int x = 0; x < bmp.Width; x++)
            for (int y = Math.Max(0, yMin); y < Math.Min(bmp.Height, yMax); y++)
                if (IsInk(bmp, x, y)) return x;
        return -1;
    }
}
