using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #846 tile-clip: the continuous view builds the render ClipRect in VISUAL
/// (rotated) space, but <see cref="SkiaRenderer"/> applies it in CONTENT
/// (unrotated) space, so a rotated page's visible band clips the wrong content and
/// its top is cut off. These pin the corrected mapping — as pure math AND as an
/// end-to-end render against real pixels.
/// </summary>
public class ContinuousTileClipTests
{
    private static readonly PdfRectangle Letter = new(0, 0, 612, 792);

    [Fact]
    public void Unrotated_VisualTopHalf_MapsToContentTopHalf()
    {
        // 0°: visual and content agree (Y just flips). Visual top half (Y 0..396)
        // is the content TOP half (cy 396..792).
        var clip = ContinuousTileClip.VisualBandToContentClip(0, Letter, 0, 0, 612, 396);
        ((double)clip.Left).Should().BeApproximately(0, 0.5);
        ((double)clip.Right).Should().BeApproximately(612, 0.5);
        ((double)clip.Top).Should().BeApproximately(396, 0.5);   // content Y-min
        ((double)clip.Bottom).Should().BeApproximately(792, 0.5); // content Y-max
    }

    [Fact]
    public void Rotated90_VisualTopHalf_MapsToContentLEFTHalf_AxisSwap()
    {
        // 90°: VisualWidth=792, VisualHeight=612. The visual TOP half (vy 0..306)
        // is the content LEFT half (cx 0..306) — the axis swap the buggy code missed.
        var clip = ContinuousTileClip.VisualBandToContentClip(90, Letter, 0, 0, 792, 306);
        ((double)clip.Left).Should().BeApproximately(0, 0.5);
        ((double)clip.Right).Should().BeApproximately(306, 0.5);   // LEFT half of content
        ((double)clip.Top).Should().BeApproximately(0, 0.5);
        ((double)clip.Bottom).Should().BeApproximately(792, 0.5);
    }

    [Fact]
    public void Rotated270_VisualTopHalf_MapsToContentRIGHTHalf()
    {
        var clip = ContinuousTileClip.VisualBandToContentClip(270, Letter, 0, 0, 792, 306);
        ((double)clip.Left).Should().BeApproximately(306, 0.5);    // RIGHT half of content
        ((double)clip.Right).Should().BeApproximately(612, 0.5);
    }

    [Fact]
    public void Rendered_Rotated90_TopHalfBand_ShowsTheVisualTopContent_NotClippedOrShifted()
    {
        // A page whose CONTENT left half is solid black; rotated 90° clockwise that
        // black block appears at the VISUAL TOP. Rendering the visual top-half band
        // through the corrected clip must come back mostly BLACK. With the naive
        // (visual-as-content) clip the band lands on the white content instead.
        using var doc = PdfDocument.Open(BlackLeftHalfPdf(rotate: 90));
        var page = doc.GetPage(1);
        page.Rotation.Should().Be(90);

        double vw = page.VisualWidth, vh = page.VisualHeight; // 792 x 612
        var contentBox = page.MediaBox.Normalize();

        // Corrected content clip for the visual top-half band.
        var good = ContinuousTileClip.VisualBandToContentClip(90, contentBox, 0, 0, vw, vh / 2);
        var goodTile = new SkiaRenderer().RenderPage(page, new Excise.Rendering.RenderOptions { Dpi = 72, ClipRect = good });
        InkFraction(goodTile).Should().BeGreaterThan(0.8,
            "the visual top-half band of the 90°-rotated page is the black content block; the corrected clip must render it");

        // The BUGGY mapping (treat the visual band as if it were content space) —
        // this is what the continuous view did. It must land on the WRONG content.
        var bad = new SKRect(0, (float)(vh / 2), (float)vw, (float)vh); // visual-as-content, bottom band in visual coords
        var badTile = new SkiaRenderer().RenderPage(page, new Excise.Rendering.RenderOptions { Dpi = 72, ClipRect = bad });
        InkFraction(badTile).Should().BeLessThan(0.6,
            "sanity: the naive visual-as-content clip does NOT render the same top content — proving the mapping matters");
    }

    private static double InkFraction(SKBitmap bmp)
    {
        if (bmp.Width == 0 || bmp.Height == 0) return 0;
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue < 384) ink++;
        }
        return (double)ink / (bmp.Width * bmp.Height);
    }

    /// <summary>A Letter page: content LEFT half (x 0..306) filled solid black,
    /// right half white. /Rotate applied so it displays rotated.</summary>
    private static byte[] BlackLeftHalfPdf(int rotate)
    {
        var content = "0 0 0 rg 0 0 306 792 re f"; // black rectangle over the left half
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate {rotate} " +
            "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
