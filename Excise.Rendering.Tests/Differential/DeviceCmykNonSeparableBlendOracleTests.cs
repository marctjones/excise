using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1528 — a non-separable blend (Hue, Saturation, Color, Luminosity) on a DeviceCMYK page must
/// follow the last paragraph of ISO 32000-2 §11.3.5: C, M and Y are complemented to RGB, blended and
/// complemented back, and K is NOT blended — it is the backdrop's K for Hue, Saturation and Color and
/// the source's K for Luminosity.
///
/// <para>Excise used to take all four channels through the colour-managed preview conversion and
/// back. That cannot return the backdrop's own ink, so a 30% grey Hue-blended over pure black
/// <c>(0 0 0 1)</c> came back as a lighter, tinted patch instead of black — the "X" the Ghent GWG160
/// and GWG161 pages exist to forbid, in eight cells. This pins the same mechanism on a synthetic
/// page so it holds on a machine that has not downloaded the Ghent suite.</para>
///
/// <para><b>Judges.</b> The spec fixes the answer for Hue, Saturation and Color on a black backdrop
/// (the backdrop's K is 1 and its complemented RGB has no hue or saturation to lose, so the result is
/// black), and mutool and Ghostscript are asserted to agree with it before excise is held to it, so
/// the premise is checked, not assumed. Luminosity has no flat answer here, so excise is held to the
/// two oracles instead. pdftocairo is deliberately not a judge: it fails the Ghent knockout page
/// outright.</para>
/// </summary>
public sealed class DeviceCmykNonSeparableBlendOracleTests
{
    private const int Dpi = 72;
    private const int Probe = 100;
    private const int Outside = 10;

    [Theory]
    [InlineData("Hue")]
    [InlineData("Saturation")]
    [InlineData("Color")]
    public void GreyOverBlack_HueSaturationColor_LeaveTheBackdropUntouched(string mode)
    {
        var pdf = BuildPdf(mode);
        var excise = RenderExcise(pdf);
        var (mutool, ghostscript) = RenderOracles(pdf);

        // Premise first: both independent renderers leave the black patch black.
        InsideVsOutside(mutool).Should().BeLessThanOrEqualTo(6, "mutool is the first oracle for /BM /{0}", mode);
        InsideVsOutside(ghostscript).Should().BeLessThanOrEqualTo(6, "Ghostscript is the second oracle for /BM /{0}", mode);

        InsideVsOutside(excise).Should().BeLessThanOrEqualTo(6,
            "§11.3.5: K is the backdrop's, and a black backdrop has no hue or saturation to blend, so /BM /{0} " +
            "must leave it black (inside {1}, outside {2})", mode, excise.GetPixel(Probe, Probe), excise.GetPixel(Outside, Outside));
    }

    [Fact]
    public void GreyOverBlack_Luminosity_AgreesWithBothOracles()
    {
        var pdf = BuildPdf("Luminosity");
        var excise = RenderExcise(pdf);
        var (mutool, ghostscript) = RenderOracles(pdf);

        var inside = excise.GetPixel(Probe, Probe);
        Delta(inside, mutool.GetPixel(Probe, Probe)).Should().BeLessThanOrEqualTo(16,
            "excise {0} must match mutool {1} inside a Luminosity-blended patch", inside, mutool.GetPixel(Probe, Probe));
        Delta(inside, ghostscript.GetPixel(Probe, Probe)).Should().BeLessThanOrEqualTo(16,
            "excise {0} must match Ghostscript {1} inside a Luminosity-blended patch", inside, ghostscript.GetPixel(Probe, Probe));
    }

    private static int InsideVsOutside(SKBitmap bitmap)
        => Delta(bitmap.GetPixel(Probe, Probe), bitmap.GetPixel(Outside, Outside));

    private static int Delta(SKColor a, SKColor b)
        => Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private static SKBitmap RenderExcise(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static (SKBitmap Mutool, SKBitmap Ghostscript) RenderOracles(byte[] pdf)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = Path.Combine(Path.GetTempPath(), $"excise-nonsep-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            var ghostscript = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            mutool.Should().NotBeNull("mutool must render the synthetic page");
            ghostscript.Should().NotBeNull("Ghostscript must render the synthetic page");
            return (mutool!, ghostscript!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A pure-black DeviceCMYK page with a 30% grey non-isolated group drawn over it under /BM.</summary>
    private static byte[] BuildPdf(string blendMode)
    {
        const string content =
            "0 0 0 1 k 0 0 200 200 re f\n" +
            "q /GB gs /F1 Do Q\n";
        const string form = "0 0 0 0.3 k 50 50 100 100 re f\n";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
            "/Group << /S /Transparency /CS /DeviceCMYK >> " +
            $"/Resources << /ExtGState << /GB << /BM /{blendMode} >> >> /XObject << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] " +
            "/Group << /S /Transparency /CS /DeviceCMYK >> " +
            $"/Length {form.Length} >>\nstream\n{form}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {objects.Length + 1} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
