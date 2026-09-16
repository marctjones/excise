using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// ISO 32000-1/-2 §11.4.6: in a KNOCKOUT group every element is composited
/// against the group's INITIAL backdrop rather than against the stack of
/// preceding elements, so a later element erases an earlier one wherever it has
/// shape — <b>including where its own alpha is zero</b>. Shape and opacity are
/// separate quantities (§11.4.7, and §11.6.4.4's <c>/AIS</c> only says which of
/// the two an alpha channel means): an element at <c>/ca 0</c> contributes no
/// colour and still knocks out.
///
/// <para><b>The regression this pins (#1514, introduced by #1395).</b> On Ghent
/// <c>GWG161_Transp_Basic_BM_DeviceCMYK_Knockout_X4.pdf</c> the "Opacity (0%)"
/// cell is a <c>/K true</c> group holding a magenta X at <c>/ca 1</c> followed
/// by a green X at <c>/ca 0</c>; correct output shows NO X, and the page says
/// so in its own 4 pt caption — "If an 'X' appears, rendering of Knockout
/// Transparency Groups is not performed correctly." excise drew the green X,
/// reading mean RGB (0, 160.3, <b>133.1</b>) over the cell where mutool reads
/// (0, 142, <b>191</b>), Ghostscript (0, 152, <b>196</b>) and even pdftocairo
/// (8, 151, <b>201</b>) — green ink is <c>1 0 1 0 k</c>, whose blue is zero,
/// so a visible green X is exactly a blue deficit.</para>
///
/// <para>Two separate faults produced it, and fixing either alone moves the
/// failure to the other channel — measured, in this order:</para>
/// <list type="number">
/// <item>the composite forced the invocation alpha to 1 inside a knockout
/// parent, so the <c>/ca 0</c> element painted at full strength (blue 133 where
/// the oracles say 191);</item>
/// <item>with that removed, the zero-alpha element was skipped before it could
/// knock anything out, so the PRECEDING element survived instead (red 122.5
/// where the same test requires under 40).</item>
/// </list>
///
/// <para>Hence the fixture below has both rows: <c>/ca 0</c> must leave the
/// group's initial backdrop, and <c>/ca 1</c> must leave the second element's
/// own colour. A build that gets either fault wrong fails one of them.</para>
///
/// <para><b>Oracles:</b> mutool AND Ghostscript, which agree to the byte here —
/// both render the <c>/ca 0</c> sample identical to the backdrop and the
/// <c>/ca 1</c> sample as the second element's blue.</para>
/// </summary>
public class DeviceCmykKnockoutGroupZeroAlphaTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>Inside the two overlapping elements (PDF 50..150 x 25..75).</summary>
    private static readonly SKPointI InsideElements = new(100, 50);

    /// <summary>The page's red wash, outside the knockout group's elements.</summary>
    private static readonly SKPointI Backdrop = new(20, 50);

    private readonly List<string> _temp = new();

    /// <summary>
    /// The second element at <c>/ca 0</c>: it contributes no colour, but its
    /// shape still knocks the first element out, so the cell falls back to the
    /// group's initial backdrop — the page's red wash.
    ///
    /// <para>Each renderer is read against ITS OWN backdrop pixel, so no
    /// CMYK-to-RGB preview conversion is compared across renderers.</para>
    /// </summary>
    [Fact]
    public void ZeroAlphaElement_InAKnockoutGroup_ErasesThePrecedingElement()
    {
        RequireOracles();
        var path = WriteTemp(Fixture(secondElementAlpha: "0"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        DistanceFromOwnBackdrop(mutool!).Should().BeLessThan(12,
            "mutool must read the /ca 0 cell as the group's initial backdrop, or the target is not established");
        DistanceFromOwnBackdrop(gs!).Should().BeLessThan(12,
            "and Ghostscript must agree independently");

        DistanceFromOwnBackdrop(excise).Should().BeLessThan(12,
            "§11.4.6: a knockout group's later element erases the earlier one wherever it has SHAPE, "
            + "and /ca 0 removes colour, not shape. Forcing the invocation alpha to 1 inside a knockout "
            + "parent paints the second element instead; skipping it on zero alpha leaves the first one "
            + "standing. Both were live and both are wrong");
    }

    /// <summary>
    /// The non-vacuity control. At <c>/ca 1</c> the same second element must
    /// knock out the first AND paint its own colour, so the cell is emphatically
    /// NOT the backdrop. Without this row, "the cell equals the backdrop" would
    /// also pass on a build that simply never draws the group.
    /// </summary>
    [Fact]
    public void FullAlphaElement_InAKnockoutGroup_ReplacesThePrecedingElement()
    {
        RequireOracles();
        var path = WriteTemp(Fixture(secondElementAlpha: "1"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        using var excise = RenderWithExcise(path);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        DistanceFromOwnBackdrop(mutool!).Should().BeGreaterThan(80,
            "at /ca 1 the second element paints, so the cell must be far from the backdrop");
        DistanceFromOwnBackdrop(gs!).Should().BeGreaterThan(80, "and Ghostscript agrees");

        // Read against excise's OWN backdrop, not against the oracles' pixel
        // values: `1 1 0 0 k` is (0,0,255) under excise's DeviceCMYK->RGB
        // preview and (46,48,146) under mutool's and Ghostscript's
        // output-intent conversion, and that 48-level green gap is a colour
        // management difference, not a compositing one. What this row is for is
        // that the SECOND element painted here at all; `KnockedOutElement_
        // NeverSurvives` separately forbids the first element's colour.
        DistanceFromOwnBackdrop(excise).Should().BeGreaterThan(80,
            "at /ca 1 the knockout group's second element must both erase the first and paint, "
            + $"so this cell must be far from the page wash (excise {excise.GetPixel(InsideElements.X, InsideElements.Y)})");
    }

    /// <summary>
    /// The first element's colour must never survive, in EITHER row — this is
    /// GWG161's "if an 'X' appears" indicator as a property, and it needs no
    /// oracle. The first element is <c>1 0 1 0 k</c>, whose RGB is green; the
    /// backdrop is red and the second element blue, so "green dominates" is
    /// unambiguous and no tolerance is involved.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void KnockedOutElement_NeverSurvives(string secondElementAlpha)
    {
        using var excise = RenderWithExcise(WriteTemp(Fixture(secondElementAlpha)));
        var p = excise.GetPixel(InsideElements.X, InsideElements.Y);

        ((int)p.Green).Should().BeLessThan(Math.Max(p.Red, p.Blue),
            $"the knocked-out first element is green (1 0 1 0 k); reading green at {InsideElements} "
            + $"means it was composited under the second element instead of replaced by it (got {p})");
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
    }

    private static int DistanceFromOwnBackdrop(SKBitmap bitmap)
        => ChannelDistance(
            bitmap.GetPixel(InsideElements.X, InsideElements.Y),
            bitmap.GetPixel(Backdrop.X, Backdrop.Y));

    private static int ChannelDistance(SKColor a, SKColor b)
        => Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    /// <summary>
    /// A 200x100 pt DeviceCMYK-group page washed red (<c>0 1 1 0 k</c>), with a
    /// <c>/K true</c> group over it holding two overlapping 100x50 rectangles:
    /// green (<c>1 0 1 0 k</c>) at <c>/ca 1</c>, then blue (<c>1 1 0 0 k</c>) at
    /// <paramref name="secondElementAlpha"/>. Both inner groups are
    /// <c>/CS /DeviceCMYK /I false /K false</c> — GWG161's own shape, and what
    /// routes to the DeviceCMYK child-context path.
    /// </summary>
    private static byte[] Fixture(string secondElementAlpha)
    {
        const string page = "0 1 1 0 k 0 0 200 100 re f\nq /Fm0 Do Q\n";
        const string outer = "q /GS1 gs /FmA Do Q\nq /GS0 gs /FmB Do Q\n";
        const string first = "1 0 1 0 k 50 25 100 50 re f\n";
        const string second = "1 1 0 0 k 50 25 100 50 re f\n";

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 100] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                + "/Group << /S /Transparency /CS /DeviceCMYK >> "
                + "/Resources << /XObject << /Fm0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I false /K true >> "
                + "/Resources << /ExtGState << /GS1 8 0 R /GS0 9 0 R >> "
                + "/XObject << /FmA 6 0 R /FmB 7 0 R >> >> "
                + $"/Length {outer.Length} >>\nstream\n{outer}\nendstream\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [50 25 150 75] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I false /K false >> "
                + $"/Length {first.Length} >>\nstream\n{first}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [50 25 150 75] "
                + "/Group << /S /Transparency /CS /DeviceCMYK /I false /K false >> "
                + $"/Length {second.Length} >>\nstream\n{second}\nendstream\nendobj\n",
            "8 0 obj\n<< /Type /ExtGState /ca 1 /CA 1 >>\nendobj\n",
            $"9 0 obj\n<< /Type /ExtGState /ca {secondElementAlpha} /CA {secondElementAlpha} >>\nendobj\n",
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-knockout-{Guid.NewGuid():N}.pdf");
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
