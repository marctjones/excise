using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for a batch of PDF 2.0 content-stream
/// operators in <c>test-pdfs/manifests/pdf-spec-registry/sections/renderer-requirements.json</c>
/// (<c>pdf.20.renderer.requirement-*</c>, one row per <c>Annex A</c> operator).
///
/// Every one of these capabilities was previously "verified" only by a
/// <c>SkiaRendererTests</c>/<c>SkiaRendererCoverageTests</c> fact that renders
/// with excise and asserts <c>bitmap.Width.Should().BeGreaterThan(0)</c> — excise
/// checking that excise didn't throw, which is <c>implemented</c>, not
/// <c>verified</c> (see CLAUDE.md's no-self-oracle rule). Each test here instead
/// renders the same minimal fixture with <c>mutool draw</c> — a completely
/// independent PDF interpreter — and asserts a genuine, operator-specific visual
/// property that both engines must agree on: a fill-rule hole, a cap/join
/// extension, a dash gap, an exact color value, a clip boundary, a state-stack
/// restore, or a translated bounding box.
///
/// Every property asserted about excise is first asserted about mutool's own
/// render of the identical fixture ("fixture sanity") — if mutool didn't show
/// the property either, the fixture would prove nothing about the operator.
/// </summary>
public sealed class RendererRequirementVerificationOperatorTests : IDisposable
{
    private const int Dpi = 150;
    private const int DefaultPageSize = 300;
    private static double Scale => Dpi / 72.0;

    private readonly List<string> _temp = new();

    // ── q / Q — pdf.20.renderer.requirement-023 / -024 ─────────────────────

    /// <summary>
    /// A fill color set INSIDE a q/Q block must not leak out. Draw a red
    /// square, then inside q...Q change (but never paint with) the fill color
    /// to blue, then after Q draw a second square with no new color operator
    /// — it must inherit the PRE-q red, not the blue set inside the saved
    /// state. This is the actual semantic of §8.4.2, not just "renders
    /// without throwing".
    ///
    /// (First draft of this fixture painted the SAME rectangle blue a second
    /// time inside q, "for visibility" — which made square A itself blue
    /// after paint-order compositing and broke the exciseA==exciseB
    /// comparison below for a reason that had nothing to do with Q. Caught by
    /// investigating the failure rather than loosening the assertion: the
    /// fixture must never repaint the thing it uses as the "before" sample.)
    /// </summary>
    [Fact]
    public void SaveRestore_RestoresFillColorAfterQ_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = @"
            1 0 0 rg
            40 40 60 60 re f
            q
            0 0 1 rg
            Q
            170 40 60 60 re f
        ";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull("mutool must render this trivial fixture");

        var mutoolB = SamplePdfPoint(mutool!, 200, 70, DefaultPageSize);
        var exciseA = SamplePdfPoint(excise, 70, 70, DefaultPageSize);
        var exciseB = SamplePdfPoint(excise, 200, 70, DefaultPageSize);

        // Fixture sanity: an independent interpreter must show square B as red
        // (state restored), not blue — otherwise this fixture doesn't test Q.
        mutoolB.Red.Should().BeGreaterThan(180);
        mutoolB.Blue.Should().BeLessThan(80);

        // excise must agree with the independent renderer...
        ColorsClose(exciseB, mutoolB, 25).Should().BeTrue(
            $"excise's post-Q square must match mutool's: excise={exciseB}, mutool={mutoolB}");

        // ...and the two excise squares (pre-q and post-Q) must be identical,
        // proving Q genuinely restored the saved state rather than the second
        // square happening to also be drawn red.
        ColorsClose(exciseA, exciseB, 10).Should().BeTrue(
            "the pre-q and post-Q squares must be the same color if Q truly restored state");
    }

    // ── cm — pdf.20.renderer.requirement-025 ────────────────────────────────

    /// <summary>
    /// <c>1 0 0 1 tx ty cm</c> translates subsequent painting. The ink bounding
    /// box of a rect drawn at the origin after this cm must land at
    /// (tx,ty)-(tx+60,ty+60), not at the origin — verified by comparing excise's
    /// ink bbox against mutool's independently-computed bbox for the same file.
    /// </summary>
    [Fact]
    public void Translation_cm_MovesRect_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = @"
            0 0 0 rg
            1 0 0 1 100 50 cm
            0 0 60 60 re f
        ";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var e = InkBoundsBox(excise);
        var m = InkBoundsBox(mutool!);

        ((double)e.MinX).Should().BeApproximately(m.MinX, 3);
        ((double)e.MinY).Should().BeApproximately(m.MinY, 3);
        ((double)e.Width).Should().BeApproximately(m.Width, 3);
        ((double)e.Height).Should().BeApproximately(m.Height, 3);

        // Sanity: the box must actually be away from the origin (proves the
        // cm translation moved it rather than the rect merely rendering at (0,0)).
        e.MinX.Should().BeGreaterThan((int)(50 * Scale),
            "the translated rect must not sit at the untranslated origin");
    }

    // ── J — pdf.20.renderer.requirement-027 ─────────────────────────────────

    /// <summary>
    /// A round cap (<c>1 J</c>) extends stroked ink past the line's logical
    /// endpoints by a semicircle of radius strokeWidth/2 at each end; a butt
    /// cap (<c>0 J</c>, the default) does not. The width difference between
    /// the two renders is therefore ~strokeWidth. Measured independently by
    /// mutool and by excise, and the two measurements must agree.
    /// </summary>
    [Fact]
    public void RoundLineCap_ExtendsInkPastEndpoints_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var buttPath = WriteTemp(CapLinePdf(cap: 0));
        var roundPath = WriteTemp(CapLinePdf(cap: 1));

        var exButt = InkBoundsBox(RenderWithExcise(buttPath));
        var exRound = InkBoundsBox(RenderWithExcise(roundPath));

        using var muButt = MutoolReferenceRenderer.RenderPage(buttPath, 1, Dpi);
        using var muRound = MutoolReferenceRenderer.RenderPage(roundPath, 1, Dpi);
        muButt.Should().NotBeNull();
        muRound.Should().NotBeNull();
        var mButt = InkBoundsBox(muButt!);
        var mRound = InkBoundsBox(muRound!);

        double expectedExtension = 20 * Scale; // strokeWidth (20) * scale, split across both ends
        double muExtension = mRound.Width - mButt.Width;
        double exExtension = exRound.Width - exButt.Width;

        // Fixture sanity: an independent renderer must show the round cap
        // extending the ink bbox, or this fixture proves nothing about J.
        muExtension.Should().BeGreaterThan(expectedExtension * 0.5,
            "mutool must show the round cap extending past the line's endpoints");

        ((double)exExtension).Should().BeApproximately(muExtension, 6,
            $"excise's round-cap extension ({exExtension}px) must match mutool's ({muExtension}px)");
    }

    private static byte[] CapLinePdf(int cap) => Page($@"
        0 G
        20 w
        {cap} J
        100 150 m 200 150 l S
    ");

    // ── j — pdf.20.renderer.requirement-028 ─────────────────────────────────

    /// <summary>
    /// At a sharp interior angle, a miter join (<c>0 j</c>, the default, with a
    /// generous miter limit) produces a pointed spike well past the vertex; a
    /// round join (<c>1 j</c>) is bounded to strokeWidth/2 past the vertex. The
    /// miter fixture's topmost ink row must therefore sit well above (smaller
    /// pixel row) the round fixture's — independently confirmed by mutool.
    /// </summary>
    [Fact]
    public void RoundLineJoin_StaysBoundedUnlikeAMiterSpike_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var miterPath = WriteTemp(JoinSpikePdf(join: 0, miterLimit: 10));
        var roundPath = WriteTemp(JoinSpikePdf(join: 1, miterLimit: 10));

        var exMiter = InkBoundsBox(RenderWithExcise(miterPath));
        var exRound = InkBoundsBox(RenderWithExcise(roundPath));

        using var muMiter = MutoolReferenceRenderer.RenderPage(miterPath, 1, Dpi);
        using var muRound = MutoolReferenceRenderer.RenderPage(roundPath, 1, Dpi);
        muMiter.Should().NotBeNull();
        muRound.Should().NotBeNull();
        var mMiter = InkBoundsBox(muMiter!);
        var mRound = InkBoundsBox(muRound!);

        // Fixture sanity: mutool's full miter spike must reach measurably
        // higher (smaller row index) than its round join.
        var muGap = mRound.MinY - mMiter.MinY;
        muGap.Should().BeGreaterThan(20,
            "mutool's miter spike must extend well past its round join for this apex angle");

        var exGap = exRound.MinY - exMiter.MinY;
        exGap.Should().BeGreaterThan(0,
            "excise's miter join must also reach higher than its round join");
        ((double)exGap).Should().BeApproximately(muGap, 30,
            $"excise's miter-vs-round gap ({exGap}px) must be the same order as mutool's ({muGap}px)");
    }

    // ── M — pdf.20.renderer.requirement-029 ─────────────────────────────────

    /// <summary>
    /// §8.4.3.5: when the miter length ratio (1/sin(halfAngle)) exceeds the
    /// miter limit, a conforming renderer MUST fall back to a bevel even
    /// though the join style is still "miter". For this fixture's ~34.7°
    /// apex angle the ratio is ~3.36, so <c>M 10</c> allows the full spike
    /// while <c>M 2</c> forces a bevel — independently reproduced by mutool.
    /// </summary>
    [Fact]
    public void MiterLimit_ForcesBevelWhenRatioExceedsLimit_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var highLimitPath = WriteTemp(JoinSpikePdf(join: 0, miterLimit: 10));
        var lowLimitPath = WriteTemp(JoinSpikePdf(join: 0, miterLimit: 2));

        var exHigh = InkBoundsBox(RenderWithExcise(highLimitPath));
        var exLow = InkBoundsBox(RenderWithExcise(lowLimitPath));

        using var muHigh = MutoolReferenceRenderer.RenderPage(highLimitPath, 1, Dpi);
        using var muLow = MutoolReferenceRenderer.RenderPage(lowLimitPath, 1, Dpi);
        muHigh.Should().NotBeNull();
        muLow.Should().NotBeNull();
        var mHigh = InkBoundsBox(muHigh!);
        var mLow = InkBoundsBox(muLow!);

        // Fixture sanity: mutool must itself lower the miter limit and see the
        // spike disappear (a bevelled corner reaches less far than the spike).
        var muGap = mLow.MinY - mHigh.MinY;
        muGap.Should().BeGreaterThan(20,
            "mutool must fall back to a bevel once the miter-limit ratio is exceeded");

        var exGap = exLow.MinY - exHigh.MinY;
        exGap.Should().BeGreaterThan(0,
            "excise must also fall back to a bevel once M is below the ratio");
        ((double)exGap).Should().BeApproximately(muGap, 30,
            $"excise's M-driven bevel gap ({exGap}px) must be the same order as mutool's ({muGap}px)");
    }

    private static byte[] JoinSpikePdf(int join, int miterLimit) => Page($@"
        0 G
        30 w
        {join} j
        {miterLimit} M
        100 100 m 150 260 l 200 100 l S
    ", pageSize: 400);

    // ── d — pdf.20.renderer.requirement-030 ─────────────────────────────────

    /// <summary>
    /// A dash array <c>[15 10] 0 d</c> leaves ~40% of a stroked line unpainted
    /// (gaps), so the total ink of a dashed stroke must be a modest fraction
    /// of a solid stroke's — checked in mutool's independent render first,
    /// then in excise's, then the two fractions must roughly agree.
    /// </summary>
    [Fact]
    public void DashPattern_LeavesGapsInStroke_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var solidPath = WriteTemp(DashLinePdf(dashed: false));
        var dashedPath = WriteTemp(DashLinePdf(dashed: true));

        var exSolidInk = InkPixels(RenderWithExcise(solidPath));
        var exDashedInk = InkPixels(RenderWithExcise(dashedPath));

        using var muSolid = MutoolReferenceRenderer.RenderPage(solidPath, 1, Dpi);
        using var muDashed = MutoolReferenceRenderer.RenderPage(dashedPath, 1, Dpi);
        muSolid.Should().NotBeNull();
        muDashed.Should().NotBeNull();
        var muSolidInk = InkPixels(muSolid!);
        var muDashedInk = InkPixels(muDashed!);

        double muFraction = (double)muDashedInk / muSolidInk;
        double exFraction = (double)exDashedInk / exSolidInk;

        // Fixture sanity: mutool must show a real gap pattern (not solid).
        muFraction.Should().BeLessThan(0.9,
            "mutool must show the dash pattern leaving gaps");
        muFraction.Should().BeGreaterThan(0.2,
            "mutool must still show a stroke, not an empty one");

        exFraction.Should().BeInRange(muFraction - 0.2, muFraction + 0.2,
            $"excise's dashed-to-solid ink ratio ({exFraction:P0}) must agree with mutool's ({muFraction:P0})");
    }

    private static byte[] DashLinePdf(bool dashed) => Page(dashed
        ? "0 G\n5 w\n[15 10] 0 d\n40 150 m 260 150 l S"
        : "0 G\n5 w\n40 150 m 260 150 l S");

    // ── m, l, v, y, h — pdf.20.renderer.requirement-034/035/037/038/039 ─────

    /// <summary>
    /// A single closed subpath built from every path-construction primitive
    /// except cubic <c>c</c> (which has its own coverage elsewhere):
    /// <c>m</c> moveto, <c>l</c> lineto, <c>v</c> curveto (first control =
    /// current point), <c>y</c> curveto (second control = endpoint), and
    /// <c>h</c> closepath. Both engines must resolve the identical Bézier math
    /// to the same ink bounding box and the same filled area.
    /// </summary>
    [Fact]
    public void PathConstructionPrimitives_ClosedPathViaLVYH_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = @"
            0 0 0 rg
            50 50 m
            50 200 l
            100 250 150 200 v
            200 250 200 150 y
            200 50 l
            h
            f
        ";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var e = InkBoundsBox(excise);
        var m = InkBoundsBox(mutool!);

        ((double)e.MinX).Should().BeApproximately(m.MinX, 4);
        ((double)e.MinY).Should().BeApproximately(m.MinY, 4);
        ((double)e.Width).Should().BeApproximately(m.Width, 4);
        ((double)e.Height).Should().BeApproximately(m.Height, 4);

        int eInk = InkPixels(excise);
        int mInk = InkPixels(mutool!);
        ((double)eInk).Should().BeApproximately(mInk, mInk * 0.1,
            $"the filled area from m/l/v/y/h must agree between excise ({eInk}px) and mutool ({mInk}px)");
    }

    // ── S — pdf.20.renderer.requirement-041 ─────────────────────────────────

    /// <summary>
    /// <c>S</c> strokes the path outline only — the interior of a stroked
    /// rectangle must stay unpainted in both engines, while the border ink
    /// bounding box must agree between them.
    /// </summary>
    [Fact]
    public void StrokeOnly_S_LeavesInteriorUnpainted_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = "0 G\n10 w\n60 60 150 150 re\nS";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var interiorMutool = SamplePdfPoint(mutool!, 135, 135, DefaultPageSize);
        var interiorExcise = SamplePdfPoint(excise, 135, 135, DefaultPageSize);

        interiorMutool.Red.Should().BeGreaterThan(230,
            "mutool must leave the interior of a merely-stroked rectangle unpainted");
        interiorExcise.Red.Should().BeGreaterThan(230,
            "excise must also leave the interior unpainted — S must not fill");

        var e = InkBoundsBox(excise);
        var m = InkBoundsBox(mutool!);
        ((double)e.MinX).Should().BeApproximately(m.MinX, 3);
        ((double)e.Width).Should().BeApproximately(m.Width, 3);
    }

    // ── f — pdf.20.renderer.requirement-043 ─────────────────────────────────

    /// <summary>
    /// <c>f</c> fills the path interior solidly. The interior must be black
    /// in both engines and the outer bbox must agree.
    /// </summary>
    [Fact]
    public void FillOnly_f_PaintsSolidInterior_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = "0 0 0 rg\n60 60 150 150 re\nf";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var interiorMutool = SamplePdfPoint(mutool!, 135, 135, DefaultPageSize);
        var interiorExcise = SamplePdfPoint(excise, 135, 135, DefaultPageSize);

        interiorMutool.Red.Should().BeLessThan(25, "mutool must show a solid fill");
        interiorExcise.Red.Should().BeLessThan(25, "excise must also show a solid fill");

        var e = InkBoundsBox(excise);
        var m = InkBoundsBox(mutool!);
        ((double)e.Width).Should().BeApproximately(m.Width, 3);
        ((double)e.Height).Should().BeApproximately(m.Height, 3);
    }

    // ── f* — pdf.20.renderer.requirement-045 ────────────────────────────────

    /// <summary>
    /// A self-intersecting 5-point star path filled with nonzero winding
    /// (<c>f</c>) paints the doubly-wound center pentagon (winding number 2:
    /// nonzero); filled with even-odd (<c>f*</c>) that same region has an EVEN
    /// winding count and is left unpainted, producing a visible hole. This is
    /// the textbook fixture for distinguishing the two fill rules (§8.5.3),
    /// and it is reproduced independently by mutool.
    /// </summary>
    [Fact]
    public void FillEvenOdd_StarFixture_LeavesCenterUnpaintedUnlikeNonzero_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var nonzeroPath = WriteTemp(Page(StarPath() + "f"));
        var evenOddPath = WriteTemp(Page(StarPath() + "f*"));

        var exNonzeroInk = InkPixels(RenderWithExcise(nonzeroPath));
        var exEvenOddInk = InkPixels(RenderWithExcise(evenOddPath));

        using var muNonzero = MutoolReferenceRenderer.RenderPage(nonzeroPath, 1, Dpi);
        using var muEvenOdd = MutoolReferenceRenderer.RenderPage(evenOddPath, 1, Dpi);
        muNonzero.Should().NotBeNull();
        muEvenOdd.Should().NotBeNull();
        var muNonzeroInk = InkPixels(muNonzero!);
        var muEvenOddInk = InkPixels(muEvenOdd!);

        // Fixture sanity: an independent renderer must show LESS ink for
        // even-odd (the center pentagon hole) than for nonzero.
        muEvenOddInk.Should().BeLessThan((int)(muNonzeroInk * 0.9),
            "mutool must show the even-odd star with a hollow center vs. the solid nonzero star");

        exEvenOddInk.Should().BeLessThan((int)(exNonzeroInk * 0.9),
            "excise must also show the even-odd star with a hollow center");

        // The center-pentagon hole must be a similar fraction of the total in
        // both engines (not merely "some" difference).
        double muHoleFraction = 1.0 - (double)muEvenOddInk / muNonzeroInk;
        double exHoleFraction = 1.0 - (double)exEvenOddInk / exNonzeroInk;
        exHoleFraction.Should().BeInRange(muHoleFraction - 0.15, muHoleFraction + 0.15,
            $"excise's hole fraction ({exHoleFraction:P0}) must agree with mutool's ({muHoleFraction:P0})");
    }

    // ── n, W, W* — pdf.20.renderer.requirement-050/051/052 ──────────────────

    /// <summary>
    /// Setting the star path as a NONZERO clip (<c>W n</c> — the <c>n</c>
    /// no-op operator applies the pending clip without painting the path
    /// itself, §8.5.4) then filling the whole page must confine visible ink
    /// to the nonzero-wound star shape (solid center) — the same shape the
    /// nonzero fill-rule test paints directly. Verified against mutool.
    /// </summary>
    [Fact]
    public void ClipNonzero_W_ConfinesPaintToNonzeroStarRegion_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var clipPath = WriteTemp(Page(StarPath() + "W n\n0 0 0 rg\n0 0 300 300 re f"));
        var directFillPath = WriteTemp(Page(StarPath() + "f"));

        var exClipInk = InkPixels(RenderWithExcise(clipPath));
        var exDirectInk = InkPixels(RenderWithExcise(directFillPath));

        using var muClip = MutoolReferenceRenderer.RenderPage(clipPath, 1, Dpi);
        muClip.Should().NotBeNull();
        var muClipInk = InkPixels(muClip!);

        // Fixture sanity: mutool must confine the page-filling black rect to
        // (approximately) the star's own footprint, not the whole page.
        var pageInk = DefaultPageSize * DefaultPageSize * Scale * Scale;
        muClipInk.Should().BeLessThan((int)(pageInk * 0.5),
            "mutool must confine the clipped fill to well under the full page area");

        // excise's clip-confined ink must be close to mutool's, and both must
        // be close to excise's own direct (unclipped-path) nonzero fill of the
        // same star — proving W actually clips to the nonzero region rather
        // than some other area.
        ((double)exClipInk).Should().BeApproximately(muClipInk, muClipInk * 0.15,
            $"excise's W-clipped ink ({exClipInk}px) must agree with mutool's ({muClipInk}px)");
        ((double)exClipInk).Should().BeApproximately(exDirectInk, exDirectInk * 0.1,
            "clipping to the nonzero star then filling the page must paint the same area as directly filling the star");
    }

    /// <summary>
    /// The even-odd counterpart: <c>W* n</c> clips to the star's even-odd
    /// region (hollow center) instead. The clipped ink must therefore be
    /// LESS than the nonzero-clip case above, matching the fill-rule
    /// distinction already proven for <c>f</c>/<c>f*</c> — independently in
    /// mutool.
    /// </summary>
    [Fact]
    public void ClipEvenOdd_WStar_ExcludesCenterHoleUnlikeNonzeroClip_AgreesWithIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var nonzeroClipPath = WriteTemp(Page(StarPath() + "W n\n0 0 0 rg\n0 0 300 300 re f"));
        var evenOddClipPath = WriteTemp(Page(StarPath() + "W* n\n0 0 0 rg\n0 0 300 300 re f"));

        var exNonzero = InkPixels(RenderWithExcise(nonzeroClipPath));
        var exEvenOdd = InkPixels(RenderWithExcise(evenOddClipPath));

        using var muNonzero = MutoolReferenceRenderer.RenderPage(nonzeroClipPath, 1, Dpi);
        using var muEvenOdd = MutoolReferenceRenderer.RenderPage(evenOddClipPath, 1, Dpi);
        muNonzero.Should().NotBeNull();
        muEvenOdd.Should().NotBeNull();
        var muNonzeroInk = InkPixels(muNonzero!);
        var muEvenOddInk = InkPixels(muEvenOdd!);

        muEvenOddInk.Should().BeLessThan((int)(muNonzeroInk * 0.9),
            "mutool's even-odd clip must exclude the star's center hole, unlike the nonzero clip");

        exEvenOdd.Should().BeLessThan((int)(exNonzero * 0.9),
            "excise's even-odd clip must also exclude the star's center hole");

        double muHoleFraction = 1.0 - (double)muEvenOddInk / muNonzeroInk;
        double exHoleFraction = 1.0 - (double)exEvenOdd / exNonzero;
        exHoleFraction.Should().BeInRange(muHoleFraction - 0.15, muHoleFraction + 0.15,
            $"excise's W* hole fraction ({exHoleFraction:P0}) must agree with mutool's ({muHoleFraction:P0})");
    }

    /// <summary>
    /// Five points of a pentagram, visited in star order (every second
    /// vertex) so the path self-intersects — the standard nonzero/even-odd
    /// distinguishing fixture. Center (150,150), radius 100, on a 300x300
    /// page. No trailing painting operator; callers append f / f* / W n / W* n.
    /// </summary>
    private static string StarPath() => @"
        0 0 0 rg
        150.00 50.00 m
        208.78 230.90 l
        54.89 119.10 l
        245.11 119.10 l
        91.22 230.90 l
        h
        ";

    // ── G, g — pdf.20.renderer.requirement-059 / -060 ───────────────────────

    /// <summary>
    /// <c>g</c> (fill gray) and <c>G</c> (stroke gray) must produce the exact
    /// device-gray RGB triplet in excise's DeviceGray-&gt;RGB path, matching
    /// mutool's independent conversion pixel-for-pixel (no color management
    /// involved for plain gray, so the tolerance is tight).
    /// </summary>
    [Fact]
    public void GrayscaleFillAndStroke_PixelValuesMatchIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = @"
            0.6 g
            60 60 150 150 re f
            0.2 G
            8 w
            60 60 150 150 re S
        ";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var exciseFill = SamplePdfPoint(excise, 135, 135, DefaultPageSize);
        var mutoolFill = SamplePdfPoint(mutool!, 135, 135, DefaultPageSize);
        var exciseStroke = SamplePdfPoint(excise, 135, 60, DefaultPageSize);
        var mutoolStroke = SamplePdfPoint(mutool!, 135, 60, DefaultPageSize);

        // Fixture sanity: mutool's fill must be lighter than its stroke
        // (0.6 gray > 0.2 gray), or the fixture doesn't test both operators.
        mutoolFill.Red.Should().BeGreaterThan(mutoolStroke.Red,
            "mutool's 0.6 fill must be visibly lighter than its 0.2 stroke");

        ColorsClose(exciseFill, mutoolFill, 15).Should().BeTrue(
            $"excise's g fill ({exciseFill}) must match mutool's ({mutoolFill})");
        ColorsClose(exciseStroke, mutoolStroke, 15).Should().BeTrue(
            $"excise's G stroke ({exciseStroke}) must match mutool's ({mutoolStroke})");
    }

    // ── RG, rg — pdf.20.renderer.requirement-061 / -062 ─────────────────────

    /// <summary>
    /// <c>rg</c> (fill RGB) and <c>RG</c> (stroke RGB) must produce the exact
    /// requested RGB triplets, matching mutool's independent DeviceRGB path.
    /// </summary>
    [Fact]
    public void RgbFillAndStroke_PixelValuesMatchIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string content = @"
            0.2 0.4 0.8 rg
            60 60 150 150 re f
            0.9 0.1 0.1 RG
            8 w
            60 60 150 150 re S
        ";
        var path = WriteTemp(Page(content));

        using var excise = RenderWithExcise(path);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();

        var exciseFill = SamplePdfPoint(excise, 135, 135, DefaultPageSize);
        var mutoolFill = SamplePdfPoint(mutool!, 135, 135, DefaultPageSize);
        var exciseStroke = SamplePdfPoint(excise, 135, 60, DefaultPageSize);
        var mutoolStroke = SamplePdfPoint(mutool!, 135, 60, DefaultPageSize);

        // Fixture sanity: mutool's fill must be predominantly blue and its
        // stroke predominantly red, or the fixture doesn't test both operators.
        mutoolFill.Blue.Should().BeGreaterThan(mutoolFill.Red,
            "mutool's rg fill must be predominantly blue");
        mutoolStroke.Red.Should().BeGreaterThan(mutoolStroke.Blue,
            "mutool's RG stroke must be predominantly red");

        ColorsClose(exciseFill, mutoolFill, 15).Should().BeTrue(
            $"excise's rg fill ({exciseFill}) must match mutool's ({mutoolFill})");
        ColorsClose(exciseStroke, mutoolStroke, 15).Should().BeTrue(
            $"excise's RG stroke ({exciseStroke}) must match mutool's ({mutoolStroke})");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] Page(string content, int pageSize = DefaultPageSize) => Assemble(new[]
    {
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
        $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {pageSize} {pageSize}] >>\nendobj\n",
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << >> >>\nendobj\n",
        ContentObj(content),
    });

    private static string ContentObj(string content)
    {
        var byteCount = Encoding.ASCII.GetByteCount(content);
        return $"4 0 obj\n<< /Length {byteCount} >>\nstream\n{content}\nendstream\nendobj\n";
    }

    private static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    /// <summary>
    /// Maps a PDF-space point to the pixel it lands on in a top-down raster
    /// rendered at <see cref="Dpi"/>, given the page's (square) MediaBox size.
    /// Matches <c>SkiaRenderer</c>'s page-to-device matrix: pixelX = pdfX *
    /// scale; pixelY = (pageHeight - pdfY) * scale (PDF's bottom-left origin
    /// flipped to the raster's top-left origin).
    /// </summary>
    private static SKColor SamplePdfPoint(SKBitmap bmp, double pdfX, double pdfY, int pageSize)
    {
        int x = Math.Clamp((int)(pdfX * Scale), 0, bmp.Width - 1);
        int y = Math.Clamp((int)((pageSize - pdfY) * Scale), 0, bmp.Height - 1);
        return bmp.GetPixel(x, y);
    }

    private static bool ColorsClose(SKColor a, SKColor b, int tolerance) =>
        Math.Abs(a.Red - b.Red) <= tolerance &&
        Math.Abs(a.Green - b.Green) <= tolerance &&
        Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static (int MinX, int MinY, int Width, int Height) InkBoundsBox(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 200 && c.Green < 200 && c.Blue < 200)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        return maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 200 && c.Green < 200 && c.Blue < 200) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-rendreq-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
