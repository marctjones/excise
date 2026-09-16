using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1395 — a transparency group invoked under a soft mask, a constant alpha or
/// a blend mode on a page whose <c>/Group</c> is DeviceCMYK.
///
/// <para><b>The defect.</b> On a DeviceCMYK page, CMYK paint composites
/// straight into the page bitmap and its retained CMYK backdrop. A Skia layer
/// opened for a group's <c>/SMask</c> therefore held only what did NOT take
/// that path; the mask was applied to that, and the unmasked object had
/// already escaped underneath. Ghent GWG168 rendered every cell of its "Actual
/// test objects" row wrong that way while each mask bitmap was individually
/// correct. <see cref="SoftMaskIndependenceTests"/> could not see it: its
/// fixture has no page group, no masked form group and uses <c>rg</c>.</para>
///
/// <para><b>The fixture</b> changes ONE variable against its control: the
/// page's <c>/Group &lt;&lt; /S /Transparency /CS /DeviceCMYK &gt;&gt;</c>.
/// A 400x200 pt page is washed yellow (<c>0 0 0.4 0 k</c>). A reference patch of
/// opaque cyan is painted directly on the page. Then an <c>/I false</c> form
/// group paints a cyan object (x 20..180) through a luminosity soft mask whose
/// <c>/G</c> paints grey 0.5 over the LEFT half only, leaving the right half at
/// <c>/BC</c> (default black: masked out). Sub-cases vary only what the group
/// contains: a <c>k</c> fill, the same fill under <c>/BM /Multiply</c>, a
/// DeviceCMYK image, a nested <c>/CS /DeviceCMYK</c> group — each one a
/// distinct DeviceCMYK direct-write path the group's contents used to escape
/// through.</para>
///
/// <para><b>The measurement</b> is colour-management agnostic. The CMYK to RGB
/// preview conversion differs between excise, MuPDF and Ghostscript, so no
/// absolute colour is compared. Each renderer is read against ITS OWN wash and
/// reference patch: the cyan fraction of a sample is how far its red channel has
/// moved from the wash towards the reference. Revealed half: ~0.5. Masked-out
/// half and outside the object: ~0.</para>
///
/// <para><b>Both directions of the delta are asserted.</b> The object is masked
/// correctly (against independent renderers), and — separately, with no oracle
/// needed — nothing outside the revealed half changes at all when the group
/// invocation is removed from the page.</para>
/// </summary>
public class DeviceCmykSoftMaskedGroupTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>Oracle-to-oracle and excise-to-oracle agreement on a cyan fraction.</summary>
    private const double FractionTolerance = 0.15;

    /// <summary>A sample that must read as "no cyan here".</summary>
    private const double NoInkTolerance = 0.08;

    // Device pixels at 72 dpi: x = PDF x, y = 200 - PDF y.
    private static readonly SKPointI Revealed = new(60, 100);
    private static readonly SKPointI MaskedOut = new(140, 100);
    private static readonly SKPointI InsideBBoxOutsideObject = new(10, 100);
    private static readonly SKPointI WashSample = new(350, 190);
    private static readonly SKPointI ReferenceSample = new(350, 50);

    private readonly List<string> _temp = new();

    public enum GroupContent
    {
        Fill,
        BlendMultiplyFill,
        Image,
        NestedCmykGroup,
    }

    public enum GroupSpace
    {
        DeviceCmyk,
        Absent,
        DeviceRgb,
    }

    /// <summary>
    /// The masked group, against MuPDF and Ghostscript. The <c>cmykPage: false</c>
    /// rows are the control: same bytes without the page group, taking the
    /// RGB path #1395 does not touch.
    /// </summary>
    [Theory]
    [InlineData(true, GroupContent.Fill, GroupSpace.DeviceCmyk)]
    [InlineData(true, GroupContent.BlendMultiplyFill, GroupSpace.DeviceCmyk)]
    [InlineData(true, GroupContent.Image, GroupSpace.DeviceCmyk)]
    [InlineData(true, GroupContent.NestedCmykGroup, GroupSpace.DeviceCmyk)]
    [InlineData(true, GroupContent.Fill, GroupSpace.Absent)]
    [InlineData(true, GroupContent.Fill, GroupSpace.DeviceRgb)]
    [InlineData(false, GroupContent.Fill, GroupSpace.DeviceCmyk)]
    [InlineData(false, GroupContent.BlendMultiplyFill, GroupSpace.DeviceCmyk)]
    [InlineData(false, GroupContent.Image, GroupSpace.DeviceCmyk)]
    [InlineData(false, GroupContent.NestedCmykGroup, GroupSpace.DeviceCmyk)]
    public void SoftMaskedGroup_IsMasked_AsIndependentRenderersMaskIt(
        bool cmykPage, GroupContent content, GroupSpace space)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(cmykPage, content, space, invocation: "/GSm gs"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, Revealed, "the half the mask reveals at 0.5");
        AssertNoInk(excise, mutool!, gs!, MaskedOut,
            "the half the mask leaves at /BC black, i.e. masked out — the unmasked object "
            + "escaping the layer shows up here as full cyan (#1395)");
        AssertNoInk(excise, mutool!, gs!, InsideBBoxOutsideObject,
            "inside the group's /BBox but outside the object nothing is painted");

        if (content == GroupContent.BlendMultiplyFill)
        {
            // Multiply against the non-isolated group's yellow backdrop keeps the
            // wash's yellow under the cyan. Blue is the yellow channel: an
            // isolated or unblended result would lift it towards cyan's blue.
            var m = mutool!.GetPixel(Revealed.X, Revealed.Y).Blue;
            var g = gs!.GetPixel(Revealed.X, Revealed.Y).Blue;
            ((int)m).Should().BeCloseTo(g, 40,
                "MuPDF and Ghostscript must agree on the blended blue before it is used as the target");
            ((int)excise.GetPixel(Revealed.X, Revealed.Y).Blue).Should().BeCloseTo((m + g) / 2, 40,
                "the /BM /Multiply inside the masked group blends against the group backdrop");
        }
    }

    /// <summary>
    /// A group with <c>/ca 0.5</c> and no mask on the DeviceCMYK page — the
    /// plain-layer entry point #1395 fixes alongside the masked one. The whole
    /// object reads as half cyan; both halves are sampled.
    ///
    /// <para>The three rows take the three routes: <see cref="GroupSpace.DeviceCmyk"/>
    /// the pre-existing unmasked DeviceCMYK group entry, <see cref="GroupSpace.Absent"/>
    /// the #1395 child-context entry, <see cref="GroupSpace.DeviceRgb"/> the
    /// contained Skia layer. Before #1395 the DeviceCMYK entry did not reset the
    /// group's content to alpha 1 (§11.6.6) and applied <c>/ca</c> again on the
    /// composite — in a non-isolated group over a wash, ~0.125 where the spec
    /// says 0.5. The Ghent gwg160–162 patches never invoke a group with
    /// <c>/ca</c> below 1, which is why no contract saw it.</para>
    /// </summary>
    [Theory]
    [InlineData(GroupSpace.DeviceCmyk)]
    [InlineData(GroupSpace.Absent)]
    [InlineData(GroupSpace.DeviceRgb)]
    public void ConstantAlphaGroup_OnCmykPage_IsHalfTransparent_AsIndependentRenderersDrawIt(GroupSpace space)
    {
        RequireOracles();
        var path = WriteTemp(Fixture(cmykPage: true, GroupContent.Fill, space, invocation: "/GSa gs"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, Revealed, "/ca 0.5 over the left half");
        AssertFractionAgrees(excise, mutool!, gs!, MaskedOut, "/ca 0.5 over the right half (no mask here)");
        AssertNoInk(excise, mutool!, gs!, InsideBBoxOutsideObject,
            "inside the group's /BBox but outside the object nothing is painted");
    }

    /// <summary>
    /// §11.6.5.1: the mask is interpreted in the CTM in force at <c>gs</c>, not
    /// at <c>Do</c>. The fixture sets the mask, THEN shifts the CTM 50 pt right
    /// and paints the group. The object moves to x 70..230; the mask stays at
    /// x 0..100 (revealed) / 100..200 (masked out). Sampled at x 140: revealed
    /// under the paint-time CTM, masked out under the gs-time CTM.
    ///
    /// <para>Only the DeviceCMYK group path reads the gs-time CTM in this
    /// change; the Skia layer path on RGB pages still uses paint time, so no
    /// RGB control row is asserted here.</para>
    /// </summary>
    [Fact]
    public void SoftMask_UsesTheCtmAtGsTime_OnCmykPage()
    {
        RequireOracles();
        var path = WriteTemp(Fixture(
            cmykPage: true, GroupContent.Fill, GroupSpace.DeviceCmyk, invocation: "/GSm gs 1 0 0 1 50 0 cm"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        AssertFractionAgrees(excise, mutool!, gs!, new SKPointI(85, 100),
            "object shifted to x 70..230, gs-time mask reveals x < 100");
        AssertNoInk(excise, mutool!, gs!, new SKPointI(140, 100),
            "gs-time mask leaves x 100..200 masked out; a paint-time mask would reveal x 50..150");
    }

    /// <summary>
    /// The other direction of the delta, with no oracle needed: rendering the
    /// page WITH the masked group invocation must differ from rendering it
    /// WITHOUT only inside the half the mask reveals. An escaped direct write,
    /// a composite outside the group region, or a backdrop sync that rewrote
    /// pixels it should not have all show up as a change somewhere else.
    /// </summary>
    [Theory]
    [InlineData(GroupContent.Fill, GroupSpace.DeviceCmyk)]
    [InlineData(GroupContent.BlendMultiplyFill, GroupSpace.DeviceCmyk)]
    [InlineData(GroupContent.Image, GroupSpace.DeviceCmyk)]
    [InlineData(GroupContent.NestedCmykGroup, GroupSpace.DeviceCmyk)]
    [InlineData(GroupContent.Fill, GroupSpace.Absent)]
    [InlineData(GroupContent.Fill, GroupSpace.DeviceRgb)]
    public void SoftMaskedGroup_OnCmykPage_ChangesNothingOutsideTheRevealedHalf(
        GroupContent content, GroupSpace space)
    {
        using var with = RenderWithExcise(WriteTemp(Fixture(true, content, space, invocation: "/GSm gs")));
        using var without = RenderWithExcise(WriteTemp(Fixture(true, content, space, invocation: null)));

        // The revealed half of the object, padded by 2 px for antialiasing.
        var changed = new SKRectI(18, 18, 102, 182);
        var worst = WorstDifferenceOutside(with, without, changed, out var at);
        worst.Should().BeLessThanOrEqualTo(3,
            $"only the revealed half of the object may change; pixel {at} changed by {worst} (#1395)");

        // And the revealed half DID change — otherwise "nothing changed" is vacuous.
        var inside = ChannelDistance(with.GetPixel(Revealed.X, Revealed.Y), without.GetPixel(Revealed.X, Revealed.Y));
        inside.Should().BeGreaterThan(40, "the mask reveals half of a cyan object over yellow");
    }

    /// <summary>The same no-oracle delta for the <c>/ca 0.5</c> group: only the object changes.</summary>
    [Theory]
    [InlineData(GroupSpace.DeviceCmyk)]
    [InlineData(GroupSpace.Absent)]
    [InlineData(GroupSpace.DeviceRgb)]
    public void ConstantAlphaGroup_OnCmykPage_ChangesNothingOutsideTheObject(GroupSpace space)
    {
        using var with = RenderWithExcise(WriteTemp(Fixture(true, GroupContent.Fill, space, invocation: "/GSa gs")));
        using var without = RenderWithExcise(WriteTemp(Fixture(true, GroupContent.Fill, space, invocation: null)));

        var changed = new SKRectI(18, 18, 182, 182);
        var worst = WorstDifferenceOutside(with, without, changed, out var at);
        worst.Should().BeLessThanOrEqualTo(3,
            $"only the object may change; pixel {at} changed by {worst} (#1395)");
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
        target.Should().BeInRange(0.2, 0.8,
            $"the fixture is built so {what} is partial coverage; oracles read {m:F2}/{g:F2}");
        CyanFraction(excise, at).Should().BeApproximately(target, FractionTolerance,
            $"excise at {at}: {what} (oracles {m:F2}/{g:F2})");
    }

    private static void AssertNoInk(SKBitmap excise, SKBitmap mutool, SKBitmap gs, SKPointI at, string what)
    {
        CyanFraction(mutool, at).Should().BeLessThan(NoInkTolerance, $"MuPDF at {at}: {what}");
        CyanFraction(gs, at).Should().BeLessThan(NoInkTolerance, $"Ghostscript at {at}: {what}");
        CyanFraction(excise, at).Should().BeLessThan(NoInkTolerance, $"excise at {at}: {what}");
    }

    /// <summary>
    /// How far the red channel at <paramref name="at"/> has moved from this
    /// renderer's own wash towards this renderer's own opaque-cyan reference:
    /// 0 = wash only, 1 = opaque cyan over the wash.
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
    /// 400x200 pt page: yellow wash, opaque cyan reference patch at x 310..390,
    /// y 110..190, then (unless <paramref name="invocation"/> is null)
    /// <c>q {invocation} /Fm0 Do Q</c>. <c>/GSm</c> sets the luminosity mask;
    /// <c>/GSa</c> sets <c>/ca 0.5</c> with no mask.
    /// </summary>
    private static byte[] Fixture(bool cmykPage, GroupContent content, GroupSpace space, string? invocation)
    {
        var pageContent = new StringBuilder()
            .Append("0 0 0.4 0 k 0 0 400 200 re f\n")
            .Append("1 0 0 0 k 310 110 80 80 re f\n");
        if (invocation != null)
            pageContent.Append("q ").Append(invocation).Append(" /Fm0 Do Q\n");

        const string maskContent = "0.5 g 0 0 100 200 re f\n";

        var groupContent = content switch
        {
            GroupContent.Fill => "1 0 0 0 k 20 20 160 160 re f\n",
            GroupContent.BlendMultiplyFill => "/GB gs 1 0 0 0 k 20 20 160 160 re f\n",
            GroupContent.Image => "q 160 0 0 160 20 20 cm /Im0 Do Q\n",
            GroupContent.NestedCmykGroup => "/Fm1 Do\n",
            _ => throw new ArgumentOutOfRangeException(nameof(content)),
        };

        var groupSpace = space switch
        {
            GroupSpace.DeviceCmyk => " /CS /DeviceCMYK",
            GroupSpace.Absent => "",
            GroupSpace.DeviceRgb => " /CS /DeviceRGB",
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };

        const string nestedContent = "1 0 0 0 k 20 20 160 160 re f\n";
        const string imageData = "FF000000>";
        var pageGroup = cmykPage ? " /Group << /S /Transparency /CS /DeviceCMYK >>" : "";
        var page = pageContent.ToString();

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 200] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R{pageGroup} /Resources "
                + "<< /ExtGState << /GSm 5 0 R /GSa 9 0 R >> /XObject << /Fm0 7 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceGray /I true >> "
                + $"/Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + $"/Group << /S /Transparency{groupSpace} /I false >> "
                + "/Resources << /ExtGState << /GB 8 0 R >> /XObject << /Im0 10 0 R /Fm1 11 0 R >> >> "
                + $"/Length {groupContent.Length} >>\nstream\n{groupContent}\nendstream\nendobj\n",
            "8 0 obj\n<< /Type /ExtGState /BM /Multiply >>\nendobj\n",
            "9 0 obj\n<< /Type /ExtGState /ca 0.5 /CA 0.5 >>\nendobj\n",
            "10 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK "
                + $"/BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {imageData.Length} >>\n"
                + $"stream\n{imageData}\nendstream\nendobj\n",
            "11 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I true >> "
                + $"/Length {nestedContent.Length} >>\nstream\n{nestedContent}\nendstream\nendobj\n",
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-1395-{Guid.NewGuid():N}.pdf");
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
