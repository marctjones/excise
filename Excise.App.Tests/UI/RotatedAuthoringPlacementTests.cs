using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1985: typewriter text, a Square shape and a sticky note placed with real pointer
/// gestures on rotated pages, then Save As, reopen, place a second one, Undo, Redo and
/// Save As again.
///
/// <para><b>Oracles.</b> Where to aim comes from MuPDF stext of the document as it
/// stands (displayed, crop-relative, top-left points): a free region of the page, away
/// from every MuPDF glyph. What landed comes from tools that are not excise:</para>
/// <list type="bullet">
///   <item>Typewriter: MuPDF stext and Poppler <c>pdftotext -bbox</c> of the saved file
///   must find the complete typed word inside the dragged region, reading left to right
///   on screen as it was typed.</item>
///   <item>Square and sticky note: a MuPDF render of the saved file is diffed against a
///   render of the document just before the gesture; the changed pixels must be the
///   dragged rectangle (Square) or start at the clicked point (the note's icon, which is
///   NoRotate/NoZoom, §12.5.3: upright, anchored at its upper-left corner).</item>
/// </list>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class RotatedAuthoringPlacementTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "excise-1985-" + Guid.NewGuid().ToString("N"));
    private const int Dpi = 144;

    public RotatedAuthoringPlacementTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly string[] Scenarios =
        { "s-r0", "s-r90", "s-r180-z150", "s-r270", "s-inherit90", "s-crop90-z75", "s-r0-ui90" };

    public static IEnumerable<object[]> Cases() =>
        from tool in new[] { "Typewriter", "Square", "StickyNote" }
        from s in Scenarios
        select new object[] { s, tool };

    [FixedAvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Placement_LandsWhereThePointerWas_ThroughSaveReopenEditUndoRedo(string scenarioId, string tool)
    {
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable || !MutoolReferenceRenderer.IsAvailable,
            "mutool is not installed; it is the glyph-region and render oracle.");
        Assert.SkipWhen(!PopplerGeometry.IsAvailable, "Poppler (pdfinfo, pdftotext) is not installed.");

        var s = RotationScenarioTable.Get(scenarioId);
        var source = System.IO.Path.Combine(_tempDir, "source.pdf");
        await File.WriteAllBytesAsync(source, RotationFixtures.TryLoad(s.Fixture, out _)!);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        if (s.Dpr != 1) viewer.RenderScalingOverride = s.Dpr;
        await vm.LoadDocumentAsync(source);
        vm.ViewMode = PdfViewMode.SinglePage;
        for (int i = 0; i < s.UiQuarterTurns; i++) await vm.RotatePageRightCommand.Execute();
        if (s.Zoom > 0) vm.ZoomLevel = s.Zoom;
        _out.WriteLine($"{s.Id} {tool}: fixture={s.FixtureId} final={s.FinalRotation}");

        // First placement, Save As, reopen.
        var first = await PlaceAsync(window, viewer, vm, tool, "FIRST1985", avoid: null);
        var firstPath = System.IO.Path.Combine(_tempDir, "first.pdf");
        await vm.SaveFileAsAsync(firstPath);
        PopplerGeometry.Info(firstPath, 1).Rotation.Should().Be(s.FinalRotation);
        AssertLanded(firstPath, tool, first);
        await vm.LoadDocumentAsync(firstPath);
        vm.ViewMode = PdfViewMode.SinglePage;

        // Edit again: a second placement, Undo, Redo, Save As.
        var second = await PlaceAsync(window, viewer, vm, tool, "SECOND1985", avoid: first.Region);
        if (tool != "Typewriter")
        {
            int Count() => vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
                .Count(a => a.Subtype != PdfAnnotationSubtype.Popup);
            var placed = Count();
            await vm.UndoCommand.Execute();
            Count().Should().Be(placed - 1, "Undo removes the second placement");
            await vm.RedoCommand.Execute();
            Count().Should().Be(placed, "Redo restores it");
        }
        var secondPath = System.IO.Path.Combine(_tempDir, "second.pdf");
        await vm.SaveFileAsAsync(secondPath);
        AssertLanded(secondPath, tool, first);
        AssertLanded(secondPath, tool, second);
    }

    /// <summary>What was aimed at: a displayed region, the word typed into it, and the render before.</summary>
    private sealed record Placement(VisualRegion Region, string Word, byte[] Before);

    private async Task<Placement> PlaceAsync(Window window, PdfViewerControl viewer, MainWindowViewModel vm,
        string tool, string word, VisualRegion? avoid)
    {
        var before = vm.PdfCoreDocument!.SaveToBytes();
        var stext = MutoolStextGeometry.Read(before, 1);
        var size = tool switch { "Typewriter" => (W: 150.0, H: 30.0), "Square" => (W: 80.0, H: 50.0), _ => (W: 2.0, H: 2.0) };
        // A note's card (220 x 90 pt from the click) is clickable; keep the second click off it.
        var region = FreeRegion(stext, size.W, size.H, avoid, tool == "StickyNote" ? 230 : 60);
        _out.WriteLine($"{tool} {word}: aiming at {region} on a {stext.Width}x{stext.Height} page");

        switch (tool)
        {
            case "Typewriter": if (!vm.IsTypewriterMode) await vm.ToggleTypewriterModeCommand.Execute(); break;
            case "Square": if (!vm.IsShapeAnnotationMode) await vm.ToggleSquareModeCommand.Execute(); break;
            default: if (!vm.IsStickyNoteToolActive) await vm.ToggleStickyNoteToolCommand.Execute(); break;
        }
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
        var img = viewer.SinglePagePart.PdfImage!;
        await WaitAsync(() =>
        {
            window.UpdateLayout();
            return !viewer.IsLoading && Math.Abs(img.Bounds.Width / img.Bounds.Height - stext.Width / stext.Height) < 0.01;
        }, "the page to render at MuPDF's displayed aspect");
        var overlay = viewer.SinglePagePart.OverlayCanvas!;
        Point W(double x, double y) => overlay.TranslatePoint(
            new Point(x / stext.Width * img.Bounds.Width, y / stext.Height * img.Bounds.Height), window)!.Value;

        int ops = vm.TypewriterTextOperations.Count;
        int annots = vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Count;
        var start = W(region.Left, region.Top);
        var end = W(region.Right, region.Bottom);
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        if (tool != "StickyNote")
        {
            for (int i = 1; i <= 6; i++)
                window.MouseMove(new Point(start.X + (end.X - start.X) * i / 6, start.Y + (end.Y - start.Y) * i / 6));
            window.MouseUp(end, MouseButton.Left);
        }
        else
        {
            window.MouseUp(start, MouseButton.Left);
        }
        window.MouseMove(new Point(2, 2));

        if (tool == "Typewriter")
        {
            await WaitAsync(() => vm.TypewriterTextOperations.Count == ops + 1, "the typewriter box");
            vm.OnTypewriterTextEdited(vm.TypewriterTextOperations[^1].Id, word, 1);
        }
        else
        {
            await WaitAsync(() => vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Count > annots, $"the {tool}");
            if (tool == "StickyNote") vm.CommitOpenStickyNotePopup();
        }
        return new Placement(region, word, before);
    }

    /// <summary>
    /// A displayed region of the given size that no MuPDF glyph touches, and far enough from
    /// <paramref name="avoid"/> (an earlier placement) that the two cannot share a diff window.
    /// </summary>
    private static VisualRegion FreeRegion(MutoolStextGeometry.StextPage stext, double w, double h,
        VisualRegion? avoid, double avoidMargin)
    {
        var glyphs = stext.Lines.SelectMany(l => l).Select(c => c.Box.Inflate(8)).ToList();
        if (avoid is { } a) glyphs.Add(a.Inflate(avoidMargin));
        for (double fy = 0.12; fy < 0.9; fy += 0.08)
            for (double fx = 0.12; fx < 0.9; fx += 0.08)
            {
                var r = new VisualRegion(fx * stext.Width, fy * stext.Height, fx * stext.Width + w, fy * stext.Height + h);
                if (r.Right > stext.Width - 10 || r.Bottom > stext.Height - 10) continue;
                if (glyphs.Any(g => g.FractionCoveredBy(r) > 0 || r.FractionCoveredBy(g) > 0)) continue;
                return r;
            }
        throw new InvalidOperationException("no free region on the page");
    }

    private void AssertLanded(string savedPath, string tool, Placement p)
    {
        QpdfReferenceTool.Check(savedPath)!.Value.Success.Should().BeTrue("qpdf --check accepts the saved file");
        var stext = MutoolStextGeometry.Read(savedPath, 1);
        if (tool == "Typewriter")
        {
            var hits = stext.Find(p.Word);
            hits.Should().NotBeEmpty($"MuPDF must read the complete typed word '{p.Word}' in the saved file");
            var hit = hits[0];
            var dir = stext.DirectionOf(p.Word);
            _out.WriteLine($"typewriter '{p.Word}': MuPDF {hit} dir ({dir.X},{dir.Y}); aimed {p.Region}");
            dir.Should().Be((1.0, 0.0), "typed text reads left to right on screen, as it was typed");
            p.Region.Inflate(3).FractionCoveredBy(hit).Should().BeGreaterThan(0, $"the typed word {hit} overlaps the dragged box {p.Region}");
            hit.FractionCoveredBy(p.Region.Inflate(3)).Should().BeGreaterThan(0.95,
                $"the typed word {hit} lies inside the dragged box {p.Region}");
            var poppler = PopplerGeometry.Words(savedPath, 1, cropBox: true).Words.Where(w => w.Word == p.Word).ToList();
            poppler.Should().NotBeEmpty($"Poppler must read '{p.Word}'");
            poppler[0].Box.FractionCoveredBy(p.Region.Inflate(4)).Should().BeGreaterThan(0.9,
                $"Poppler places '{p.Word}' at {poppler[0].Box}, inside {p.Region}");
            return;
        }

        var beforePath = System.IO.Path.Combine(_tempDir, $"before-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(beforePath, p.Before);
        using var a = MutoolReferenceRenderer.RenderPage(beforePath, 1, Dpi);
        using var b = MutoolReferenceRenderer.RenderPage(savedPath, 1, Dpi);
        File.Delete(beforePath);
        a.Should().NotBeNull(); b.Should().NotBeNull();
        a!.Width.Should().Be(b!.Width); a.Height.Should().Be(b.Height);

        // Changed pixels near the aimed region (another placement elsewhere is excluded).
        double scale = b.Width / stext.Width;
        var window = p.Region.Inflate(tool == "StickyNote" ? 40 : 12);
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, bt = double.MinValue;
        int changed = 0;
        for (int py = Math.Max(0, (int)(window.Top * scale)); py < Math.Min(b.Height, (int)(window.Bottom * scale)); py++)
            for (int px = Math.Max(0, (int)(window.Left * scale)); px < Math.Min(b.Width, (int)(window.Right * scale)); px++)
            {
                var c0 = a.GetPixel(px, py); var c1 = b.GetPixel(px, py);
                if (Math.Abs(c0.Red - c1.Red) + Math.Abs(c0.Green - c1.Green) + Math.Abs(c0.Blue - c1.Blue) < 60) continue;
                changed++;
                double x = (px + 0.5) / scale, y = (py + 0.5) / scale;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); bt = Math.Max(bt, y);
            }
        changed.Should().BeGreaterThan(10, $"MuPDF must draw the {tool} near {p.Region}");
        var drawn = new VisualRegion(l, t, r, bt);
        _out.WriteLine($"{tool}: MuPDF changed {drawn} ({changed} px); aimed {p.Region}");
        if (tool == "Square")
        {
            drawn.Left.Should().BeApproximately(p.Region.Left, 3, $"Square drawn {drawn}, dragged {p.Region}");
            drawn.Top.Should().BeApproximately(p.Region.Top, 3, $"Square drawn {drawn}, dragged {p.Region}");
            drawn.Right.Should().BeApproximately(p.Region.Right, 3, $"Square drawn {drawn}, dragged {p.Region}");
            drawn.Bottom.Should().BeApproximately(p.Region.Bottom, 3, $"Square drawn {drawn}, dragged {p.Region}");
        }
        else
        {
            // NoRotate/NoZoom icon: upright, its upper-left corner at the click.
            drawn.Left.Should().BeApproximately(p.Region.Left, 3, $"note icon drawn {drawn}, clicked {p.Region.Left},{p.Region.Top}");
            drawn.Top.Should().BeApproximately(p.Region.Top, 3, $"note icon drawn {drawn}, clicked {p.Region.Left},{p.Region.Top}");
            drawn.Width.Should().BeLessThan(30, "a note icon is icon-sized");
            drawn.Height.Should().BeLessThan(30, "a note icon is icon-sized");
        }
    }

    private static async Task WaitAsync(Func<bool> ready, string what, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }
}
