using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using SkiaSharp;

namespace Excise.App.Automation;

/// <summary>
/// Live-driven visual-stability trace (#846 / #695 Phase 3). Runs INSIDE the real
/// app — where the compositor actually drives the continuous-view tile re-render
/// after a document mutation, unlike the headless test host — performs a
/// page-mutation (rotate/remove/move) and then snapshots the viewer surface every
/// ~<see cref="FrameIntervalMs"/> ms for a short window, recording the ink
/// centroid per frame. A stable view holds the centroid still; the #846 "bounce"
/// shows up as the centroid oscillating across frames. Writes PNG frames plus a
/// trajectory.csv the shell harness analyses.
///
/// Gated entirely behind the EXCISE_VISUAL_TRACE_OUT env var — a no-op in normal
/// use. The action is chosen by EXCISE_VISUAL_TRACE_ACTION (default rotate-right).
/// </summary>
public static class VisualTraceRunner
{
    private const int FrameIntervalMs = 80;
    private const int FramesAfterAction = 30;   // ~2.4s of settle observation
    private const int ChromeMarginPx = 20;

    public static bool IsRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXCISE_VISUAL_TRACE_OUT"));

    /// <summary>Run the trace once the document is loaded, then quit the app.</summary>
    public static async Task RunAsync(Window window, MainWindowViewModel vm)
    {
        var outDir = Environment.GetEnvironmentVariable("EXCISE_VISUAL_TRACE_OUT")!;
        var action = (Environment.GetEnvironmentVariable("EXCISE_VISUAL_TRACE_ACTION") ?? "rotate-right").Trim();
        Directory.CreateDirectory(outDir);
        var csv = new StringBuilder("phase,frame,ms,offsetY,inkFraction,centroidX,centroidY\n");

        try
        {
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            if (viewer == null) { File.WriteAllText(Path.Combine(outDir, "ERROR.txt"), "PdfViewerControl not found"); Shutdown(); return; }

            await WaitInkedAsync(window, viewer);
            var scroll = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer");

            if (scroll != null && Environment.GetEnvironmentVariable("EXCISE_VISUAL_TRACE_SCROLL") == "mid")
            {
                scroll.Offset = new Vector(0, scroll.Extent.Height * 0.4);
                await PumpAsync(window, 8);
            }

            // 1. Baseline (resting) frames.
            for (int i = 0; i < 5; i++)
            {
                Sample(window, viewer, scroll, outDir, csv, "before", i);
                await Task.Delay(FrameIntervalMs);
            }

            // 2. The mutation + its settle.
            InvokeAction(vm, action);
            for (int i = 0; i < FramesAfterAction; i++)
            {
                await Task.Delay(FrameIntervalMs);
                window.UpdateLayout();
                Sample(window, viewer, scroll, outDir, csv, "after", i);
            }

            // 3. Scroll sweep — the user's "bounce" appears on scroll AFTER a mutation.
            //    Step the offset down in equal increments; a stable view moves the
            //    centroid smoothly with the offset, a bouncing one does not.
            if (scroll != null)
            {
                double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                for (int i = 0; i < 12; i++)
                {
                    double target = Math.Min(max, scroll.Offset.Y + 60);
                    scroll.Offset = new Vector(0, target);
                    await Task.Delay(FrameIntervalMs);
                    window.UpdateLayout();
                    Sample(window, viewer, scroll, outDir, csv, "scroll", i);
                }
            }

            // 4. Zoom in + settle.
            (vm.ZoomInCommand as System.Windows.Input.ICommand)?.Execute(null);
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(FrameIntervalMs);
                window.UpdateLayout();
                Sample(window, viewer, scroll, outDir, csv, "zoom", i);
            }

            // 5. Save round-trip — must not crash or disturb the view.
            string savePath = Path.Combine(outDir, "saved.pdf");
            bool saveOk = true;
            try { await vm.SaveDocumentCommand(savePath); }
            catch (Exception ex) { saveOk = false; File.WriteAllText(Path.Combine(outDir, "SAVE_ERROR.txt"), ex.ToString()); }
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(FrameIntervalMs);
                window.UpdateLayout();
                Sample(window, viewer, scroll, outDir, csv, "save", i);
            }

            File.WriteAllText(Path.Combine(outDir, "trajectory.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(outDir, "meta.txt"),
                $"action={action}\nframes_after={FramesAfterAction}\ninterval_ms={FrameIntervalMs}\nsaveOk={saveOk}\nsavedPath={savePath}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "ERROR.txt"), ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private static void InvokeAction(MainWindowViewModel vm, string action)
    {
        // "rotate-all" rotates EVERY page (uniform landscape) — the discriminator
        // for #846: if the post-mutation scroll bounce appears only for a MIXED
        // (one-page-rotated) document and not a uniform one, the defect is the
        // mixed-width horizontal layout, not the vertical scroll-anchor.
        if (action == "rotate-all")
        {
            int pages = vm.TotalPages;
            for (int p = 0; p < pages; p++)
            {
                vm.CurrentPageIndex = p;
                (vm.RotatePageRightCommand as System.Windows.Input.ICommand)?.Execute(null);
            }
            vm.CurrentPageIndex = Math.Min(2, Math.Max(0, pages - 1));
            return;
        }

        // Fire (don't await) — we want to observe the frames WHILE the mutation
        // and its re-render/re-anchor settle, which is where the bounce lives.
        System.Windows.Input.ICommand cmd = action switch
        {
            "rotate-left" => vm.RotatePageLeftCommand,
            "rotate-right" => vm.RotatePageRightCommand,
            "rotate-180" => vm.RotatePage180Command,
            "remove-page" => vm.RemoveCurrentPageCommand,
            "move-later" => vm.MoveCurrentPageLaterCommand,
            "zoom-in" => vm.ZoomInCommand,
            _ => vm.RotatePageRightCommand,
        };
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }

    private static void Sample(Window window, PdfViewerControl viewer, ScrollViewer? scroll, string outDir, StringBuilder csv,
        string phase, int frame)
    {
        int ms = frame * FrameIntervalMs;
        double offsetY = scroll?.Offset.Y ?? -1;
        var (ink, cx, cy, bmp) = CaptureCentroid(viewer);
        try
        {
            var name = $"{phase}_{frame:D2}.png";
            using var fs = File.Create(Path.Combine(outDir, name));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            data.SaveTo(fs);
        }
        catch { /* best effort on frame PNG */ }
        finally { bmp.Dispose(); }

        csv.Append(phase).Append(',').Append(frame).Append(',').Append(ms).Append(',')
           .Append(offsetY.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
           .Append(ink.ToString("F5", CultureInfo.InvariantCulture)).Append(',')
           .Append(cx.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
           .Append(cy.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static (double ink, double cx, double cy, SKBitmap bmp) CaptureCentroid(PdfViewerControl viewer)
    {
        int w = Math.Max(1, (int)viewer.Bounds.Width);
        int h = Math.Max(1, (int)viewer.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms);
        ms.Position = 0;
        var bmp = SKBitmap.Decode(ms) ?? new SKBitmap(w, h);

        long n = 0; double sx = 0, sy = 0;
        int right = bmp.Width - ChromeMarginPx, bottom = bmp.Height - ChromeMarginPx;
        for (int y = 0; y < bottom; y++)
        for (int x = 0; x < right; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384) { n++; sx += x; sy += y; }
        }
        double frac = (double)n / (bmp.Width * bmp.Height);
        return (frac, n > 0 ? sx / n : -1, n > 0 ? sy / n : -1, bmp);
    }

    private static async Task WaitInkedAsync(Window window, PdfViewerControl viewer, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(120);
            window.UpdateLayout();
            var (ink, _, _, bmp) = CaptureCentroid(viewer);
            bmp.Dispose();
            if (ink > 0.002) return;
        }
    }

    private static async Task PumpAsync(Window window, int cycles)
    {
        for (int i = 0; i < cycles; i++) { await Task.Delay(60); window.UpdateLayout(); }
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is
            global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }
}
