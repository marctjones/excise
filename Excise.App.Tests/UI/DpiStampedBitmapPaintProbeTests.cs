using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Probe for the #697 mechanism: an <see cref="Image"/> whose Source is a
/// WriteableBitmap stamped at a DPI other than 96. The bitmap's dip Size is
/// px×96/dpi; Image lays out at that Size. If the paint path confuses dip and
/// pixel units for the source rect, only the top-left (96/dpi)² fraction of
/// the pixels is painted, magnified — which is exactly what the live app
/// showed after Fit at dpr 2 (single-page bitmaps are stamped 96×zoom×dpr).
/// </summary>
[Collection("AvaloniaTests")]
public class DpiStampedBitmapPaintProbeTests
{
    /// <remarks>
    /// This test PINS THE BUGGY BEHAVIOR of the installed Avalonia version —
    /// it is the assumption PdfViewerControl's single-page path works around
    /// by always stamping 96 and sizing the Image explicitly. If this test
    /// ever FAILS, Avalonia fixed dpi-aware Image painting: the workaround
    /// still renders correctly, but the stamp restriction can be lifted.
    /// </remarks>
    [FixedAvaloniaFact]
    public void Image_WithDpiStampedBitmap_MispaintsAsMagnifiedTopLeftCrop()
    {
        // 200×200 px bitmap stamped 192 dpi → dip Size 100×100.
        // Pixels: left half black, right half white.
        var wb = new WriteableBitmap(new PixelSize(200, 200), new Vector(192, 192),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var row = new byte[fb.RowBytes];
            for (int x = 0; x < 200; x++)
            {
                byte v = x < 100 ? (byte)0x00 : (byte)0xFF;
                row[x * 4 + 0] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v;
                row[x * 4 + 3] = 0xFF;
            }
            for (int y = 0; y < 200; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
        }
        wb.Size.Width.Should().BeApproximately(100, 0.5, "192-dpi stamp halves the dip size");

        var img = new Image { Source = wb, Width = 100, Height = 100, Stretch = global::Avalonia.Media.Stretch.Fill };
        var window = new Window { Width = 120, Height = 120, Content = img };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var rt = new RenderTargetBitmap(new PixelSize(100, 100));
            rt.Render(img);
            using var ms = new MemoryStream();
            rt.Save(ms);
            ms.Position = 0;
            using var cap = SKBitmap.Decode(ms)!;

            // Correct dpi-aware painting would show the whole bitmap: left half
            // black, right half white → (75,50) WHITE. The installed Avalonia
            // instead paints only the top-left 100×100 PIXELS magnified — all
            // black → (75,50) BLACK. We pin the buggy behavior (see remarks).
            var right = cap.GetPixel(75, 50);
            var left = cap.GetPixel(25, 50);
            (left.Red + left.Green + left.Blue).Should().BeLessThan(100, "left half is black ink");
            (right.Red + right.Green + right.Blue).Should().BeLessThan(100,
                "the installed Avalonia paints a dpi-stamped bitmap as a magnified top-left pixel " +
                "crop (#697). If this assert FAILS, Avalonia now paints dpi-aware — the 96-stamp " +
                "workaround in PdfViewerControl.RenderCurrentPageAsync/SkiaInterop is then " +
                "optional (still correct) and this probe should be flipped to assert full painting");
        }
        finally
        {
            window.Close();
        }
    }
}
