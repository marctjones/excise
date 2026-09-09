using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1393 — ISO 32000-2 §11.6.5.2: a soft mask's <c>/G</c> group is rendered
/// INDEPENDENTLY of the object it masks. The caller's blend mode and constant
/// alpha belong to the composite of the masked object, not to the production of
/// the mask; <c>/BC</c> gives the group's initial backdrop; <c>/TR</c> maps the
/// computed mask value to the value actually used.
///
/// <para><b>The fixture</b> is built so each entry is separately observable. A
/// red rect is painted through a luminosity mask whose <c>/G</c> group paints
/// grey 0.25 over only the LEFT half of the page. So:</para>
/// <list type="bullet">
/// <item>sampling the LEFT half reads the mask the group painted (0.25, or
/// <c>/TR</c> of it);</item>
/// <item>sampling the RIGHT half reads <c>/BC</c> — the backdrop, default
/// black, i.e. fully masked out.</item>
/// </list>
///
/// <para><b>Measured at 72 dpi (red over white, so the GREEN channel reads the
/// mask directly: 255 = fully masked out, 0 = fully revealed):</b></para>
///
/// <code>
/// case                     sample  excise BEFORE  excise AFTER  gs    mutool  pdftocairo
/// plain                    left    192            192           191   192     191
/// /BC [1] (white backdrop) right   255            0             0     0       0
/// /TR invert               left    192            63            64    62      64
/// caller /BM /Multiply     left    192            192           191   193     191
/// caller /ca 0.5           left    240            224           223   223     223
/// </code>
///
/// <para>Three separate defects show up in that table. <c>/BC</c> was ignored
/// entirely, so a white backdrop masked everything out instead of revealing it
/// — a mask rendered as its own photographic negative. <c>/TR</c> was ignored
/// entirely. And <c>/ca</c> was applied TWICE (0.5 x 0.5 x 0.25 = 0.0625 rather
/// than 0.5 x 0.25 = 0.125) because the path and text callers pass the same
/// <c>SKPaint</c> they draw with, so the object's alpha was inside the layer
/// AND on the layer.</para>
///
/// <para>Unlike #1373 and #1394, pdftocairo is CORRECT on all of these and is
/// included as a third witness.</para>
///
/// <para><b>Not covered:</b> <c>/S /Alpha</c> masks. excise derives every mask
/// from luminosity; no fixture here exercises the alpha subtype and this change
/// does not add it.</para>
/// </summary>
public class SoftMaskIndependenceTests : IDisposable
{
    private const int Dpi = 72;

    /// <summary>Inside the masked rect, left half — the mask the group paints.</summary>
    private const int LeftX = 60;

    /// <summary>Inside the masked rect, right half — the /BC backdrop.</summary>
    private const int RightX = 140;

    private const int SampleY = 100;

    private readonly List<string> _temp = new();

    /// <summary>
    /// <c>/BC</c>, the entry whose absence renders a mask as its own negative.
    /// Default (absent) is luminosity 0 — masked out; <c>/BC [1]</c> is white —
    /// fully revealed.
    /// </summary>
    [Fact]
    public void BackdropColour_DecidesWhatTheGroupDoesNotPaint()
    {
        using var withoutBc = RenderWithExcise(WriteTemp(MaskFixture()));
        using var withBc = RenderWithExcise(WriteTemp(MaskFixture(backdrop: "1")));

        withoutBc.GetPixel(RightX, SampleY).Green.Should().BeGreaterThan(240,
            "with no /BC the backdrop is luminosity 0, so the area the group "
            + "never painted is masked out and the page stays white");
        withBc.GetPixel(RightX, SampleY).Green.Should().BeLessThan(20,
            "/BC [1] is a white backdrop: the same area is fully revealed. "
            + "Ignoring /BC inverts the meaning of every mask that sets it (#1393)");

        // The half the group DID paint must be unaffected by /BC either way —
        // otherwise "BC is honoured" could be satisfied by a global change.
        withoutBc.GetPixel(LeftX, SampleY).Green.Should().BeCloseTo(
            withBc.GetPixel(LeftX, SampleY).Green, 4,
            "/BC only supplies the backdrop the group composites onto");
    }

    /// <summary>
    /// <c>/TR</c>: an inverting transfer turns a 0.25 mask into 0.75, so the
    /// red goes from barely visible to mostly visible.
    /// </summary>
    [Fact]
    public void TransferFunction_IsAppliedToTheMaskValue()
    {
        using var plain = RenderWithExcise(WriteTemp(MaskFixture()));
        using var inverted = RenderWithExcise(WriteTemp(MaskFixture(invertTransfer: true)));

        var plainGreen = plain.GetPixel(LeftX, SampleY).Green;
        var invertedGreen = inverted.GetPixel(LeftX, SampleY).Green;

        plainGreen.Should().BeGreaterThan(170, "a 0.25 mask barely reveals the red");
        invertedGreen.Should().BeLessThan(90,
            "/TR inverts 0.25 to 0.75, so the red is mostly revealed. Ignoring "
            + "/TR leaves this identical to the plain case (#1393)");
    }

    /// <summary>
    /// The caller's <c>/ca</c> must reach the composite exactly once. Two
    /// applications is the signature the issue names, and it is a large,
    /// unambiguous difference: 0.0625 of red where the spec says 0.125.
    /// </summary>
    [Fact]
    public void CallerConstantAlpha_IsAppliedOnce_NotTwice()
    {
        using var opaque = RenderWithExcise(WriteTemp(MaskFixture()));
        using var half = RenderWithExcise(WriteTemp(MaskFixture(callerAlpha: "0.5")));

        // Red over white: green = 255 * (1 - effectiveAlpha).
        var opaqueAlpha = (255 - opaque.GetPixel(LeftX, SampleY).Green) / 255.0;
        var halfAlpha = (255 - half.GetPixel(LeftX, SampleY).Green) / 255.0;

        opaqueAlpha.Should().BeApproximately(0.25, 0.03, "mask 0.25, caller alpha 1");
        halfAlpha.Should().BeApproximately(0.125, 0.03,
            "caller /ca 0.5 times mask 0.25. Applying /ca twice gives 0.0625 — "
            + "the pre-#1393 behaviour, which read as 240 rather than 224");
    }

    /// <summary>
    /// The caller's blend mode must not participate in producing the mask.
    /// <c>/BM /Multiply</c> against the mask group's black backdrop would drive
    /// the luminosity to zero and erase the content the mask should reveal.
    /// </summary>
    [Fact]
    public void CallerBlendMode_DoesNotRasteriseIntoTheMask()
    {
        using var normal = RenderWithExcise(WriteTemp(MaskFixture()));
        using var multiply = RenderWithExcise(WriteTemp(MaskFixture(callerBlendMode: "Multiply")));

        multiply.GetPixel(LeftX, SampleY).Green.Should().BeCloseTo(
            normal.GetPixel(LeftX, SampleY).Green, 6,
            "against a white page /BM /Multiply of an opaque red is the same red; "
            + "the mask itself must be produced identically either way (§11.6.5.2)");
    }

    /// <summary>
    /// The independent witnesses. All three reference engines agree on this
    /// behaviour — including pdftocairo, unlike #1373 and #1394 — so all three
    /// are asserted.
    /// </summary>
    [Theory]
    [InlineData(null, "1", false, LeftX)]      // plain
    [InlineData("1", "1", false, RightX)]      // /BC white
    [InlineData(null, "1", true, LeftX)]       // /TR invert
    [InlineData(null, "0.5", false, LeftX)]    // caller /ca
    public void MaskedFill_AgreesWithIndependentRenderers(
        string? backdrop, string callerAlpha, bool invertTransfer, int sampleX)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = WriteTemp(MaskFixture(backdrop, callerAlpha, invertTransfer));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        var m = mutool!.GetPixel(sampleX, SampleY).Green;
        var g = gs!.GetPixel(sampleX, SampleY).Green;
        ((int)m).Should().BeCloseTo(g, 6,
            "MuPDF and Ghostscript must agree before their answer is used as the target");

        using var excise = RenderWithExcise(path);
        ((int)excise.GetPixel(sampleX, SampleY).Green).Should().BeCloseTo(g, 8,
            "excise matches the independent renderers on the mask value");
    }

    /// <summary>
    /// 200x200 white page. A red rect (20..180) is painted through a
    /// <c>/Luminosity</c> soft mask whose <c>/G</c> group paints grey 0.25 over
    /// the left half only, leaving the right half at <c>/BC</c>.
    /// </summary>
    private static byte[] MaskFixture(
        string? backdrop = null,
        string callerAlpha = "1",
        bool invertTransfer = false)
    {
        const string pageContent = "/GS1 gs 1 0 0 rg 20 20 160 160 re f\n";
        const string maskContent = "0.25 g 0 0 100 200 re f\n";

        var smask = new StringBuilder("<< /S /Luminosity /G 6 0 R");
        if (backdrop != null) smask.Append($" /BC [{backdrop}]");
        if (invertTransfer) smask.Append(" /TR 7 0 R");
        smask.Append(" >>");

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources "
                + "<< /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /ExtGState /BM /Normal "
                + $"/CA {callerAlpha} /ca {callerAlpha} /SMask {smask} >>\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceGray /I true >> "
                + $"/Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n",
            "7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\nendobj\n",
        });
    }

    /// <summary>Overload used by the blend-mode case.</summary>
    private static byte[] MaskFixture(string callerBlendMode)
    {
        const string pageContent = "/GS1 gs 1 0 0 rg 20 20 160 160 re f\n";
        const string maskContent = "0.25 g 0 0 100 200 re f\n";

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources "
                + "<< /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /ExtGState /BM /{callerBlendMode} /CA 1 /ca 1 "
                + "/SMask << /S /Luminosity /G 6 0 R >> >>\nendobj\n",
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /S /Transparency /CS /DeviceGray /I true >> "
                + $"/Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n",
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-1393-{Guid.NewGuid():N}.pdf");
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
