using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1505 — a NON-isolated transparency group invoked with a non-Normal
/// <c>/BM</c> from INSIDE another DeviceCMYK child group must blend against the
/// enclosing group's seeded backdrop, not against nothing.
///
/// <para><b>The mechanism this pins.</b> Entering a non-isolated group under a
/// non-Normal invocation blend, <c>TryRenderDeviceCmykFormGroup</c> first
/// reconciles the current context's retained CMYK backdrop with what is
/// actually in that context's bitmap, so the blend inside the child acts on the
/// real backdrop. Until #1505 it always used the PAGE-flavoured sync
/// (<c>SyncDeviceCmykBackdropFromRootBitmap</c>), which resolves partial alpha
/// against paper. That is right on the page — a page starting in a DeviceCMYK
/// group is cleared to the paper colour, so every pixel has alpha 255 — and
/// wrong inside a #1395 child, whose group bitmap starts
/// <c>Clear(Transparent)</c>: it read alpha 0 / RGB (0,0,0) on every unpainted
/// pixel, found it far outside its own 12-unit skip threshold, and OVERWROTE
/// the non-isolated seed with zero ink at alpha 0. With the seed gone,
/// <c>BlendDeviceCmykWithBackdropAlpha</c> correctly short-circuits a
/// zero-alpha backdrop to the source itself — so the nested group's RAW colour
/// reached the page with no blend at all.</para>
///
/// <para><b>Why no existing fixture could see it.</b> The nesting requires a
/// child group inside a child group, and before #1395 the only DeviceCMYK entry
/// that opened a child context was an unmasked <c>/CS /DeviceCMYK</c> group. On
/// pdf.js <c>issue13520.pdf</c> neither the outer group nor its two nested ones
/// declare a <c>/CS</c>, so on develop all three went through the Skia layer and
/// the defect had no way to exist. <see cref="DeviceCmykNonIsolatedGroupBackdropRemovalTests"/>
/// nests nothing, so its non-Normal row runs the sync on the PAGE, where it is
/// correct.</para>
///
/// <para><b>The measurement is normalised per renderer</b>, so no CMYK-to-RGB
/// preview conversion is compared: each render is read as a position between
/// its OWN wash and its OWN opaque patch of the nested group's raw ink.
/// A correct Screen blend lands slightly LIGHTER than the wash (fraction
/// about -0.17); the unblended raw colour lands ON the reference patch
/// (fraction 1.0). The two answers are 1.1 apart on a metric whose tolerance
/// is 0.2, so no row here can pass both ways.</para>
///
/// <para><b>Oracles.</b> mutool AND Ghostscript, never pdftocairo — on
/// issue13520 pdftocairo renders 103 dark pixels in the lobe where mutool and
/// Ghostscript both render 0, because it shares excise's isolated-group defect
/// (#1373/#1394). Agreeing with it would have been a warning, not a
/// result.</para>
/// </summary>
public class DeviceCmykNestedNonIsolatedGroupBlendTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>
    /// Agreement tolerance on the normalised dark fraction. The right and wrong
    /// answers are about 1.1 apart, so this cannot straddle them.
    /// </summary>
    private const double FractionTolerance = 0.2;

    // Device pixels at 72 dpi: x = PDF x, y = 200 - PDF y.
    private static readonly SKPointI InsideObject = new(100, 100);
    private static readonly SKPointI WashSample = new(350, 150);
    private static readonly SKPointI ReferenceSample = new(350, 50);

    private readonly List<string> _temp = new();

    /// <summary>How the innermost group puts its dark ink down.</summary>
    public enum InnerContent
    {
        /// <summary>
        /// A DeviceCMYK <c>re f</c>. Takes <c>TryPaintDeviceCmykBlendPath</c>, so
        /// the colour reaches the innermost child's retained CMYK backdrop
        /// directly.
        /// </summary>
        CmykFill,

        /// <summary>
        /// A DeviceCMYK axial shading through <c>sh</c> — issue13520's own shape.
        /// Shadings are painted by Skia as RGB and never touch the CMYK backdrop,
        /// so this row additionally depends on
        /// <c>SyncDeviceCmykGroupBackdropFromGroupBitmap</c> folding them back in.
        /// The RGB round trip moves the yellow component (0.1 to 0), which the
        /// tolerance absorbs and the 1.1 gap dwarfs.
        /// </summary>
        CmykShading,
    }

    /// <summary>
    /// ISO 32000-1/-2 §11.4.4 NOTE 5, as a property with NO oracle: a
    /// non-isolated, non-knockout group whose result is composited with the
    /// Normal blend mode at full opacity and no mask is transparent to grouping
    /// — "the effect of compositing objects as a group is the same as that of
    /// compositing them separately". The objects INSIDE may use any blend mode,
    /// because either way they see the same backdrop.
    ///
    /// <para>So wrapping the <c>/BM /Screen</c> group in an outer non-isolated
    /// group must not change the pixel. The outer invocation carries
    /// <c>/ca 0.99</c> rather than 1 only because <c>/ca</c> 1 with no mask and
    /// a Normal blend fails <c>GroupInvocationNeedsCompositing</c> and takes the
    /// plain Skia layer, where no CMYK backdrop is seeded at all and the defect
    /// cannot arise; 0.99 reaches #1395's child path while leaving the
    /// equivalence intact to within a percent.</para>
    ///
    /// <para>The second assertion is what stops the equality being vacuous: two
    /// identically broken renders agree too. Both variants must land LIGHTER
    /// than the wash, which is what a Screen blend against an inky backdrop
    /// means and what the raw unblended ink (fraction 1.0) is not.</para>
    /// </summary>
    [Theory]
    [InlineData(InnerContent.CmykFill)]
    [InlineData(InnerContent.CmykShading)]
    public void NestedNonIsolatedScreenGroup_IsUnaffectedByTheEnclosingGroup(InnerContent inner)
    {
        using var grouped = RenderWithExcise(WriteTemp(Fixture(inner, nested: true)));
        using var ungrouped = RenderWithExcise(WriteTemp(Fixture(inner, nested: false)));

        var ungroupedFraction = DarkFraction(ungrouped, InsideObject);
        ungroupedFraction.Should().BeLessThan(0.25,
            "the control: a /BM /Screen group over an inky backdrop composites to LESS ink than "
            + "the backdrop alone (result ink = backdrop x source on the complemented components, "
            + "§11.3.5), so it must read at or below the wash — not at the raw source ink (1.0)");

        DarkFraction(grouped, InsideObject).Should().BeApproximately(ungroupedFraction, FractionTolerance,
            "§11.4.4 NOTE 5: an enclosing non-isolated group composited Normal at full opacity is "
            + "transparent to grouping. Before #1505 the enclosing child's pre-seed sync wiped its "
            + "own seeded backdrop to alpha 0, the nested Screen blend short-circuited to the raw "
            + "source, and this read about 1.0 — the unblended ink");
    }

    /// <summary>
    /// The absolute answer, from two independent renderers. The property test
    /// above only constrains the nested render relative to the ungrouped one; a
    /// change that moved BOTH would satisfy it and fail this.
    /// </summary>
    [Theory]
    [InlineData(InnerContent.CmykFill)]
    [InlineData(InnerContent.CmykShading)]
    public void NestedNonIsolatedScreenGroup_MatchesIndependentRenderers(InnerContent inner)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(inner, nested: true));

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
            "a /BM /Screen non-isolated group nested inside a non-isolated DeviceCMYK child group, "
            + $"inner content {inner} (oracles {m:F2}/{g:F2}). Before #1505 excise read about 1.0 "
            + "— the nested group's raw ink, unblended (#1505)");
    }

    /// <summary>
    /// #1505 gave the group-flavoured sync a region overload. A region that
    /// reached beyond the nested group's own window would fold group-bitmap
    /// pixels into the backdrop somewhere the group never painted, so compare
    /// against the same page with the group not invoked at all and require that
    /// only the object moved.
    /// </summary>
    [Fact]
    public void NestedNonIsolatedScreenGroup_ChangesNothingOutsideTheObject()
    {
        using var with = RenderWithExcise(WriteTemp(Fixture(InnerContent.CmykFill, nested: true)));
        using var without = RenderWithExcise(WriteTemp(
            Fixture(InnerContent.CmykFill, nested: true, invoked: false)));

        // The object is PDF x 20..180, y 20..180; padded by two pixels.
        var changed = new SKRectI(18, 18, 182, 182);
        var worst = WorstDifferenceOutside(with, without, changed, out var at);
        worst.Should().BeLessThanOrEqualTo(3,
            $"only the nested object may change; pixel {at} changed by {worst} (#1505)");

        ChannelDistance(
                with.GetPixel(InsideObject.X, InsideObject.Y),
                without.GetPixel(InsideObject.X, InsideObject.Y))
            .Should().BeGreaterThan(20,
                "and the object DID change, so \"nothing else changed\" is not vacuous");
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
    }

    /// <summary>
    /// Where the sample sits between this renderer's OWN wash (0) and this
    /// renderer's OWN opaque patch of the nested group's raw ink (1), by
    /// luminance. Negative means lighter than the wash, which is what a Screen
    /// blend against an inky backdrop produces. Keeps the comparison
    /// independent of each renderer's CMYK-to-RGB preview conversion.
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

    /// <summary>
    /// A 400x200 pt page whose <c>/Group</c> is <c>/CS /DeviceCMYK</c>, washed
    /// <c>0.2 0.3 0.5 0.2 k</c> (an opaque, four-component backdrop, so
    /// <c>a0 = 1</c> and every Screen component is non-degenerate), with an
    /// opaque <c>0.9 0.9 0.1 0.8 k</c> reference patch — the nested group's raw
    /// ink, for the normalisation.
    ///
    /// <para><paramref name="nested"/> true invokes the inner <c>/BM /Screen</c>
    /// group from inside an outer non-isolated group at <c>/ca 0.99</c>, which
    /// is the shape of issue13520's <c>Fm2</c>; false invokes it straight from
    /// the page, which is the same compositing per §11.4.4 NOTE 5 and the
    /// control. Both groups are <c>/I false</c> with no <c>/CS</c> — inheriting
    /// the CMYK page — because that is what the fixture in the field does and
    /// what routes to #1395's child-context entries.</para>
    /// </summary>
    private static byte[] Fixture(InnerContent inner, bool nested, bool invoked = true)
    {
        var innerContent = inner switch
        {
            InnerContent.CmykFill => "0.9 0.9 0.1 0.8 k 20 20 160 160 re f\n",
            InnerContent.CmykShading => "q 20 20 160 160 re W n /Sh0 sh Q\n",
            _ => throw new ArgumentOutOfRangeException(nameof(inner)),
        };

        const string outerContent = "q /GSs gs /Fm1 Do Q\n";

        var pageContent = new StringBuilder()
            .Append("0.2 0.3 0.5 0.2 k 0 0 400 200 re f\n")
            .Append("0.9 0.9 0.1 0.8 k 310 110 80 80 re f\n");
        if (invoked)
        {
            pageContent.Append(nested
                ? "q /GSa gs /Fm0 Do Q\n"
                : "q /GSs gs /Fm1 Do Q\n");
        }

        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /ExtGState << /GSa 5 0 R /GSs 6 0 R >> "
                + "/XObject << /Fm0 7 0 R /Fm1 8 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            // Near-1 so the outer group reaches #1395's child path (a Normal,
            // unmasked, /ca 1 invocation does not) while staying within a
            // percent of NOTE 5's "composited with Normal blend and opacity 1".
            "5 0 obj\n<< /Type /ExtGState /ca 0.99 /CA 0.99 >>\nendobj\n",
            "6 0 obj\n<< /Type /ExtGState /BM /Screen >>\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 400 200] "
                + "/Group << /S /Transparency /I false >> "
                + "/Resources << /ExtGState << /GSs 6 0 R >> /XObject << /Fm1 8 0 R >> >> "
                + $"/Length {outerContent.Length} >>\nstream\n{outerContent}\nendstream\nendobj\n",
            "8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /I false >> "
                + "/Resources << /Shading << /Sh0 9 0 R >> >> "
                + $"/Length {innerContent.Length} >>\nstream\n{innerContent}\nendstream\nendobj\n",
            // Constant across the domain: the row is about the blend, not about
            // gradient interpolation, and a constant keeps the sampled pixel's
            // expected value the same as the CmykFill row's.
            "9 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [20 0 180 0] "
                + "/Extend [true true] /Function 10 0 R >>\nendobj\n",
            "10 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0.9 0.9 0.1 0.8] "
                + "/C1 [0.9 0.9 0.1 0.8] /N 1 >>\nendobj\n",
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-1505-{Guid.NewGuid():N}.pdf");
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
