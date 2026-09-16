using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1510 and #1511 — the pre-#1395 <c>UnmaskedDeviceCmykGroup</c> entry, i.e. an
/// explicit <c>/CS /DeviceCMYK</c> transparency group invoked with no soft mask.
/// That entry opens a child context whose bitmap is a GROUP bitmap cleared to
/// transparent, exactly like #1395's contained entries, but it was not covered by
/// #1505's fix because that keyed on <c>_isContainedGroupChild</c> rather than on
/// the property that matters — "this bitmap is a group bitmap".
///
/// <para><b>Why the flavour of the sync is the whole question.</b> The two
/// backdrop syncs store the same colour and the same alpha at bitmap alpha 255;
/// they differ ONLY where alpha is below 255. The page-flavoured sync
/// (<c>SyncDeviceCmykBackdropFromRootBitmap</c>) resolves partial alpha against
/// paper and records a zero-alpha pixel as zero ink at alpha 0 — right for the
/// page bitmap, which a DeviceCMYK-group page clears to the paper colour. The
/// group-flavoured sync keeps straight colour at the pixel's own alpha and skips
/// zero-alpha pixels — right for a buffer that starts empty, where an unpainted
/// pixel carries no information and a nested group's seed or knockout reset lives
/// underneath it.</para>
///
/// <para><b>One class, four sites, one row each.</b> Reverting any single change
/// must redden exactly the row named for it:</para>
/// <list type="number">
/// <item><b>Site 1</b> — the pre-seed sync when a non-isolated group is entered
/// under a non-Normal <c>/BM</c> (<c>SkiaRenderer.XObjects.cs</c>, inside
/// <c>TryRenderDeviceCmykFormGroup</c>): <see cref="SiteOne_NestedScreenGroupInsideExplicitCmykGroup_MatchesIndependentRenderers"/>
/// and the grouping-invariance property next to it.</item>
/// <item><b>Site 2</b> — that entry's post-content, dirty-flag-gated fold:
/// <see cref="SiteTwo_HalfOpacityShadingPatternInsideExplicitCmykGroup_MatchesItsCalibrationPatch"/>.</item>
/// <item><b>Site 3</b> — the contained Skia-layer fold-back, found in passing and
/// the same defect (it runs on <c>this</c>, which is a child context whenever a
/// nested group's <c>/CS</c> is explicitly not CMYK):
/// <see cref="SiteThree_NonCmykGroupInsideExplicitCmykGroup_IsNotDoubleLightened"/>.</item>
/// <item><b>#1511</b> — the <c>sh</c> operator never marking the backdrop dirty:
/// <see cref="ShOperatorInsideExplicitCmykGroup_MatchesIndependentRenderers"/>
/// and the no-oracle sibling next to it.</item>
/// </list>
///
/// <para><b>Oracles: mutool AND Ghostscript, never pdftocairo.</b> pdftocairo
/// reproduces excise's isolated-group defect (#1373/#1394) and on pdf.js
/// issue13520 renders 103 dark pixels in the lobe where mutool and Ghostscript
/// both render 0 (#1505). Agreeing with it would be a warning, not a result. On
/// every fixture here mutool and Ghostscript agree to within 3 RGB levels.</para>
/// </summary>
public class DeviceCmykExplicitGroupBackdropSyncTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>
    /// Tolerance on the normalised dark fraction, for the rows whose right and
    /// wrong answers are about 1.0 apart. Copied from
    /// <see cref="DeviceCmykNestedNonIsolatedGroupBlendTests"/>, which measures
    /// the same quantity on the same page geometry.
    /// </summary>
    private const double FractionTolerance = 0.2;

    /// <summary>
    /// Luminance tolerance for the rows that compare a pixel against a
    /// CALIBRATION PATCH in the same render. MEASURED: excise lands 4.3 levels
    /// off the patch with the fix and 61.3 off without it, so this sits an order
    /// of magnitude below the defect. Deliberately not a fraction between two
    /// different inks: a same-render comparison cancels each renderer's own
    /// CMYK-to-RGB preview conversion exactly.
    /// </summary>
    private const double PatchLuminanceTolerance = 15;

    /// <summary>
    /// The oracles' own object-vs-patch agreement, which the calibration rests
    /// on. Measured 1.1 (mutool) and 0.0 (Ghostscript).
    /// </summary>
    private const double OracleCalibrationTolerance = 4;

    /// <summary>
    /// A loose per-channel bound kept alongside the luminance one so a gross hue
    /// shift still fails. Measured: 13 with the fix (the RgbToDeviceCmyk round
    /// trip on a neutral grey), 62 without it.
    /// </summary>
    private const int PatchHueGuard = 25;

    // Device pixels at 72 dpi on a 400x200 pt page: x = PDF x, y = 200 - PDF y.
    private static readonly SKPointI InsideObject = new(100, 100);
    private static readonly SKPointI WashSample = new(350, 150);
    private static readonly SKPointI ReferenceSample = new(350, 50);

    /// <summary>The <c>0 0 0 0.55 k</c> patch: 50% of full black over the 10%
    /// black wash, composited per §11.3 in the page group's DeviceCMYK space.
    /// Both oracles render the half-opacity object and this patch within 1.1
    /// luminance levels of each other (measured), which is what makes it a
    /// calibration.</summary>
    private static readonly SKPointI CalibrationSample = new(250, 50);

    private readonly List<string> _temp = new();

    // ---------------------------------------------------------------- site 1

    /// <summary>
    /// #1510 site 1. A non-isolated <c>/BM /Screen</c> group nested inside an
    /// explicit <c>/CS /DeviceCMYK</c> group. Entering it, the enclosing child
    /// reconciles its retained CMYK backdrop with its own bitmap so the Screen
    /// has the real backdrop to act on — and ran the PAGE-flavoured sync, which
    /// read alpha 0 / RGB (0,0,0) on every pixel the enclosing group had not
    /// painted and overwrote the seed with zero ink at alpha 0.
    /// <c>BlendDeviceCmykWithBackdropAlpha</c> then correctly short-circuits a
    /// zero-alpha backdrop to the source itself (§11.3.6), so the nested group's
    /// RAW ink reached the page with no blend at all.
    ///
    /// <para>This is #1505's fixture with <c>/CS /DeviceCMYK</c> added to the
    /// enclosing group — one dictionary key, which moves it from #1395's
    /// contained entry to the pre-#1395 one. The invocation needs no <c>/ca</c>
    /// trick: an unmasked <c>/CS /DeviceCMYK</c> group is attempted at the child
    /// path unconditionally, before <c>GroupInvocationNeedsCompositing</c>.</para>
    /// </summary>
    [Fact]
    public void SiteOne_NestedScreenGroupInsideExplicitCmykGroup_MatchesIndependentRenderers()
    {
        RequireOracles();
        var path = WriteTemp(NestedScreenFixture(nested: true));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        var m = DarkFraction(mutool!, InsideObject);
        var g = DarkFraction(gs!, InsideObject);
        m.Should().BeApproximately(g, FractionTolerance,
            $"MuPDF ({m:F2}) and Ghostscript ({g:F2}) must agree before their answer is the target");
        var target = (m + g) / 2;
        target.Should().BeLessThan(0.5,
            "the oracles must read the Screen result, not the raw ink, or the fixture measures "
            + $"nothing (mutool {m:F2}, gs {g:F2})");

        DarkFraction(excise, InsideObject).Should().BeApproximately(target, FractionTolerance,
            "a /BM /Screen non-isolated group nested inside an explicit /CS /DeviceCMYK child "
            + $"group (oracles {m:F2}/{g:F2}, measured -0.131 and -0.128 — LIGHTER than the wash, "
            + "which is what Screen against an inky backdrop means; excise reads -0.133). Before "
            + "#1510 the enclosing child ran the page-flavoured pre-seed sync on its own "
            + "transparent group bitmap, destroyed the seed, and this read exactly 1.000 — the "
            + "object came out BYTE-IDENTICAL to the raw-ink reference patch, unblended");
    }

    /// <summary>
    /// The same defect as a spec property with NO oracle: ISO 32000-1/-2
    /// §11.4.4 NOTE 5 — a non-isolated, non-knockout group composited with the
    /// Normal blend mode at full opacity and no mask is transparent to grouping.
    /// So invoking the <c>/BM /Screen</c> group from inside the explicit
    /// <c>/CS /DeviceCMYK</c> group must produce the same pixel as invoking it
    /// straight from the page.
    ///
    /// <para>The control assertion is what stops the equality being vacuous:
    /// two identically broken renders agree too.</para>
    /// </summary>
    [Fact]
    public void SiteOne_NestedScreenGroupInsideExplicitCmykGroup_IsUnaffectedByTheEnclosingGroup()
    {
        using var grouped = RenderWithExcise(WriteTemp(NestedScreenFixture(nested: true)));
        using var ungrouped = RenderWithExcise(WriteTemp(NestedScreenFixture(nested: false)));

        var ungroupedFraction = DarkFraction(ungrouped, InsideObject);
        ungroupedFraction.Should().BeLessThan(0.25,
            "the control: a /BM /Screen group over an inky backdrop composites to LESS ink than "
            + "the backdrop alone (§11.3.5), so it must read at or below the wash — not at the raw "
            + "source ink (1.0)");

        DarkFraction(grouped, InsideObject).Should().BeApproximately(ungroupedFraction, FractionTolerance,
            "§11.4.4 NOTE 5: the enclosing /CS /DeviceCMYK group is composited Normal at full "
            + "opacity with no mask, so it is transparent to grouping. mutool and Ghostscript render "
            + "the two fixtures byte-identically (measured, both at (177,161,182)). Before #1510 "
            + "the grouped variant read the raw ink at fraction 1.000 while the ungrouped control "
            + "read -0.133");
    }

    /// <summary>
    /// Site 1 passes a REGION to the sync. A region reaching beyond the nested
    /// group's own window would fold group-bitmap pixels into the backdrop
    /// somewhere the group never painted, so compare against the same page with
    /// the nested group not invoked and require that only the object moved.
    /// </summary>
    [Fact]
    public void SiteOne_NestedScreenGroupInsideExplicitCmykGroup_ChangesNothingOutsideTheObject()
    {
        using var with = RenderWithExcise(WriteTemp(NestedScreenFixture(nested: true)));
        using var without = RenderWithExcise(WriteTemp(NestedScreenFixture(nested: true, invoked: false)));

        // The object is PDF x 20..180, y 20..180; padded by two pixels.
        var changed = new SKRectI(18, 18, 182, 182);
        var worst = WorstDifferenceOutside(with, without, changed, out var at);
        worst.Should().BeLessThanOrEqualTo(3,
            $"only the nested object may change; pixel {at} changed by {worst} (#1510)");

        ChannelDistance(
                with.GetPixel(InsideObject.X, InsideObject.Y),
                without.GetPixel(InsideObject.X, InsideObject.Y))
            .Should().BeGreaterThan(20,
                "and the object DID change, so \"nothing else changed\" is not vacuous");
    }

    // ---------------------------------------------------------------- site 2

    /// <summary>
    /// #1510 site 2 — the post-content fold at the same entry. A shading-pattern
    /// fill at <c>/ca 0.5</c> inside an explicit <c>/CS /DeviceCMYK</c> group:
    /// the pattern marks the backdrop dirty TODAY (<c>RenderFillPattern</c>
    /// always did), so this row isolates the sync's FLAVOUR from #1511's missing
    /// dirty mark.
    ///
    /// <para>The page-flavoured sync resolved the group bitmap's alpha 127
    /// against paper, halving the ink, and <c>CompositeDeviceCmykGroupBitmap</c>
    /// then applied the same alpha again — a half-opacity result reached the page
    /// at about a quarter strength. MEASURED on this fixture: the object read
    /// L=193.9 against a calibration patch of L=132.6, i.e. 61.3 luminance
    /// levels too light; with the fix it reads L=136.9, 4.3 levels off the patch.
    /// The oracles read L=140.7 (mutool) and L=139.6 (Ghostscript).</para>
    ///
    /// <para><b>The assertion is against a calibration patch in the same
    /// render</b>, <c>0 0 0 0.55 k</c> = 50% of full black over the 10% wash.
    /// Both oracles render the object within 1.1 luminance levels of that patch,
    /// which the test re-derives rather than trusting; comparing two patches
    /// inside one render cancels that renderer's CMYK-to-RGB preview conversion
    /// exactly, so the tolerance does not have to absorb it.</para>
    ///
    /// <para><b>Luminance, not per-channel max</b>, because the two pixels reach
    /// RGB by different routes: the patch through
    /// <c>TryPaintDeviceCmykBlendPath</c>, the object through Skia and a
    /// <c>RgbToDeviceCmyk</c> round trip that does not return a neutral grey
    /// neutral — measured, excise puts the object 13 levels high on RED while
    /// its luminance is 4.3 off. Ink DENSITY is what the calibration is about
    /// and what the doubled alpha changes (61.3 levels), so a per-channel
    /// tolerance wide enough for the hue drift would have been most of the way
    /// to the defect. A loose per-channel bound is kept as a guard against a
    /// gross hue shift.</para>
    ///
    /// <para>The fixture paints an explicit <c>0 0 0 0.1 k</c> wash over the
    /// whole page for a reason: the page's RETAINED backdrop has to carry alpha 1
    /// under the group. Where it does not, the non-contained entry's composite
    /// has no destination-blend branch (#1395 added one for contained entries
    /// only), and its full-ink result would dominate the measurement.</para>
    /// </summary>
    [Fact]
    public void SiteTwo_HalfOpacityShadingPatternInsideExplicitCmykGroup_MatchesItsCalibrationPatch()
    {
        RequireOracles();
        var path = WriteTemp(HalfOpacityPatternFixture());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        foreach (var (name, oracle) in new[] { ("mutool", mutool!), ("ghostscript", gs!) })
        {
            LuminanceDistanceToCalibrationPatch(oracle, out var oracleObject, out var oraclePatch)
                .Should().BeLessThanOrEqualTo(OracleCalibrationTolerance,
                    $"{name} must render the half-opacity object (L={oracleObject:F1}) as the "
                    + $"0 0 0 0.55 k patch (L={oraclePatch:F1}), or the patch is not a calibration "
                    + "for this fixture and this row proves nothing (measured: 1.1 and 0.0)");
        }

        using var excise = RenderWithExcise(path);
        LuminanceDistanceToCalibrationPatch(excise, out var exciseObject, out var excisePatch)
            .Should().BeLessThanOrEqualTo(PatchLuminanceTolerance,
                $"a /ca 0.5 shading-pattern fill inside an explicit /CS /DeviceCMYK group "
                + $"(L={exciseObject:F1}) must composite to the same ink DENSITY as the "
                + $"0 0 0 0.55 k patch (L={excisePatch:F1}), which both oracles confirm. Before "
                + "#1510 the post-content fold ran the page-flavoured sync on the child's "
                + "transparent group bitmap, the alpha was applied twice, and this read L=193.9 "
                + "against the patch's 132.6 — 61.3 levels too light (measured, both ways)");
        DistanceToCalibrationPatch(excise, out var exciseObjectRgb, out var excisePatchRgb)
            .Should().BeLessThanOrEqualTo(PatchHueGuard,
                $"and the hue must not have drifted grossly either ({exciseObjectRgb} vs "
                + $"{excisePatchRgb}); measured 13, which is the RgbToDeviceCmyk round trip on a "
                + "neutral grey and not the doubled alpha (that is 62 on this metric)");
    }

    // ---------------------------------------------------------------- site 3

    /// <summary>
    /// The third instance of the same defect, found while fixing #1510's two:
    /// the contained Skia-layer fold-back in <c>RenderFormXObjectAtInvocation</c>
    /// also ran the page-flavoured sync, and it runs on <c>this</c> — which is a
    /// CHILD context whenever a nested group's <c>/CS</c> is explicitly not CMYK,
    /// since such a group fails <c>inheritsDeviceCmyk</c> and takes the contained
    /// layer instead of the child path.
    ///
    /// <para><b>Why this row asserts an inequality and not a match.</b> MEASURED:
    /// with the fix excise reads the object at L=132.6, which is its
    /// <c>0 0 0 0.55 k</c> calibration patch to the BYTE (distance 0) — so
    /// excise composites the DeviceRGB group's black as CMYK K=1 at alpha 0.5
    /// over the 0.1 wash, exactly 0.55 ink. mutool and Ghostscript read L=98.2
    /// and L=96.2, i.e. 34-36 levels DARKER, so they do something else with the
    /// DeviceRGB-into-DeviceCMYK group conversion; it is neither excise's answer
    /// nor a plain RGB mix (which would be L≈115). Pinning excise to their number
    /// would pin a colour-conversion difference this lane did not investigate.
    /// What the oracles DO establish, and what this row asserts, is an
    /// inequality: both render the object at least as dark as their own
    /// calibration patch, so excise must too. Before the fix excise read L=188.3,
    /// 55.7 levels LIGHTER than the patch — the double-applied alpha — and that
    /// is what reddens.</para>
    /// </summary>
    [Fact]
    public void SiteThree_NonCmykGroupInsideExplicitCmykGroup_IsNotDoubleLightened()
    {
        RequireOracles();
        var path = WriteTemp(NonCmykNestedGroupFixture());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        foreach (var (name, oracle) in new[] { ("mutool", mutool!), ("ghostscript", gs!) })
        {
            var oracleObject = Luminance(oracle.GetPixel(InsideObject.X, InsideObject.Y));
            var oraclePatch = Luminance(oracle.GetPixel(CalibrationSample.X, CalibrationSample.Y));
            oracleObject.Should().BeLessThan(oraclePatch,
                $"{name} must read the /ca 0.5 DeviceRGB group (L={oracleObject:F1}) at least as "
                + $"dark as the 0 0 0 0.55 k patch (L={oraclePatch:F1}), or the inequality this row "
                + "asserts is not the oracles' answer");
        }

        using var excise = RenderWithExcise(path);
        var exciseObject = Luminance(excise.GetPixel(InsideObject.X, InsideObject.Y));
        var excisePatch = Luminance(excise.GetPixel(CalibrationSample.X, CalibrationSample.Y));
        exciseObject.Should().BeLessThanOrEqualTo(excisePatch + PatchLuminanceTolerance,
            $"a /ca 0.5 DeviceRGB group inside an explicit /CS /DeviceCMYK group "
            + $"(L={exciseObject:F1}) must be at least as dark as the 0 0 0 0.55 k patch "
            + $"(L={excisePatch:F1}), which both oracles bound from below (they read 98.2 and "
            + "96.2, darker still). Before the fix the contained-layer fold-back ran the "
            + "page-flavoured sync on the enclosing child's transparent group bitmap, applying "
            + "the layer's alpha twice, and this read L=188.3 — 55.7 levels lighter than the "
            + "patch (measured, both ways)");
    }

    // ------------------------------------------------------------------ #1511

    /// <summary>
    /// #1511 — the <c>sh</c> operator never called
    /// <c>MarkBackdropDirtyFromRgbPaint</c>, so inside the pre-#1395 entry,
    /// whose fold is dirty-flag gated, an <c>sh</c> was never folded into the
    /// child's retained CMYK backdrop. <c>CompositeDeviceCmykGroupBitmap</c>
    /// takes the group's ALPHA from the group bitmap (which the shading painted)
    /// and its COLOUR from that backdrop (which it did not), so the shading
    /// composited as whatever the backdrop held — here the page wash, i.e.
    /// invisible.
    ///
    /// <para>Full alpha deliberately: at bitmap alpha 255 the two syncs are
    /// identical, so this row moves on the dirty mark alone and not on #1510's
    /// flavour change.</para>
    /// </summary>
    [Fact]
    public void ShOperatorInsideExplicitCmykGroup_MatchesIndependentRenderers()
    {
        RequireOracles();
        var path = WriteTemp(ShadingOperatorFixture());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        var m = DarkFraction(mutool!, InsideObject);
        var g = DarkFraction(gs!, InsideObject);
        m.Should().BeApproximately(g, FractionTolerance,
            $"MuPDF ({m:F2}) and Ghostscript ({g:F2}) must agree before their answer is the target");
        var target = (m + g) / 2;
        target.Should().BeGreaterThan(0.75,
            "the oracles must read the shading at its own ink — the reference patch is painted in "
            + $"that same ink, so a correct render is at fraction 1.0 (mutool {m:F2}, gs {g:F2})");

        DarkFraction(excise, InsideObject).Should().BeApproximately(target, FractionTolerance,
            $"an `sh` inside an explicit /CS /DeviceCMYK group (oracles {m:F2}/{g:F2}, both "
            + "measured 1.00 — the shading reads as its own ink; excise reads 1.017). Before "
            + "#1511 `sh` marked nothing dirty, the child's fold never ran, and the composite "
            + "read the seeded wash as the group's colour: fraction exactly 0.000, the object "
            + "BYTE-IDENTICAL to the wash, i.e. the shading invisible (measured)");
    }

    /// <summary>
    /// The same defect with no oracle at all, because "the shading is invisible"
    /// is a statement about this render alone: the square must not read as the
    /// wash it sits on, and it must be NEARER the shading's own ink than that
    /// wash.
    ///
    /// <para>Deliberately a comparison and not a tolerance. The two pixels reach
    /// RGB by different routes — the reference patch through
    /// <c>TryPaintDeviceCmykBlendPath</c>'s <c>DeviceCmykToRgb</c>, the shading
    /// through <c>ComponentsToSkColor</c>, Skia, then
    /// <c>RgbToDeviceCmyk</c>/<c>Set</c>/<c>DeviceCmykToRgb</c> — and #1505
    /// already recorded that round trip moving a yellow component from 0.1 to 0.
    /// Requiring the two within a few levels would make this row a colour-
    /// conversion test; "which of the two is it closer to" is a 140-level gap
    /// and is the #1511 question. The oracle row next to this one carries the
    /// absolute answer.</para>
    /// </summary>
    [Fact]
    public void ShOperatorInsideExplicitCmykGroup_DrawsTheShadingAndNotTheBackdrop()
    {
        using var excise = RenderWithExcise(WriteTemp(ShadingOperatorFixture()));

        var obj = excise.GetPixel(InsideObject.X, InsideObject.Y);
        var wash = excise.GetPixel(WashSample.X, WashSample.Y);
        var ink = excise.GetPixel(ReferenceSample.X, ReferenceSample.Y);

        ChannelDistance(wash, ink).Should().BeGreaterThan(80,
            $"the wash ({wash}) and the shading's ink ({ink}) must be far apart, or this row "
            + "cannot tell them apart");
        ChannelDistance(obj, wash).Should().BeGreaterThan(40,
            $"the `sh` square ({obj}) must not read as the wash it sits on ({wash}) — that is what "
            + "a shading that was never folded into the child's CMYK backdrop looks like, and "
            + "before #1511 the two were BYTE-IDENTICAL (measured: distance 0)");
        ChannelDistance(obj, ink).Should().BeLessThan(ChannelDistance(obj, wash),
            $"and it must read nearer the shading's own ink ({ink}) than the wash ({wash}), which "
            + "the page paints as a reference patch in the shading's colour. Measured with the "
            + "fix: 20 from the ink, 164 from the wash — the 20 is the CMYK-RGB-CMYK round trip "
            + "flattening the 0.1 yellow component, which is why this is a comparison and not a "
            + "tolerance");
    }

    // ------------------------------------------------------------------ setup

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
    }

    /// <summary>
    /// Where the sample sits between this renderer's OWN wash (0) and this
    /// renderer's OWN opaque patch of the object's raw ink (1), by luminance.
    /// Keeps the comparison independent of each renderer's CMYK-to-RGB preview
    /// conversion. Same metric and same geometry as
    /// <see cref="DeviceCmykNestedNonIsolatedGroupBlendTests"/>.
    /// </summary>
    private static double DarkFraction(SKBitmap bitmap, SKPointI at)
    {
        var wash = Luminance(bitmap.GetPixel(WashSample.X, WashSample.Y));
        var raw = Luminance(bitmap.GetPixel(ReferenceSample.X, ReferenceSample.Y));
        var span = wash - raw;
        span.Should().BeGreaterThan(80,
            "the reference patch must read much darker than the wash, or the fixture measures nothing");
        return (wash - Luminance(bitmap.GetPixel(at.X, at.Y))) / span;
    }

    /// <summary>
    /// Worst per-channel distance between the half-opacity object and the
    /// calibration patch IN THE SAME RENDER, so the renderer's own CMYK-to-RGB
    /// conversion cancels.
    /// </summary>
    private static int DistanceToCalibrationPatch(SKBitmap bitmap, out SKColor sample, out SKColor patch)
    {
        sample = bitmap.GetPixel(InsideObject.X, InsideObject.Y);
        patch = bitmap.GetPixel(CalibrationSample.X, CalibrationSample.Y);
        return ChannelDistance(sample, patch);
    }

    /// <summary>
    /// Luminance gap between the object and the calibration patch IN THE SAME
    /// RENDER, so the renderer's own CMYK-to-RGB conversion cancels.
    /// </summary>
    private static double LuminanceDistanceToCalibrationPatch(
        SKBitmap bitmap, out double sample, out double patch)
    {
        sample = Luminance(bitmap.GetPixel(InsideObject.X, InsideObject.Y));
        patch = Luminance(bitmap.GetPixel(CalibrationSample.X, CalibrationSample.Y));
        return Math.Abs(sample - patch);
    }

    private static double Luminance(SKColor c)
        => (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);

    private static int WorstDifferenceOutside(SKBitmap a, SKBitmap b, SKRectI excluded, out SKPointI at)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);
        var worst = 0;
        at = default;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (x >= excluded.Left && x < excluded.Right && y >= excluded.Top && y < excluded.Bottom)
                    continue;

                var d = ChannelDistance(a.GetPixel(x, y), b.GetPixel(x, y));
                if (d > worst)
                {
                    worst = d;
                    at = new SKPointI(x, y);
                }
            }
        }

        return worst;
    }

    private static int ChannelDistance(SKColor a, SKColor b)
        => Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    // --------------------------------------------------------------- fixtures

    /// <summary>
    /// Site 1. A 400x200 pt page whose <c>/Group</c> is <c>/CS /DeviceCMYK</c>,
    /// washed <c>0.2 0.3 0.5 0.2 k</c> (opaque and four-component, so
    /// <c>a0 = 1</c> and no Screen component is degenerate), with an opaque
    /// <c>0.9 0.9 0.1 0.8 k</c> reference patch in the nested group's own ink.
    ///
    /// <para><paramref name="nested"/> true invokes the <c>/BM /Screen</c> group
    /// from inside <c>Fm0</c>, whose <c>/Group</c> carries an EXPLICIT
    /// <c>/CS /DeviceCMYK</c> — the one key that routes it to the pre-#1395
    /// entry. False invokes it straight from the page, which §11.4.4 NOTE 5 says
    /// is the same compositing, and which both oracles confirm byte for byte.
    /// </para>
    /// </summary>
    private static byte[] NestedScreenFixture(bool nested, bool invoked = true)
    {
        const string inner = "0.9 0.9 0.1 0.8 k 20 20 160 160 re f\n";
        const string outer = "q /GSs gs /Fm1 Do Q\n";

        var pageContent = new StringBuilder()
            .Append("0.2 0.3 0.5 0.2 k 0 0 400 200 re f\n")
            .Append("0.9 0.9 0.1 0.8 k 310 110 80 80 re f\n");
        if (invoked)
            pageContent.Append(nested ? "q /Fm0 Do Q\n" : "q /GSs gs /Fm1 Do Q\n");
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /ExtGState << /GSs 6 0 R >> /XObject << /Fm0 7 0 R /Fm1 8 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState >>\nendobj\n",
            "6 0 obj\n<< /Type /ExtGState /BM /Screen >>\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 400 200] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I false >> "
                + "/Resources << /ExtGState << /GSs 6 0 R >> /XObject << /Fm1 8 0 R >> >> "
                + $"/Length {outer.Length} >>\nstream\n{outer}\nendstream\nendobj\n",
            "8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /I false >> "
                + "/Resources << >> "
                + $"/Length {inner.Length} >>\nstream\n{inner}\nendstream\nendobj\n",
        });
    }

    /// <summary>
    /// #1511. The same page, with an explicit <c>/CS /DeviceCMYK</c> group whose
    /// only content is <c>/Sh0 sh</c> clipped to the object rectangle. The
    /// shading is a CONSTANT DeviceCMYK axial in the reference patch's own ink,
    /// so a correct render puts the object at fraction 1.0 and the failure —
    /// the seeded wash — at 0.0.
    /// </summary>
    private static byte[] ShadingOperatorFixture()
    {
        const string inner = "q 20 20 160 160 re W n /Sh0 sh Q\n";

        var pageContent = new StringBuilder()
            .Append("0.2 0.3 0.5 0.2 k 0 0 400 200 re f\n")
            .Append("0.9 0.9 0.1 0.8 k 310 110 80 80 re f\n");
        pageContent.Append("q /Fm0 Do Q\n");
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /XObject << /Fm0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I false >> "
                + "/Resources << /Shading << /Sh0 6 0 R >> >> "
                + $"/Length {inner.Length} >>\nstream\n{inner}\nendstream\nendobj\n",
            "6 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [20 0 180 0] "
                + "/Extend [true true] /Function 7 0 R >>\nendobj\n",
            "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0.9 0.9 0.1 0.8] "
                + "/C1 [0.9 0.9 0.1 0.8] /N 1 >>\nendobj\n",
        });
    }

    /// <summary>
    /// Site 2. An ISOLATED explicit <c>/CS /DeviceCMYK</c> group whose only
    /// content is a shading-pattern fill at <c>/ca 0.5</c>. Isolated so no seed
    /// and no §11.4.4 removal enter the measurement; a shading PATTERN rather
    /// than <c>sh</c> so the backdrop is marked dirty with or without #1511, and
    /// the row moves on the sync's flavour alone.
    ///
    /// <para>The <c>0 0 0 0.1 k</c> wash is load-bearing — see the test's own
    /// remarks — and the <c>0 0 0 0.55 k</c> patch is the calibration.</para>
    /// </summary>
    private static byte[] HalfOpacityPatternFixture()
    {
        const string inner = "/Pattern cs /P0 scn q /GSh gs 20 20 160 160 re f Q\n";

        var pageContent = new StringBuilder()
            .Append("0 0 0 0.1 k 0 0 400 200 re f\n")
            .Append("0 0 0 1 k 310 110 80 80 re f\n")
            .Append("0 0 0 0.55 k 220 110 60 80 re f\n");
        pageContent.Append("q /Fm0 Do Q\n");
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /XObject << /Fm0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I true >> "
                + "/Resources << /ExtGState << /GSh 6 0 R >> /Pattern << /P0 7 0 R >> >> "
                + $"/Length {inner.Length} >>\nstream\n{inner}\nendstream\nendobj\n",
            "6 0 obj\n<< /Type /ExtGState /ca 0.5 /CA 0.5 >>\nendobj\n",
            "7 0 obj\n<< /PatternType 2 /Shading 8 0 R >>\nendobj\n",
            "8 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [20 0 180 0] "
                + "/Extend [true true] /Function 9 0 R >>\nendobj\n",
            "9 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 1] /C1 [0 0 0 1] /N 1 >>\nendobj\n",
        });
    }

    /// <summary>
    /// Site 3. The same isolated explicit <c>/CS /DeviceCMYK</c> group, this time
    /// containing a nested group whose <c>/CS</c> is explicitly
    /// <c>/DeviceRGB</c>, invoked at <c>/ca 0.5</c>. A non-CMYK <c>/CS</c> fails
    /// <c>inheritsDeviceCmyk</c>, so the nested group takes the CONTAINED SKIA
    /// LAYER and its fold-back — which runs on the enclosing child's transparent
    /// group bitmap.
    /// </summary>
    private static byte[] NonCmykNestedGroupFixture()
    {
        const string inner = "q /GSh gs /FmRgb Do Q\n";
        const string rgb = "0 0 0 rg 20 20 160 160 re f\n";

        var pageContent = new StringBuilder()
            .Append("0 0 0 0.1 k 0 0 400 200 re f\n")
            .Append("0 0 0 1 k 310 110 80 80 re f\n")
            .Append("0 0 0 0.55 k 220 110 60 80 re f\n");
        pageContent.Append("q /Fm0 Do Q\n");
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /XObject << /Fm0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I true >> "
                + "/Resources << /ExtGState << /GSh 6 0 R >> /XObject << /FmRgb 7 0 R >> >> "
                + $"/Length {inner.Length} >>\nstream\n{inner}\nendstream\nendobj\n",
            "6 0 obj\n<< /Type /ExtGState /ca 0.5 /CA 0.5 >>\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceRGB /I true >> "
                + "/Resources << >> "
                + $"/Length {rgb.Length} >>\nstream\n{rgb}\nendstream\nendobj\n",
        });
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
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-1510-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
        GC.SuppressFinalize(this);
    }
}
