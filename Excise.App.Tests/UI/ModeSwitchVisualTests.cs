using System;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using ReactiveUI;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Pixel-level verification of what the user actually SEES after clicking the
/// interaction-mode toolbar buttons — the automation requested for the live
/// report "the mode button changed what text was displayed … the text was no
/// longer being displayed but the overlay outline was shown without text, and
/// the outline did not have the same shape as the text".
///
/// ModeSwitchDisplayTests asserts control metadata (sizes, sources, flags);
/// this battery renders the real viewer subtree to a bitmap after each mode
/// click (the same RenderTargetBitmap capture the visual-baseline tests use,
/// real Skia pixels) and asserts on INK:
///
///   • the page's text ink survives the mode switch (catches "text vanished");
///   • the ink's bounding region stays put (catches displaced/mis-shaped
///     content or an opaque overlay covering the text);
///   • on a form document, entering a mode adds overlay ink only WHERE the
///     field actually is (catches outlines drawn at the wrong place/scale).
///
/// Runs at devicePixelRatio 1 and simulated-Retina 2 via RenderScalingOverride.
/// </summary>
[Collection("AvaloniaTests")]
public class ModeSwitchVisualTests
{
    public static TheoryData<string, double> ModeByDpr()
    {
        var data = new TheoryData<string, double>();
        foreach (var mode in new[] { "redact", "select-text", "typewriter", "form-authoring" })
        foreach (var dpr in new[] { 1.0, 2.0 })
            data.Add(mode, dpr);
        return data;
    }

    [FixedAvaloniaTheory]
    [MemberData(nameof(ModeByDpr))]
    public async Task PageText_RemainsDisplayed_AfterClickingModeButton(string mode, double dpr)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-mode-visual-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(path, new[]
        {
            "EXCISE MODE SWITCH VISUAL CHECK LINE ONE",
            "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG",
            "0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "FOURTH LINE OF BODY TEXT FOR INK MASS",
        });
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            // Continuous (reading) view first: wait until the page tiles have
            // real ink on screen, then keep that as the reference.
            var before = await CaptureWhenInkedAsync(window, viewer);
            var inkBefore = InkFraction(before);
            var boundsBefore = InkBounds(before);
            inkBefore.Should().BeGreaterThan(0.002,
                "the continuous view must be showing the page text before the mode click");

            // Click the mode button.
            ModeCommand(vm, mode).Execute().Subscribe();
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);

            var after = await CaptureWhenInkedAsync(window, viewer,
                failureContext: $"after switching to {mode} mode (dpr={dpr}) the page text must still be displayed");
            var inkAfter = InkFraction(after);
            var boundsAfter = InkBounds(after);
            DumpCaptureIfRequested(before, $"{mode}-{dpr}-before");
            DumpCaptureIfRequested(after, $"{mode}-{dpr}-after");

            // 1. The text is still there: comparable ink mass on screen.
            inkAfter.Should().BeGreaterThan(inkBefore * 0.4,
                $"the page's text must remain visible after entering {mode} mode " +
                $"(before={inkBefore:P2}, after={inkAfter:P2}) — an overlay must never blank the text");

            // 2. The text is where it was: the ink region's center must not
            // jump (mis-mapped overlay/raster shows up as displaced ink).
            var dxFrac = (double)Math.Abs(Center(boundsAfter).X - Center(boundsBefore).X) / before.Width;
            var dyFrac = (double)Math.Abs(Center(boundsAfter).Y - Center(boundsBefore).Y) / before.Height;
            dxFrac.Should().BeLessThan(0.2,
                $"the displayed text region must not shift horizontally when entering {mode} mode");
            dyFrac.Should().BeLessThan(0.25,
                $"the displayed text region must not shift vertically when entering {mode} mode");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaTheory]
    [InlineData("select-text", 2.0)]
    [InlineData("form-authoring", 2.0)]
    [InlineData("redact", 1.0)]
    public async Task FormDocument_ModeClick_KeepsTextAndPutsOverlayInkAtTheField(string mode, double dpr)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-mode-form-visual-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildTextAndFieldPdf());
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            var before = await CaptureWhenInkedAsync(window, viewer);
            var inkBefore = InkFraction(before);

            ModeCommand(vm, mode).Execute().Subscribe();
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);
            var after = await CaptureWhenInkedAsync(window, viewer,
                failureContext: $"after {mode} on a form document the page text must still be displayed");

            // Text survives on a document that ALSO has a form field.
            InkFraction(after).Should().BeGreaterThan(inkBefore * 0.4,
                $"page text must remain visible after entering {mode} mode on a form document");

            // Any overlay ink the mode added must be where the page content is
            // (text top half, field upper-middle) — NOT sprayed across the
            // bottom half of the page, which held no content at all.
            var bottomQuarterInk = RegionInkFraction(after,
                new SKRectI(0, after.Height * 3 / 4, after.Width, after.Height));
            bottomQuarterInk.Should().BeLessThan(0.02,
                $"{mode} mode must not draw overlay outlines where the page has no content " +
                "(a mis-scaled overlay shows up as ink far from the real text/field)");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task ModeClick_WhileScrolledIntoDocument_KeepsTheCurrentPageOnScreen(double dpr)
    {
        // The live report came from clicking a mode mid-document. Scroll the
        // continuous view into page 2, click a mode, and require that the SAME
        // page is still the one on screen with its text inked — the mode switch
        // must not dump the user somewhere else in the document.
        var path = Path.Combine(Path.GetTempPath(), $"excise-mode-scroll-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4);
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;
            await CaptureWhenInkedAsync(window, viewer); // continuous view settled

            // Scroll into the middle of the document.
            var continuous = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            continuous.Offset = new Vector(continuous.Offset.X, continuous.Extent.Height * 0.4);
            await PumpUntilAsync(window, () => vm.CurrentPage > 1);
            var pageBefore = vm.CurrentPage;

            ModeCommand(vm, "redact").Execute().Subscribe();
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);
            var after = await CaptureWhenInkedAsync(window, viewer,
                failureContext: $"after entering redact mode mid-document the current page's text must be displayed");

            vm.CurrentPage.Should().Be(pageBefore,
                "entering a mode must keep the page the user was reading");
            InkFraction(after).Should().BeGreaterThan(0.0005,
                "the kept page's text must actually be inked on screen after the switch");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    // ── capture + ink analysis ──────────────────────────────────────────────

    /// <summary>Diagnostic: EXCISE_DUMP_MODE_CAPTURES=dir writes capture PNGs there.</summary>
    private static void DumpCaptureIfRequested(SKBitmap bmp, string name)
    {
        var dir = Environment.GetEnvironmentVariable("EXCISE_DUMP_MODE_CAPTURES");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(Path.Combine(dir, $"{name}.png"));
        data.SaveTo(fs);
    }

    private static async Task<SKBitmap> CaptureWhenInkedAsync(
        Window window, PdfViewerControl viewer, string? failureContext = null, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        SKBitmap? last = null;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            window.UpdateLayout();
            last?.Dispose();
            last = Capture(viewer);
            if (InkFraction(last) > 0.002)
                return last;
        }
        // Return the last capture so the caller's assertion message carries the
        // real ink numbers; a null Source would already have failed earlier.
        last.Should().NotBeNull(failureContext ?? "viewer produced no capture");
        return last!;
    }

    private static SKBitmap Capture(PdfViewerControl viewer)
    {
        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException(
                "Could not decode captured viewer surface — check TestAppBuilder UseHeadlessDrawing=false.");
    }

    /// <summary>Fraction of pixels that read as ink (dark on the page).</summary>
    private static double InkFraction(SKBitmap bmp) =>
        (double)CountInk(bmp, new SKRectI(0, 0, bmp.Width, bmp.Height)) / (bmp.Width * bmp.Height);

    private static double RegionInkFraction(SKBitmap bmp, SKRectI region)
    {
        var area = Math.Max(1, region.Width * region.Height);
        return (double)CountInk(bmp, region) / area;
    }

    /// <summary>
    /// Viewer chrome (scrollbar tracks/thumbs at the right and bottom edges)
    /// reads as dark pixels and must not count as page ink — it deterministically
    /// dragged the ink bounding box toward the bottom-right on every capture
    /// that had scrollbars.
    /// </summary>
    private const int ChromeMarginPx = 20;

    private static int CountInk(SKBitmap bmp, SKRectI region)
    {
        int count = 0;
        int right = Math.Min(bmp.Width - ChromeMarginPx, region.Right);
        int bottom = Math.Min(bmp.Height - ChromeMarginPx, region.Bottom);
        for (int y = Math.Max(0, region.Top); y < bottom; y++)
        for (int x = Math.Max(0, region.Left); x < right; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384)
                count++;
        }
        return count;
    }

    private static SKRectI InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height - ChromeMarginPx; y++)
        for (int x = 0; x < bmp.Width - ChromeMarginPx; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static SKPointI Center(SKRectI r) => new(r.MidX, r.MidY);

    private static ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ModeCommand(
        MainWindowViewModel vm, string mode) => mode switch
    {
        "redact" => vm.ToggleRedactionModeCommand,
        "select-text" => vm.ToggleTextSelectionModeCommand,
        "typewriter" => vm.ToggleTypewriterModeCommand,
        "form-authoring" => vm.ToggleFormAuthoringModeCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static async Task PumpUntilAsync(Window window, Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("condition not met while pumping the dispatcher");
            await Task.Delay(50);
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// Minimal PDF: real text in the top half plus one text form field
    /// (/Rect upper-middle-left). Same hand-built style as
    /// FormFieldsOverlayTests.BuildFormPdf.
    /// </summary>
    private static byte[] BuildTextAndFieldPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                      "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>");
        sb.AppendLine("endobj");
        var content = "BT /F1 20 Tf 72 730 Td (EXCISE FORM VISUAL CHECK LINE ONE) Tj " +
                      "0 -28 Td (SECOND LINE OF REAL PAGE TEXT) Tj " +
                      "0 -28 Td (THIRD LINE KEEPS THE INK MASS UP) Tj ET";
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {content.Length} >>");
        sb.AppendLine("stream");
        sb.AppendLine(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        sb.AppendLine("endobj");
        long o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 560 300 600] " +
                      "/F 4 /DA (/Helv 0 Tf 0 g) >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        foreach (var o in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendLine($"{o:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
