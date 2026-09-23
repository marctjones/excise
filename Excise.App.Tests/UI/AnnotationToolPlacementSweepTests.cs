using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Graphics;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Every tool on the floating annotation palette, driven the way a user drives
/// it: a real click on the palette button, then the tool's real gesture on the
/// page (drag, freehand stroke, vertex clicks, text selection, or a click), and
/// a check that the annotation landed where the gesture was made (#1796).
/// </summary>
/// <remarks>
/// <para>The oracle never uses the app's coordinate math. Page geometry comes
/// from INK in the composited frame: a 70 pt black box at a known PDF position
/// gives both the screen origin and the pt-per-DIP scale, so screen -> PDF is
/// derived from what the user sees. A defect the app holds consistently in its
/// mapper (the #992 failure shape) still shows up here.</para>
/// <para>Two checks per tool: the saved annotation geometry (input side) and
/// the pixels that changed on screen after placement (output side).</para>
/// <para>The reader state is the stressing one: continuous view, scrolled to
/// page 2, render scaling 2 (Retina), zoom 1.5 — every scale factor a mapping
/// can drop is away from 1.</para>
/// </remarks>
[Collection("AvaloniaTests")]
public class AnnotationToolPlacementSweepTests
{
    private const int TargetPage = 2;
    private const double BoxLeftPt = 110, BoxBottomPt = 560, BoxSizePt = 70;
    private const double WordLeftPt = 300, WordBaselinePt = 500, WordFontPt = 24;
    private const string Word = "MARKTHIS";
    private const double PadDips = 10;
    private const double TolPt = 2.5;

    public static TheoryData<string, double> Tools() => new()
    {
        { "PaletteHighlightButton", 1.5 },
        { "PaletteUnderlineButton", 1.5 },
        { "PaletteStrikeOutButton", 1.5 },
        { "PaletteSquigglyButton", 1.5 },
        { "PaletteSquareButton", 1.5 },
        { "PaletteCircleButton", 1.5 },
        { "PaletteFreeTextButton", 1.5 },
        { "PaletteStampButton", 1.5 },
        { "PaletteImageStampButton", 1.5 },
        { "PaletteInkButton", 1.5 },
        { "PaletteLineButton", 1.5 },
        { "PaletteArrowButton", 1.5 },
        { "PalettePolygonButton", 1.5 },
        { "PalettePolyLineButton", 1.5 },
        { "PaletteStickyNoteButton", 1.5 },
        // Fit zoom (auto, near 1) — the app's default, for the text path.
        { "PaletteHighlightButton", 0 },
    };

    [FixedAvaloniaTheory(Timeout = 120000)]
    [MemberData(nameof(Tools))]
    public async Task PaletteTool_RealGesture_LandsWhereTheUserPointed(string buttonName, double zoom)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-tool-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "in.pdf");
        var output = Path.Combine(dir, "out.pdf");
        var png = Path.Combine(dir, "stamp.png");
        CreateFixture(source);
        CreatePng(png);

        var vm = MainWindowViewModelTestFactory.Create(
            thumbnailPrewarmEnabled: false, dialogService: new AnsweringDialogService());
        vm.SetImageStampPathProviderForTests(() => Task.FromResult<string?>(png));
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1100 };
        window.Show();
        try
        {
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = 2.0;
            await vm.LoadDocumentAsync(source);
            await Settle(window, viewer);
            vm.CurrentPageIndex = TargetPage - 1;
            if (zoom > 0) vm.ZoomLevel = zoom;
            await Settle(window, viewer);

            // ---- Arm the tool with a real click on the palette. ----
            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
            var palette = window.AnnotationPalette!;
            var button = palette.FindControl<Button>(buttonName)!;
            button.Should().NotBeNull($"the palette must have {buttonName}");
            var bc = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), palette)!.Value;
            palette.MouseDown(bc, MouseButton.Left);
            palette.MouseUp(bc, MouseButton.Left);
            await Settle(window, viewer);

            vm.PdfCoreDocument!.GetPage(TargetPage).GetAnnotations().Should().BeEmpty(
                $"clicking {buttonName} arms a tool; nothing is placed until the user points at the page");

            // ---- Where the page is, measured from ink the user can see. ----
            using var before = AnnotationPlacementAccuracyTests.Capture(window, viewer);
            var (box, word) = FindBoxAndWord(before);
            var viewerOrigin = viewer.TranslatePoint(new Point(0, 0), window)!.Value;
            double k = BoxSizePt / box.Width; // pt per DIP, from the box's own ink
            var map = new ScreenMap(box, k);
            string state = $"{buttonName} zoom={viewer.ZoomLevel:F3} view={viewer.ViewMode} page={viewer.CurrentPage} pt/DIP={k:F3}";
            (box.Height * k).Should().BeApproximately(BoxSizePt, 3,
                $"the box ink must be square on screen ({state})");

            Point W(Point viewerLocal) => new(viewerLocal.X + viewerOrigin.X, viewerLocal.Y + viewerOrigin.Y);
            // A sticky note is a /Text plus its /Popup (§12.5.6.14); count what the user placed.
            static List<PdfAnnotation> Placed(PdfPage p) =>
                p.GetAnnotations().Where(a => a.Subtype != PdfAnnotationSubtype.Popup).ToList();
            var beforeAnnots = Placed(vm.PdfCoreDocument!.GetPage(TargetPage)).Count;

            // ---- The gesture, and what it should produce. ----
            var dragRect = box.Inflate(PadDips);
            Rect expectedScreen;
            Action<PdfAnnotation> assertGeometry;
            switch (buttonName)
            {
                case "PaletteHighlightButton" or "PaletteUnderlineButton"
                    or "PaletteStrikeOutButton" or "PaletteSquigglyButton":
                {
                    var y = word.Center.Y;
                    await Drag(window, W(new Point(word.Left + 1, y)), W(new Point(word.Right - 1, y)));
                    var wordPt = map.ToPdf(word);
                    // Highlight ends are rounded past the glyph ink by up to
                    // about half the line height; the overlay does the same.
                    expectedScreen = word.Inflate(new Thickness(word.Height * 0.6, 8));
                    assertGeometry = a =>
                    {
                        var r = a.Rect.Normalize();
                        var ctx = $"{state}: selected '{Word}' whose ink is at {Fmt(wordPt)} pt, annotation /Rect {Fmt(r)}";
                        ((r.Left + r.Right) / 2).Should().BeApproximately((wordPt.Left + wordPt.Right) / 2, TolPt, ctx);
                        ((r.Bottom + r.Top) / 2).Should().BeApproximately((wordPt.Bottom + wordPt.Top) / 2, 6, ctx);
                        r.Width.Should().BeApproximately(wordPt.Width, 6, ctx);
                        r.Height.Should().BeLessThan(WordFontPt * 1.6, ctx);
                    };
                    break;
                }
                case "PaletteSquareButton" or "PaletteCircleButton" or "PaletteFreeTextButton"
                    or "PaletteStampButton" or "PaletteImageStampButton":
                {
                    await Drag(window, W(dragRect.TopLeft), W(dragRect.BottomRight));
                    var exp = map.ToPdf(dragRect);
                    expectedScreen = dragRect;
                    var edges = buttonName != "PaletteStampButton";
                    assertGeometry = a =>
                    {
                        var r = a.Rect.Normalize();
                        var ctx = $"{state}: dragged {Fmt(exp)} pt, annotation /Rect {Fmt(r)}";
                        ((r.Left + r.Right) / 2).Should().BeApproximately((exp.Left + exp.Right) / 2, TolPt, ctx);
                        ((r.Bottom + r.Top) / 2).Should().BeApproximately((exp.Bottom + exp.Top) / 2, TolPt, ctx);
                        if (edges)
                        {
                            r.Width.Should().BeApproximately(exp.Width, 2 * TolPt, ctx);
                            r.Height.Should().BeApproximately(exp.Height, 2 * TolPt, ctx);
                        }
                    };
                    break;
                }
                case "PaletteInkButton":
                {
                    var a0 = dragRect.TopLeft;
                    var a1 = new Point(dragRect.Right, dragRect.Center.Y);
                    var a2 = dragRect.BottomLeft;
                    window.MouseMove(W(a0));
                    window.MouseDown(W(a0), MouseButton.Left);
                    foreach (var p in Lerp(a0, a1, 8).Concat(Lerp(a1, a2, 8))) window.MouseMove(W(p));
                    window.MouseUp(W(a2), MouseButton.Left);
                    ParkPointer(window);
                    expectedScreen = dragRect;
                    assertGeometry = a =>
                    {
                        var pts = a.InkStrokes!.SelectMany(s => s).ToList();
                        var ctx = $"{state}: stroke from {Fmt(map.ToPdf(a0))} via {Fmt(map.ToPdf(a1))} to {Fmt(map.ToPdf(a2))}, " +
                                  $"saved first {Fmt(pts[0])} last {Fmt(pts[^1])}";
                        Near(pts[0], map.ToPdf(a0)).Should().BeTrue(ctx);
                        Near(pts[^1], map.ToPdf(a2)).Should().BeTrue(ctx);
                        pts.Should().Contain(p => Near(p, map.ToPdf(a1)), ctx);
                    };
                    break;
                }
                case "PaletteLineButton" or "PaletteArrowButton":
                {
                    await Drag(window, W(dragRect.TopLeft), W(dragRect.BottomRight));
                    // An arrow's /Rect is padded by the 6 pt head on every side
                    // (AddLineOrArrowAnnotation), and the viewer frames the /Rect.
                    expectedScreen = buttonName == "PaletteArrowButton" ? dragRect.Inflate(6 / k) : dragRect;
                    assertGeometry = a =>
                    {
                        var l = a.LineEndpoints!.Value;
                        var ctx = $"{state}: dragged {Fmt(map.ToPdf(dragRect.TopLeft))} -> {Fmt(map.ToPdf(dragRect.BottomRight))}, " +
                                  $"saved /L {Fmt((l.X1, l.Y1))} -> {Fmt((l.X2, l.Y2))}";
                        Near((l.X1, l.Y1), map.ToPdf(dragRect.TopLeft)).Should().BeTrue(ctx);
                        Near((l.X2, l.Y2), map.ToPdf(dragRect.BottomRight)).Should().BeTrue(ctx);
                    };
                    break;
                }
                case "PalettePolygonButton" or "PalettePolyLineButton":
                {
                    var v = new[] { dragRect.TopLeft, dragRect.TopRight, dragRect.BottomRight };
                    foreach (var p in v)
                    {
                        window.MouseMove(W(p));
                        window.MouseDown(W(p), MouseButton.Left);
                        window.MouseUp(W(p), MouseButton.Left);
                        await Task.Delay(20);
                    }
                    // A double-click finishes the path.
                    window.MouseDown(W(v[^1]), MouseButton.Left);
                    window.MouseUp(W(v[^1]), MouseButton.Left);
                    ParkPointer(window);
                    expectedScreen = dragRect;
                    assertGeometry = a =>
                    {
                        var saved = a.Vertices!;
                        var ctx = $"{state}: clicked {string.Join(", ", v.Select(p => Fmt(map.ToPdf(p))))}, " +
                                  $"saved /Vertices {string.Join(", ", saved.Select(Fmt))}";
                        foreach (var p in v)
                            saved.Should().Contain(s => Near(s, map.ToPdf(p)), ctx);
                    };
                    break;
                }
                case "PaletteStickyNoteButton":
                {
                    var click = box.Center;
                    window.MouseMove(W(click));
                    window.MouseDown(W(click), MouseButton.Left);
                    window.MouseUp(W(click), MouseButton.Left);
                    ParkPointer(window);
                    expectedScreen = new Rect(click, new Size(1, 1));
                    assertGeometry = a =>
                    {
                        var r = a.Rect.Normalize();
                        var c = map.ToPdf(click);
                        var ctx = $"{state}: clicked the page at {Fmt(c)} pt, note /Rect {Fmt(r)}";
                        (Math.Abs(r.Left - c.X) <= TolPt * 2 || (r.Left <= c.X && c.X <= r.Right)).Should().BeTrue(ctx);
                        (Math.Abs(r.Top - c.Y) <= TolPt * 2 || (r.Bottom <= c.Y && c.Y <= r.Top)).Should().BeTrue(ctx);
                    };
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(buttonName), buttonName, null);
            }

            await Settle(window, viewer);

            // ---- Input side: the saved geometry. ----
            var added = Placed(vm.PdfCoreDocument!.GetPage(TargetPage));
            added.Count.Should().Be(beforeAnnots + 1,
                $"{state}: the gesture must add exactly one annotation to page {TargetPage}; " +
                $"page annotation counts are {string.Join(", ", Enumerable.Range(1, 3).Select(p => $"p{p}={Placed(vm.PdfCoreDocument!.GetPage(p)).Count}"))}");
            await vm.SaveFileAsAsync(output);
            using (var reopened = PdfDocument.Open(File.ReadAllBytes(output)))
                assertGeometry(Placed(reopened.GetPage(TargetPage)).Last());

            // ---- Output side: what changed on screen is where the user pointed. ----
            using var after = AnnotationPlacementAccuracyTests.Capture(window, viewer);
            var changed = AnnotationPlacementAccuracyTests.ChangedBounds(before, after);
            var sctx = $"{state}: gesture at {Fmt(expectedScreen)} viewer DIPs, pixels changed at {Fmt(changed)}";
            changed.Width.Should().BeGreaterThan(0, sctx);
            if (buttonName == "PaletteStickyNoteButton")
                changed.Inflate(6).Contains(expectedScreen.TopLeft).Should().BeTrue(sctx);
            else
                expectedScreen.Inflate(8).Contains(changed).Should().BeTrue(sctx);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Screen (viewer DIPs) -> PDF points, anchored on the box's ink.</summary>
    private sealed record ScreenMap(Rect Box, double PtPerDip)
    {
        public (double X, double Y) ToPdf(Point p) =>
            (BoxLeftPt + (p.X - Box.Left) * PtPerDip,
             BoxBottomPt + BoxSizePt - (p.Y - Box.Top) * PtPerDip);

        public PdfRectangle ToPdf(Rect r)
        {
            var (l, t) = ToPdf(r.TopLeft);
            var (rr, b) = ToPdf(r.BottomRight);
            return new PdfRectangle(l, b, rr, t);
        }
    }

    private static bool Near((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) <= TolPt && Math.Abs(a.Y - b.Y) <= TolPt;

    private static IEnumerable<Point> Lerp(Point a, Point b, int n) =>
        Enumerable.Range(1, n).Select(i => new Point(a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n));

    private static async Task Drag(Window window, Point start, Point end)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        await Task.Delay(20);
        foreach (var p in Lerp(start, end, 4)) window.MouseMove(p);
        await Task.Delay(20);
        window.MouseUp(end, MouseButton.Left);
        ParkPointer(window);
    }

    // Off the page, so hover chrome is not in the before/after diff.
    private static void ParkPointer(Window window) => window.MouseMove(new Point(2, 2));

    private static async Task Settle(Window window, PdfViewerControl viewer)
    {
        await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
        if (viewer.ViewMode == PdfViewMode.SinglePage)
        {
            await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
            await AnnotationPlacementAccuracyTests.WaitForFinalSinglePageRender(window, viewer);
        }
        await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
    }

    /// <summary>
    /// Dark-ink clusters in the capture, split by blank row bands: the largest
    /// is the box, the next the word (the fixture puts the word below the box).
    /// </summary>
    private static (Rect Box, Rect Word) FindBoxAndWord(SKBitmap bmp)
    {
        var rows = new List<(int Y, int MinX, int MaxX, int Count)>();
        for (var y = 0; y < bmp.Height; y++)
        {
            int minX = int.MaxValue, maxX = -1, n = 0;
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 60 || c.Green > 60 || c.Blue > 60) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); n++;
            }
            if (n > 0) rows.Add((y, minX, maxX, n));
        }
        var clusters = new List<List<(int Y, int MinX, int MaxX, int Count)>>();
        foreach (var r in rows)
        {
            if (clusters.Count == 0 || r.Y - clusters[^1][^1].Y > 3) clusters.Add([]);
            clusters[^1].Add(r);
        }
        var ranked = clusters.OrderByDescending(c => c.Sum(r => r.Count)).ToList();
        ranked.Count.Should().BeGreaterThanOrEqualTo(2,
            "the viewer must show page 2's box and word (found " + ranked.Count + " ink clusters)");
        static Rect Bounds(List<(int Y, int MinX, int MaxX, int Count)> c) =>
            new(c.Min(r => r.MinX), c[0].Y, c.Max(r => r.MaxX) - c.Min(r => r.MinX) + 1, c[^1].Y - c[0].Y + 1);
        var box = Bounds(ranked[0]);
        var word = ranked.Skip(1).Select(Bounds).First(b => b.Top > box.Bottom);
        return (box, word);
    }

    private static void CreateFixture(string path)
    {
        using var doc = PdfDocument.CreateNew();
        for (var i = 1; i <= 3; i++)
        {
            var page = doc.Pages.AddBlank();
            if (i != TargetPage) continue;
            using var g = page.GetGraphics();
            g.DrawRectangle(BoxLeftPt, BoxBottomPt, BoxSizePt, BoxSizePt, PdfBrush.Black);
            g.DrawString(Word, PdfFont.Helvetica(WordFontPt), PdfBrush.Black, WordLeftPt, WordBaselinePt);
            g.Flush();
        }
        doc.Save(path);
    }

    private static void CreatePng(string path)
    {
        using var bmp = new SKBitmap(16, 16);
        bmp.Erase(new SKColor(30, 120, 220));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Answers every text prompt, so FreeText gets its text.</summary>
    private sealed class AnsweringDialogService : Excise.App.Services.IUserDialogService
    {
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
            Task.FromResult<string?>(string.IsNullOrEmpty(defaultValue) ? "Note" : defaultValue);
        public Task<string?> PromptPasswordAsync(string title, string message) => Task.FromResult<string?>(null);
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    }

    private static string Fmt(PdfRectangle r) => $"[{r.Left:F1},{r.Bottom:F1} → {r.Right:F1},{r.Top:F1}]";
    private static string Fmt(Rect r) => $"[{r.Left:F0},{r.Top:F0} → {r.Right:F0},{r.Bottom:F0}]";
    private static string Fmt((double X, double Y) p) => $"({p.X:F1},{p.Y:F1})";
}
