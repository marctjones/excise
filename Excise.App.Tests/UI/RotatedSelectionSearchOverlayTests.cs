using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1983: the temporary blue selection highlight and the yellow search highlights on
/// rotated pages, driven through the real MainWindow (UI rotate command, pointer drag,
/// Find, Find Next/Previous, Undo) over the shared #1982 scenario table.
///
/// <para><b>Oracle.</b> Where the text is displayed comes from MuPDF stext of the
/// document as it now stands (saved bytes, so a UI rotation is included): stext reports
/// the displayed, crop-relative, top-left page, the same space the overlay canvases use.
/// Drawn rectangles are read from the visual tree, translated into the rendered page
/// (the single-page Image, or the continuous page Border) and compared as fractions of
/// it. No excise mapper, glyph model or rotation formula decides where the text is.
/// The pointer is aimed at MuPDF's char centres, so a hit-test defect shows up as wrong
/// selected text.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class RotatedSelectionSearchOverlayTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "excise-1983-" + Guid.NewGuid().ToString("N"));

    public RotatedSelectionSearchOverlayTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    public static IEnumerable<object[]> SingleScenarios() =>
        RotationScenarioTable.Ids(s => s.View == RotationView.SinglePage);

    public static IEnumerable<object[]> ContinuousScenarios() =>
        RotationScenarioTable.Ids(s => s.View == RotationView.Continuous);

    // Glyph-metric slack between MuPDF quads and excise glyph boxes, in points.
    private const double SlackPt = 3.0;

    [FixedAvaloniaTheory]
    [MemberData(nameof(SingleScenarios))]
    public async Task SinglePage_SelectionAndSearchHighlights_LandOnMuPdfGlyphRegions(string scenarioId)
    {
        var s = RotationScenarioTable.Get(scenarioId);
        var (vm, window, viewer) = await OpenAsync(s);

        var searchTerm = s.Fixture.IsSynthetic ? RotationProbes.RepeatedWord : s.Fixture.Target!;
        var selectTerm = s.Fixture.IsSynthetic ? RotationProbes.Target : s.Fixture.Target!;

        // issue14497's header is drawn with a 90-degree text matrix; excise extracts it one
        // glyph per line, so Find and copy cannot see the word (#2008).
        bool extractionGap = s.FixtureId == "pdfjs-issue14497";

        // Search BEFORE the UI turns, so the highlights have to follow the rotation.
        if (!extractionGap)
        {
            vm.SearchText = searchTerm;
            await WaitAsync(() => vm.SearchMatches.Count > 0, "search results");
        }

        await RotateAsync(vm, s.UiQuarterTurns);
        if (s.Zoom > 0) vm.ZoomLevel = s.Zoom;

        var oracle = await OracleAsync(vm, s.FinalRotation);
        // The displayed bitmap has MuPDF's aspect and ink where MuPDF draws the target.
        await SinglePageImageAsync(window, viewer, oracle, oracle.Find(selectTerm)[0]);
        Assert.SkipWhen(extractionGap,
            "Rotation and rendering were checked against MuPDF (displayed aspect, ink at 'Parklands'). " +
            "Search and selection are skipped: excise extracts this header one glyph per line, so Find " +
            "and copy cannot match the word (#2008).");
        await AssertSearchHighlightsAsync(window, viewer, oracle, searchTerm, "after the UI turns");

        // Find Next / Previous keep every highlight on its glyphs.
        if (vm.SearchMatches.Count > 1)
        {
            await vm.FindNextCommand!.Execute();
            vm.CurrentSearchMatchIndex.Should().Be(1);
            await AssertSearchHighlightsAsync(window, viewer, oracle, searchTerm, "after Find Next");
            await vm.FindPreviousCommand!.Execute();
            vm.CurrentSearchMatchIndex.Should().Be(0);
            await AssertSearchHighlightsAsync(window, viewer, oracle, searchTerm, "after Find Previous");
        }

        // Drag from MuPDF's first char centre of the target to its last.
        await SinglePageSelectAsync(window, viewer, vm, oracle, selectTerm);

        // One more UI turn: the selection must be cleared or remapped, never left on the
        // old geometry; the search highlights must follow.
        await RotateAsync(vm, 1);
        var turned = await OracleAsync(vm, (s.FinalRotation + 90) % 360);
        await AssertSearchHighlightsAsync(window, viewer, turned, searchTerm, "after a further UI turn");
        AssertSelectionClearedOrOn(viewer.SinglePagePart.TextSelectionLayer!.Children.OfType<Rectangle>().ToList(),
            viewer.SinglePagePart.PdfImage!, turned, selectTerm, "after a further UI turn");

        // Undo restores the earlier geometry, and the highlights return with it.
        await vm.UndoCommand.Execute();
        var undone = await OracleAsync(vm, s.FinalRotation);
        await AssertSearchHighlightsAsync(window, viewer, undone, searchTerm, "after Undo");
        await vm.RedoCommand.Execute();
        var redone = await OracleAsync(vm, (s.FinalRotation + 90) % 360);
        await AssertSearchHighlightsAsync(window, viewer, redone, searchTerm, "after Redo");
    }

    [FixedAvaloniaTheory]
    [MemberData(nameof(ContinuousScenarios))]
    public async Task Continuous_SelectionHighlight_LandsOnMuPdfGlyphRegion(string scenarioId)
    {
        var s = RotationScenarioTable.Get(scenarioId);
        var (vm, window, viewer) = await OpenAsync(s);
        if (vm == null) return;
        var selectTerm = s.Fixture.IsSynthetic ? RotationProbes.Target : s.Fixture.Target!;

        await RotateAsync(vm, s.UiQuarterTurns);
        if (s.Zoom > 0) vm.ZoomLevel = s.Zoom;
        var oracle = await OracleAsync(vm, s.FinalRotation);

        var (slot, container, border) = await ContinuousPageAsync(window, viewer, oracle);
        var chars = oracle.FindChars(selectTerm);
        chars.Should().NotBeEmpty($"MuPDF must find '{selectTerm}'");
        var items = viewer.ContinuousPart.ContinuousItems!;
        Point ToItems(VisualRegion c) => border.TranslatePoint(new Point(
            c.CenterX / oracle.Width * border.Bounds.Width,
            c.CenterY / oracle.Height * border.Bounds.Height), items)!.Value;

        vm.IsTextSelectionMode = true;
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());
        vm.SelectedText = "";
        RaiseSelectionDrag(items, ToItems(chars[0][0]), ToItems(chars[0][^1]));
        await WaitAsync(() => vm.SelectedText.Length > 0, "selected text");
        vm.SelectedText.Should().Be(selectTerm, "dragging across MuPDF's glyphs of the word selects exactly that word");

        window.UpdateLayout();
        // The page container draws only the selection's Rectangles (PdfViewerSelectionTests pins this).
        var drawn = container.GetVisualDescendants().OfType<Rectangle>().ToList();
        drawn.Should().HaveCount(slot.SelectionRects.Count, "every bound highlight is a rendered Rectangle");
        AssertRectsOnRegion(drawn, border, oracle, oracle.Find(selectTerm)[0], "continuous selection");

        // A further UI turn clears or remaps the per-page selection.
        await RotateAsync(vm, 1);
        var turned = await OracleAsync(vm, (s.FinalRotation + 90) % 360);
        var (slot2, container2, border2) = await ContinuousPageAsync(window, viewer, turned);
        var after = container2.GetVisualDescendants().OfType<Rectangle>().ToList();
        _out.WriteLine($"{scenarioId}: selection rects after the further turn: {after.Count} (slot {slot2.SelectionRects.Count})");
        if (after.Count > 0)
            AssertRectsOnRegion(after, border2, turned, turned.Find(selectTerm)[0], "continuous selection after a further UI turn");
    }

    /// <summary>
    /// The minimal form of the #1983 report: at a zoom the reader chose, a UI rotation
    /// must re-render the single page. Overlays are placed for the NEW rotation, so a page
    /// image left at the old rotation puts every highlight off its text.
    /// </summary>
    public enum RerenderCase
    {
        /// <summary>One right turn at a zoom the reader set.</summary>
        ManualZoom,
        /// <summary>Turn, zoom away, zoom back to a level rendered before the turn.</summary>
        ManualZoomAwayAndBack,
        /// <summary>Two right turns at the default fit: same aspect, so only the pixels can tell.</summary>
        FitTwoTurns,
    }

    [FixedAvaloniaTheory]
    [InlineData(RerenderCase.ManualZoom)]
    [InlineData(RerenderCase.ManualZoomAwayAndBack)]
    [InlineData(RerenderCase.FitTwoTurns)]
    public async Task SinglePage_UiRotate_RerendersThePageImage(RerenderCase rerender)
    {
        var s = RotationScenarioTable.Get("s-r0");
        var (vm, window, viewer) = await OpenAsync(s);
        if (rerender != RerenderCase.FitTwoTurns) vm.ZoomLevel = 1.0;
        var upright = await OracleAsync(vm, 0);
        await SinglePageImageAsync(window, viewer, upright, upright.Find(RotationProbes.Target)[0]);

        int turns = rerender == RerenderCase.FitTwoTurns ? 2 : 1;
        await RotateAsync(vm, turns);
        var turned = await OracleAsync(vm, 90 * turns);
        if (rerender == RerenderCase.ManualZoomAwayAndBack)
        {
            vm.ZoomLevel = 1.25;
            await SinglePageImageAsync(window, viewer, turned);
            vm.ZoomLevel = 1.0;
        }
        // Aspect and ink: the displayed bitmap is drawn where MuPDF draws the target.
        await SinglePageImageAsync(window, viewer, turned, turned.Find(RotationProbes.Target)[0]);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task<(MainWindowViewModel Vm, Window Window, PdfViewerControl Viewer)> OpenAsync(RotationScenario s)
    {
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable, "mutool is not installed; it is the glyph-region oracle.");
        Assert.SkipWhen(!PopplerGeometry.IsAvailable, "pdfinfo/pdftotext (Poppler) are not installed.");
        var bytes = RotationFixtures.TryLoad(s.Fixture, out var absence);
        Assert.SkipWhen(bytes == null, absence);

        var path = System.IO.Path.Combine(_tempDir, s.Id + ".pdf");
        await File.WriteAllBytesAsync(path, bytes!);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        if (s.Dpr != 1) viewer.RenderScalingOverride = s.Dpr;
        await vm.LoadDocumentAsync(path);
        vm.ViewMode = s.View == RotationView.SinglePage ? PdfViewMode.SinglePage : PdfViewMode.Continuous;
        _out.WriteLine($"{s.Id}: fixture={s.FixtureId} turns={s.UiQuarterTurns} final={s.FinalRotation} " +
                       $"view={s.View} zoom={s.Zoom} dpr={s.Dpr}");
        return (vm, window, viewer);
    }

    private static async Task RotateAsync(MainWindowViewModel vm, int quarterTurns)
    {
        for (int i = 0; i < quarterTurns; i++)
            await vm.RotatePageRightCommand.Execute();
    }

    /// <summary>MuPDF's displayed page for the document as it stands, rotation checked by Poppler.</summary>
    private async Task<MutoolStextGeometry.StextPage> OracleAsync(MainWindowViewModel vm, int expectedRotation)
    {
        var path = System.IO.Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, vm.PdfCoreDocument!.SaveToBytes());
        PopplerGeometry.Info(path, 1).Rotation.Should().Be(expectedRotation,
            "Poppler reads the rotation the UI applied");
        var page = MutoolStextGeometry.Read(path, 1);
        File.Delete(path);
        return page;
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

    /// <summary>Wait until the single-page Image shows the oracle's aspect (the rotated render landed).</summary>
    private static async Task<Image> SinglePageImageAsync(Window window, PdfViewerControl viewer,
        MutoolStextGeometry.StextPage oracle, VisualRegion? inkAt = null)
    {
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
        var img = viewer.SinglePagePart.PdfImage!;
        double want = oracle.Width / oracle.Height;
        bool Settled() =>
            !viewer.IsLoading && img.Bounds.Height > 1 &&
            Math.Abs(img.Bounds.Width / img.Bounds.Height - want) < 0.01 &&
            (inkAt is not { } r || InkFraction(img, r, oracle) > 0.05);
        try
        {
            await WaitAsync(() => { window.UpdateLayout(); return Settled(); },
                "the rendered page to take MuPDF's displayed aspect and ink", seconds: 10);
        }
        catch (TimeoutException) { /* the assertions below report what is wrong */ }

        (img.Bounds.Width / img.Bounds.Height).Should().BeApproximately(want, 0.01,
            $"the rendered page ({img.Bounds}) must have MuPDF's displayed aspect {oracle.Width}x{oracle.Height}");
        if (inkAt is { } region)
            InkFraction(img, region, oracle).Should().BeGreaterThan(0.05,
                $"the displayed page bitmap must have ink where MuPDF draws the target {region}");
        return img;
    }

    /// <summary>Dark-pixel fraction of the DISPLAYED bitmap inside a region given in displayed points.</summary>
    private static double InkFraction(Image img, VisualRegion region, MutoolStextGeometry.StextPage oracle)
    {
        if (img.Source is not global::Avalonia.Media.Imaging.WriteableBitmap bmp) return 0;
        using var fb = bmp.Lock();
        double sx = fb.Size.Width / oracle.Width, sy = fb.Size.Height / oracle.Height;
        int x0 = Math.Max(0, (int)(region.Left * sx)), x1 = Math.Min(fb.Size.Width, (int)Math.Ceiling(region.Right * sx));
        int y0 = Math.Max(0, (int)(region.Top * sy)), y1 = Math.Min(fb.Size.Height, (int)Math.Ceiling(region.Bottom * sy));
        int ink = 0, total = 0;
        var row = new byte[fb.RowBytes];
        for (int y = y0; y < y1; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (int x = x0; x < x1; x++)
            {
                // 32-bit BGRA/RGBA: the three colour channels sum the same either way.
                total++;
                if (row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2] < 600) ink++;
            }
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private async Task<(Excise.Avalonia.Controls.PdfPageSlot Slot, Control Container, Border Border)> ContinuousPageAsync(
        Window window, PdfViewerControl viewer, MutoolStextGeometry.StextPage oracle)
    {
        var items = viewer.ContinuousPart.ContinuousItems!;
        double want = oracle.Width / oracle.Height;
        Excise.Avalonia.Controls.PdfPageSlot? slot = null;
        Control? container = null;
        Border? border = null;
        await WaitAsync(() =>
        {
            window.UpdateLayout();
            slot = items.ItemsSource?.Cast<Excise.Avalonia.Controls.PdfPageSlot>().FirstOrDefault(p => p.PageNumber == 1);
            container = items.GetRealizedContainers()
                .FirstOrDefault(c => (c.DataContext as Excise.Avalonia.Controls.PdfPageSlot)?.PageNumber == 1);
            border = container?.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            return slot != null && border is { Bounds.Height: > 1 } &&
                   Math.Abs(border.Bounds.Width / border.Bounds.Height - want) < 0.01;
        }, "the continuous page to take MuPDF's displayed aspect");
        _out.WriteLine($"continuous border={border!.Bounds} slot={slot!.DisplayWidth}x{slot.DisplayHeight} oracle={oracle.Width}x{oracle.Height}");
        return (slot!, container!, border!);
    }

    private async Task AssertSearchHighlightsAsync(Window window, PdfViewerControl viewer,
        MutoolStextGeometry.StextPage oracle, string term, string when)
    {
        var expected = oracle.Find(term);
        expected.Should().NotBeEmpty($"MuPDF must find '{term}'");
        var img = await SinglePageImageAsync(window, viewer, oracle);
        var layer = viewer.SinglePagePart.SearchHighlightsLayer!;
        try
        {
            await WaitAsync(() =>
            {
                window.UpdateLayout();
                return layer.Children.OfType<Rectangle>().Count() == expected.Count &&
                       layer.Children.OfType<Rectangle>().All(r => MatchesAny(ToVisual(r, img, oracle), expected));
            }, $"search highlights to settle {when}", seconds: 5);
        }
        catch (TimeoutException) { /* the assertions below report what is wrong */ }

        var rects = layer.Children.OfType<Rectangle>().ToList();
        var drawn = rects.Select(r => ToVisual(r, img, oracle)).ToList();
        _out.WriteLine($"search {when}: drawn {string.Join(" ", drawn)} expected {string.Join(" ", expected)}");
        drawn.Should().HaveCount(expected.Count, $"one search highlight per MuPDF occurrence of '{term}' {when}");
        foreach (var d in drawn)
            MatchesAny(d, expected).Should().BeTrue(
                $"search highlight {d} must cover a MuPDF occurrence of '{term}' {when}; MuPDF: {string.Join(" ", expected)}");
    }

    private static bool MatchesAny(VisualRegion drawn, IReadOnlyList<VisualRegion> expected) =>
        expected.Any(e =>
            Math.Abs(drawn.CenterX - e.CenterX) <= SlackPt &&
            Math.Abs(drawn.CenterY - e.CenterY) <= SlackPt &&
            e.FractionCoveredBy(drawn.Inflate(SlackPt)) > 0.9 &&
            drawn.FractionCoveredBy(e.Inflate(SlackPt)) > 0.6);

    /// <summary>A drawn Rectangle in displayed page points, measured through the real visual tree.</summary>
    private static VisualRegion ToVisual(Rectangle r, Visual page, MutoolStextGeometry.StextPage oracle)
    {
        var tl = r.TranslatePoint(new Point(0, 0), page)
                 ?? throw new InvalidOperationException("highlight is not in the page's visual tree");
        double sx = oracle.Width / page.Bounds.Width, sy = oracle.Height / page.Bounds.Height;
        return new VisualRegion(tl.X * sx, tl.Y * sy, (tl.X + r.Bounds.Width) * sx, (tl.Y + r.Bounds.Height) * sy);
    }

    private async Task SinglePageSelectAsync(Window window, PdfViewerControl viewer, MainWindowViewModel vm,
        MutoolStextGeometry.StextPage oracle, string term)
    {
        var img = await SinglePageImageAsync(window, viewer, oracle);
        var chars = oracle.FindChars(term);
        chars.Should().NotBeEmpty($"MuPDF must find '{term}'");
        vm.IsTextSelectionMode = true;
        window.UpdateLayout();
        var overlay = viewer.SinglePagePart.OverlayCanvas!;
        var origin = overlay.TranslatePoint(new Point(0, 0), img)!.Value;
        origin.X.Should().BeApproximately(0, 1.5, "the overlay and the page image share an origin");
        origin.Y.Should().BeApproximately(0, 1.5, "the overlay and the page image share an origin");
        Point ToOverlay(VisualRegion c) => new(
            c.CenterX / oracle.Width * img.Bounds.Width,
            c.CenterY / oracle.Height * img.Bounds.Height);

        vm.SelectedText = "";
        var start = ToOverlay(chars[0][0]);
        var end = ToOverlay(chars[0][^1]);
        RaiseSelectionDrag(overlay, start, end);
        await WaitAsync(() => vm.SelectedText.Length > 0, "selected text");
        window.UpdateLayout();
        var rects = viewer.SinglePagePart.TextSelectionLayer!.Children.OfType<Rectangle>().ToList();
        _out.WriteLine($"drag {start} -> {end} in image {img.Bounds.Size} (mode {viewer.InteractionMode}); " +
                       $"selected '{vm.SelectedText}'; rects {string.Join(" ", rects.Select(r => ToVisual(r, img, oracle)))}; " +
                       $"MuPDF chars {string.Join(" ", chars[0])}");
        vm.SelectedText.Should().Be(term, "dragging across MuPDF's glyphs of the word selects exactly that word");

        AssertRectsOnRegion(rects, img, oracle, oracle.Find(term)[0], "single-page selection");

        // The selection's page area (what Highlight/Underline will mark up) is the same region.
        var area = vm.CurrentTextSelectionPageArea;
        area.Should().NotBeNull("a finished selection reports its page area");
        var visual = Excise.Core.Document.PdfCoordinateMapper.ToVisualPoints(
            vm.PdfCoreDocument!.GetPage(1), area!.Value);
        var region = new VisualRegion(visual.X, visual.Y, visual.Right, visual.Y2);
        _out.WriteLine($"selection page area {region} vs MuPDF {oracle.Find(term)[0]}");
        MatchesAny(region, new[] { oracle.Find(term)[0] }).Should().BeTrue(
            $"the selection page area {region} must be MuPDF's region of '{term}'");
    }

    private void AssertRectsOnRegion(IReadOnlyList<Rectangle> rects, Visual page,
        MutoolStextGeometry.StextPage oracle, VisualRegion word, string what)
    {
        rects.Should().NotBeEmpty($"{what} draws a highlight");
        var drawn = rects.Select(r => ToVisual(r, page, oracle)).ToList();
        _out.WriteLine($"{what}: drawn {string.Join(" ", drawn)} MuPDF word {word}");
        var inflated = word.Inflate(SlackPt);
        foreach (var d in drawn)
            inflated.Contains(d.CenterX, d.CenterY).Should().BeTrue(
                $"{what} rect {d} must sit on MuPDF's glyphs of the word {word}");
        var union = VisualRegion.Union(drawn);
        word.FractionCoveredBy(union.Inflate(SlackPt)).Should().BeGreaterThan(0.85,
            $"{what} {union} must cover MuPDF's word region {word}");
        union.FractionCoveredBy(inflated).Should().BeGreaterThan(0.85,
            $"{what} {union} must not spill off MuPDF's word region {word}");
    }

    private static void AssertSelectionClearedOrOn(IReadOnlyList<Rectangle> rects, Visual page,
        MutoolStextGeometry.StextPage oracle, string term, string when)
    {
        if (rects.Count == 0) return;
        var word = oracle.Find(term)[0].Inflate(SlackPt);
        foreach (var r in rects)
        {
            var d = ToVisual(r, page, oracle);
            word.Contains(d.CenterX, d.CenterY).Should().BeTrue(
                $"a selection rect kept {when} must be remapped onto the word {word}, found {d}");
        }
    }

    /// <summary>
    /// Press at <paramref name="start"/>, move to <paramref name="end"/>, release; both in
    /// <paramref name="target"/>'s coordinates. Avalonia reads the event position relative
    /// to the TOP LEVEL (GetPosition transforms from the visual root), so the points are
    /// translated there first, as real input arrives. Passing target-local points with the
    /// target as root lands the press elsewhere once the page is offset in the window.
    /// </summary>
    private static void RaiseSelectionDrag(Control target, Point start, Point end)
    {
        var root = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("target is not in a window");
        Point R(Point p) => target.TranslatePoint(p, root) ?? throw new InvalidOperationException("no transform to the window");
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var press = new PointerPressedEventArgs(
            target, pointer, root, R(start), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        press.GetPosition(target).X.Should().BeApproximately(start.X, 0.5, "the harness must press where it aims");
        press.GetPosition(target).Y.Should().BeApproximately(start.Y, 0.5, "the harness must press where it aims");
        target.RaiseEvent(press);
        target.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, target, pointer, root, R(end), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, R(end), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }
}
