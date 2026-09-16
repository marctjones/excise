using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1504 — ISO 32000-1/-2 §11.4.4: a NON-isolated transparency group's initial
/// backdrop must be REMOVED from the group's result before the result is
/// composited onto that same backdrop, or the backdrop is counted twice.
///
/// <para><b>Why the existing fixtures are blind to this.</b>
/// <see cref="DeviceCmykSoftMaskedGroupTests"/> and the four probes recorded in
/// <c>SeedNonIsolatedGroupBackdrop</c>'s doc comment all paint an OPAQUE object
/// inside the group. The group alpha <c>agn</c> is then 1, the removal factor
/// <c>a0/agn - a0</c> is 0, and removal is a no-op by construction — seeding
/// with no removal looks exactly right. The discriminating variable is
/// PARTIAL alpha INSIDE the group over a NON-EMPTY backdrop, which is what
/// every fixture here has and no fixture had before.</para>
///
/// <para><b>The fixture</b> is the #1395 harness with one thing changed: the
/// group's fill carries <c>/ca 0.5</c>. A 400x200 pt page with
/// <c>/Group &lt;&lt; /S /Transparency /CS /DeviceCMYK &gt;&gt;</c> is washed
/// yellow (<c>0 0 0.4 0 k</c>, which makes the retained CMYK backdrop opaque
/// yellow, so <c>a0 = 1</c> and <c>C0</c> is not paper). An opaque cyan
/// reference patch is painted directly on the page. A form group then paints
/// cyan at <c>/ca 0.5</c>, so <c>agn = 0.5</c>.</para>
///
/// <para><b>Arithmetic, traced through excise's own retained backdrop</b>, for
/// the plain non-isolated row. Seed <c>C0 = (0, 0, 0.4, 0)</c> at
/// <c>a0 = 1</c>; the fill composites cyan at 0.5, giving
/// <c>Cn = (0.5, 0, 0.2, 0)</c> and <c>an = 1</c> while the group bitmap's
/// alpha stays <c>agn = 0.5</c>. Removal gives
/// <c>2*Cn - C0 = (1, 0, 0, 0)</c> — pure cyan, the colour the group would
/// have had in isolation — and compositing that at 0.5 onto the wash yields
/// <c>(0.5, 0, 0.2, 0)</c>, red 0.5, a cyan fraction of 0.5. WITHOUT removal
/// the composite uses <c>Cn</c> itself and yields <c>(0.25, 0, 0.3, 0)</c>,
/// red 0.75, a cyan fraction of 0.25 — half the ink, about 64 red levels
/// adrift.</para>
///
/// <para><b>Every row separates right from wrong by more than its own
/// tolerance</b>, which is not automatic and is why the soft mask and the
/// invocation alpha are 0.9 rather than 0.5. The un-removed answer is the
/// correct one scaled by <c>agn</c>, so the gap is <c>0.5 x (the other
/// factors)</c>: at mask 0.5 and invocation <c>/ca 0.5</c> the gap would be
/// 0.125, INSIDE the 0.15 <c>FractionTolerance</c>, and those oracle rows
/// would pass whether or not removal happened. At 0.9 the gap is 0.225.</para>
///
/// <para><b>Oracles.</b> mutool AND Ghostscript, never pdftocairo alone —
/// pdftocairo shares excise's isolated-group defect and agreeing with it is a
/// warning rather than a result (#1373, #1394). Colour management is not
/// compared: each renderer is read against ITS OWN wash and reference patch.
/// The isolated-vs-non-isolated row needs no oracle at all; it is §11.4.4
/// NOTE 5 as a property.</para>
/// </summary>
public class DeviceCmykNonIsolatedGroupBackdropRemovalTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>Oracle-to-oracle and excise-to-oracle agreement on a cyan fraction.</summary>
    private const double FractionTolerance = 0.15;

    // Device pixels at 72 dpi: x = PDF x, y = 200 - PDF y.
    private static readonly SKPointI InsideObject = new(100, 100);
    private static readonly SKPointI RevealedHalf = new(60, 100);
    private static readonly SKPointI WashSample = new(350, 190);
    private static readonly SKPointI ReferenceSample = new(350, 50);

    private readonly List<string> _temp = new();

    /// <summary>Which of the four DeviceCMYK group entries the row exercises.</summary>
    public enum Invocation
    {
        /// <summary>Plain <c>Do</c>. With <c>/CS /DeviceCMYK</c> this is the pre-#1395 unmasked entry.</summary>
        Plain,

        /// <summary>A luminosity <c>/SMask</c> — #1395's soft-masked entry, and issue13520's shape.</summary>
        SoftMask,

        /// <summary>Invocation <c>/ca 0.5</c> — the group's own constant alpha, on top of the content's.</summary>
        ConstantAlpha,

        /// <summary>
        /// Invocation <c>/BM /Multiply</c>. #1504 DELETED the routing rule that sent a
        /// non-isolated group with a non-Normal invocation blend to the contained Skia
        /// layer (it existed only because removal did not), so this row is the one that
        /// pins the deletion: it must take the child path and be removed there.
        /// </summary>
        BlendMultiply,
    }

    public enum GroupSpace
    {
        /// <summary>Explicit <c>/CS /DeviceCMYK</c>.</summary>
        DeviceCmyk,

        /// <summary>Absent, inheriting the CMYK page — #1395's child-context entries.</summary>
        Absent,
    }

    /// <summary>
    /// §11.4.4 NOTE 5, as a property with NO oracle: for a group whose contents
    /// use the Normal blend mode and whose result is composited with Normal
    /// blend and shape/opacity 1, "the effect of compositing objects as a group
    /// is the same as that of compositing them separately". An ISOLATED group's
    /// contents composite onto nothing and so are already free of the backdrop;
    /// a non-isolated group's are not, and removal is what makes the two agree.
    ///
    /// <para>This is the sharpest available discriminator. Without removal the
    /// non-isolated group reads half the ink of its isolated twin — the
    /// backdrop counted twice — and no oracle, colour profile or tolerance
    /// choice is involved in seeing it.</para>
    ///
    /// <para><paramref name="expectedIsolated"/> pins the isolated twin's own
    /// value too, so a change that moved BOTH renders cannot pass by making
    /// two wrong answers agree. ⚠️ Those figures are DERIVED from the clause
    /// (content <c>/ca 0.5</c> times the invocation's factors), not measured on
    /// this branch — if the twin itself is off, suspect #1395's contained-entry
    /// composite before suspecting removal, and see
    /// <see cref="IsolatedGroup_WithPartialAlphaContent_IsUnaffectedByRemoval"/>.</para>
    ///
    /// <para>The <c>/BM /Multiply</c> row passes 0 for it, meaning "only
    /// require that the twin painted something". Its absolute value depends on
    /// whether excise blends DeviceCMYK additively on the complemented
    /// components or subtractively on the components themselves — a separate,
    /// pre-existing modelling question owned by
    /// <c>DeviceCmykBlendPathRenderTests</c> and the gwg160-162 contracts.
    /// Pinning it here, or against an oracle, would import that question into a
    /// #1504 gate. This row's job is narrower and is fully served by the
    /// equality: a non-isolated group with a non-Normal INVOCATION blend now
    /// reaches the child path at all (#1504 deleted the routing rule that sent
    /// it to the contained Skia layer), and removal happens BEFORE the blend.
    /// Removing after it would give <c>Blend(C0, Cn)</c> extrapolated rather
    /// than <c>Blend(C0, Cs)</c>, which is not the isolated twin's answer.</para>
    ///
    /// <para><b>Routing.</b> Each row is chosen so the group actually reaches
    /// the child-context path. <c>/CS</c> absent with a plain <c>Do</c> is
    /// deliberately NOT a row: it fails <c>GroupInvocationNeedsCompositing</c>
    /// and takes the plain Skia layer, where the contents paint straight onto
    /// the page and no backdrop is ever seeded — correct already, and blind to
    /// this change.</para>
    /// </summary>
    [Theory]
    // invocation,                 /CS,                   expected isolated fraction
    [InlineData(Invocation.Plain, GroupSpace.DeviceCmyk, 0.50)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.DeviceCmyk, 0.45)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.Absent, 0.45)]
    [InlineData(Invocation.SoftMask, GroupSpace.DeviceCmyk, 0.45)]
    [InlineData(Invocation.SoftMask, GroupSpace.Absent, 0.45)]
    [InlineData(Invocation.BlendMultiply, GroupSpace.Absent, 0.0)]
    public void NonIsolatedGroup_WithPartialAlphaContent_RendersAsTheIsolatedGroupDoes(
        Invocation invocation, GroupSpace space, double expectedIsolated)
    {
        using var nonIsolated = RenderWithExcise(WriteTemp(Fixture(invocation, space, isolated: false)));
        using var isolated = RenderWithExcise(WriteTemp(Fixture(invocation, space, isolated: true)));

        var sample = invocation == Invocation.SoftMask ? RevealedHalf : InsideObject;
        var isolatedFraction = CyanFraction(isolated, sample);

        // The fixture must actually be measuring a partly covered object, or
        // "the two agree" is vacuous — two identically wrong renders agree too.
        if (expectedIsolated > 0)
        {
            isolatedFraction.Should().BeApproximately(expectedIsolated, 0.06,
                "the isolated twin's own value: content /ca 0.5 through this invocation");
        }
        else
        {
            isolatedFraction.Should().BeGreaterThan(0.15,
                "the isolated twin must have painted something for the comparison to mean anything");
        }

        CyanFraction(nonIsolated, sample).Should().BeApproximately(isolatedFraction, 0.03,
            "removal exists to make a non-isolated group's SOURCE COLOUR equal what an "
            + "isolated group would have produced, so with Normal-blend content the two "
            + "composite identically (§11.4.4). Without removal the non-isolated group "
            + "carries the backdrop into the composite and reads about half of this (#1504)");
    }

    /// <summary>
    /// The same partial-alpha non-isolated group against mutool AND
    /// Ghostscript. The oracles fix the absolute answer that the property test
    /// above only constrains relatively — a change that moved BOTH the isolated
    /// and non-isolated renders would satisfy that one and fail this.
    ///
    /// <para>Rows are Normal-invocation-blend only, so the measurement does not
    /// depend on excise's DeviceCMYK blend-space convention (see the property
    /// test's note). <c>Plain</c> + <c>/CS /DeviceCMYK</c> is the pre-#1395
    /// unmasked entry; <c>ConstantAlpha</c> + <c>/CS</c> absent is #1395's
    /// child-context PlainGroup entry.</para>
    /// </summary>
    [Theory]
    [InlineData(Invocation.Plain, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.Absent)]
    public void NonIsolatedGroup_WithPartialAlphaContent_MatchesIndependentRenderers(
        Invocation invocation, GroupSpace space)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(invocation, space, isolated: false));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, InsideObject,
            $"a non-isolated group painting cyan at /ca 0.5 over the yellow wash, invoked {invocation}");
    }

    /// <summary>
    /// issue13520's shape: a soft-masked non-isolated group on a DeviceCMYK
    /// page, with partial alpha inside. The revealed half is sampled, because
    /// the masked-out half carries no group alpha and so nothing to remove.
    ///
    /// <para>Total alpha there is 0.5 (content) x 0.9 (mask) = 0.45, so the
    /// target is a 0.45 cyan sample; without removal excise reads 0.225. The
    /// 0.225 gap is wider than <c>FractionTolerance</c>, so this row cannot
    /// pass both ways — which is why the mask is 0.9 and not 0.5.</para>
    /// </summary>
    [Theory]
    [InlineData(GroupSpace.DeviceCmyk)]
    [InlineData(GroupSpace.Absent)]
    public void SoftMaskedNonIsolatedGroup_WithPartialAlphaContent_MatchesIndependentRenderers(
        GroupSpace space)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(Invocation.SoftMask, space, isolated: false));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, RevealedHalf,
            "the revealed half of a soft-masked non-isolated group whose content is /ca 0.5 "
            + "— issue13520's shape. Without §11.4.4 removal excise reads half of this (#1504)");

        // The masked-out half carries no group alpha, so there is nothing there
        // to remove and nothing there to paint. A removal bug that reached
        // outside the group's own coverage would show up as ink here.
        CyanFraction(excise, new SKPointI(140, 100)).Should().BeLessThan(0.08,
            "the half the mask leaves at /BC black stays at the wash");
    }

    /// <summary>
    /// Removal must NOT touch an isolated group. <c>/I true</c> means the
    /// contents composited onto a transparent initial backdrop, so there is no
    /// backdrop in the result to take out; subtracting one would lighten the
    /// group by the backdrop's own colour. A no-regression row, not a
    /// fails-without-the-change row.
    ///
    /// <para>⚠️ It is NOT established that these rows are green before #1504 —
    /// only that #1504 cannot be what breaks them. Partial alpha inside a group
    /// is new here, and the <c>GroupSpace.Absent</c> row reaches #1395's
    /// <c>isContainedEntry &amp;&amp; alpha &lt; 1</c> RGB branch, which #1395's
    /// own fixtures exercised only with opaque content. Run these rows BEFORE
    /// the non-isolated ones: a red here is #1395 residue and must not be
    /// diagnosed as a removal defect.</para>
    /// </summary>
    [Theory]
    [InlineData(Invocation.Plain, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.ConstantAlpha, GroupSpace.Absent)]
    public void IsolatedGroup_WithPartialAlphaContent_IsUnaffectedByRemoval(
        Invocation invocation, GroupSpace space)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(invocation, space, isolated: true));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, InsideObject,
            $"an ISOLATED group has no initial backdrop to remove, invoked {invocation}");
    }

    /// <summary>
    /// The other direction of the delta, with no oracle: removal changes only
    /// the pixels the group paints. Reading <c>C0</c> from the parent backdrop
    /// while that same backdrop is being written pixel by pixel, or applying
    /// removal outside the group's own region, would show up here as a change
    /// somewhere the group never touched.
    /// </summary>
    [Theory]
    [InlineData(Invocation.Plain, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.SoftMask, GroupSpace.DeviceCmyk)]
    [InlineData(Invocation.BlendMultiply, GroupSpace.Absent)]
    public void NonIsolatedGroup_ChangesNothingOutsideTheObject(Invocation invocation, GroupSpace space)
    {
        using var with = RenderWithExcise(WriteTemp(Fixture(invocation, space, isolated: false)));
        using var without = RenderWithExcise(WriteTemp(
            Fixture(invocation, space, isolated: false, invoked: false)));

        // The object is x 20..180, y 20..180 in PDF space; padded by 2 px.
        var changed = new SKRectI(18, 18, 182, 182);
        var worst = WorstDifferenceOutside(with, without, changed, out var at);
        worst.Should().BeLessThanOrEqualTo(3,
            $"only the object may change; pixel {at} changed by {worst} (#1504)");

        var sample = invocation == Invocation.SoftMask ? RevealedHalf : InsideObject;
        ChannelDistance(with.GetPixel(sample.X, sample.Y), without.GetPixel(sample.X, sample.Y))
            .Should().BeGreaterThan(20,
                "and the object DID change, so \"nothing else changed\" is not vacuous");
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
    }

    private static void AssertFractionAgrees(SKBitmap excise, SKBitmap mutool, SKBitmap gs, SKPointI at, string what)
    {
        var m = CyanFraction(mutool, at);
        var g = CyanFraction(gs, at);
        m.Should().BeApproximately(g, FractionTolerance,
            $"MuPDF and Ghostscript must agree at {at} ({what}) before their answer is the target");

        var target = (m + g) / 2;
        target.Should().BeInRange(0.15, 0.85,
            $"the fixture is built so {what} is partial coverage; oracles read {m:F2}/{g:F2}");
        CyanFraction(excise, at).Should().BeApproximately(target, FractionTolerance,
            $"excise at {at}: {what} (oracles {m:F2}/{g:F2})");
    }

    /// <summary>
    /// How far the red channel at <paramref name="at"/> has moved from this
    /// renderer's own wash towards this renderer's own opaque-cyan reference:
    /// 0 = wash only, 1 = opaque cyan over the wash. Keeps the measurement
    /// independent of each renderer's CMYK-to-RGB preview conversion.
    /// </summary>
    private static double CyanFraction(SKBitmap bitmap, SKPointI at)
    {
        var wash = (int)bitmap.GetPixel(WashSample.X, WashSample.Y).Red;
        var reference = (int)bitmap.GetPixel(ReferenceSample.X, ReferenceSample.Y).Red;
        var span = wash - reference;
        span.Should().BeGreaterThan(100,
            "the reference patch must read as cyan over the yellow wash, or the fixture measures nothing");
        return (wash - bitmap.GetPixel(at.X, at.Y).Red) / (double)span;
    }

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
    /// 400x200 pt DeviceCMYK-group page: yellow wash, opaque cyan reference
    /// patch at x 310..390 / y 110..190, then <c>q {invocation} /Fm0 Do Q</c>.
    /// <c>/Fm0</c> is a <c>/BBox [0 0 200 200]</c> group whose content is
    /// <c>/GSh gs 1 0 0 0 k 20 20 160 160 re f</c> — cyan at <c>/ca 0.5</c>,
    /// which is the whole point: the group alpha is 0.5, so the removal factor
    /// is not zero.
    /// </summary>
    private static byte[] Fixture(
        Invocation invocation,
        GroupSpace space,
        bool isolated,
        bool invoked = true)
    {
        var gs = invocation switch
        {
            Invocation.Plain => "",
            Invocation.SoftMask => "/GSm gs ",
            Invocation.ConstantAlpha => "/GSa gs ",
            Invocation.BlendMultiply => "/GSb gs ",
            _ => throw new ArgumentOutOfRangeException(nameof(invocation)),
        };

        var pageContent = new StringBuilder()
            .Append("0 0 0.4 0 k 0 0 400 200 re f\n")
            .Append("1 0 0 0 k 310 110 80 80 re f\n");
        if (invoked)
            pageContent.Append("q ").Append(gs).Append("/Fm0 Do Q\n");

        // Grey 0.9 over the LEFT half only; the right half stays at /BC
        // (default black), i.e. masked out. 0.9 rather than 0.5 on purpose:
        // the un-removed answer is the correct one scaled by the group alpha,
        // so the closer the OTHER factors are to 1 the wider the gap between
        // right and wrong. At mask 0.5 the gap would be 0.125 — inside
        // FractionTolerance, i.e. an oracle row that passes either way.
        const string maskContent = "0.9 g 0 0 100 200 re f\n";

        // The discriminating variable: PARTIAL alpha inside the group.
        const string groupContent = "/GSh gs 1 0 0 0 k 20 20 160 160 re f\n";

        var groupSpace = space switch
        {
            GroupSpace.DeviceCmyk => " /CS /DeviceCMYK",
            GroupSpace.Absent => "",
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
        var groupIsolated = isolated ? " /I true" : " /I false";
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> /Resources "
                + "<< /ExtGState << /GSm 5 0 R /GSa 8 0 R /GSb 9 0 R >> "
                + "/XObject << /Fm0 7 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceGray /I true >> "
                + $"/Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + $"/Group << /S /Transparency{groupSpace}{groupIsolated} >> "
                + "/Resources << /ExtGState << /GSh 10 0 R >> >> "
                + $"/Length {groupContent.Length} >>\nstream\n{groupContent}\nendstream\nendobj\n",
            // The INVOCATION alpha, 0.9 for the same reason the mask is 0.9:
            // it must be below 1 to reach the child path at all, and close to 1
            // so it does not shrink the right-vs-wrong gap.
            "8 0 obj\n<< /Type /ExtGState /ca 0.9 /CA 0.9 >>\nendobj\n",
            "9 0 obj\n<< /Type /ExtGState /BM /Multiply >>\nendobj\n",
            // The CONTENT alpha: the group alpha agn, and the whole variable
            // this fixture exists for. 0.5 maximises |right - wrong|, which is
            // agn * (1 - agn) times the other factors.
            "10 0 obj\n<< /Type /ExtGState /ca 0.5 /CA 0.5 >>\nendobj\n",
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-1504-{Guid.NewGuid():N}.pdf");
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
