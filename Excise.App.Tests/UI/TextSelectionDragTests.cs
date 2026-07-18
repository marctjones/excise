using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
using Excise.Avalonia.Services;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// User report: "the string select text with a mouse seems to select
/// the wrong text". The likely culprit is a coord-space mismatch between
/// the pointer event (post-zoom DIPs) and the letter hit-test (which
/// expects pre-zoom DIPs). Recent fix moved the conversion to the
/// OverlayCanvas (inside the LayoutTransformControl wrapper), which
/// should make pointer coords pre-zoom. These tests pin that down by
/// picking a known letter run on a known page and verifying that a
/// simulated drag selects the same text the user sees.
/// </summary>
[Collection("AvaloniaTests")]
public class TextSelectionDragTests
{
    private readonly ITestOutputHelper _out;
    public TextSelectionDragTests(ITestOutputHelper o) { _out = o; }

    private const string PragmaticBook =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";
    private const double RenderDpi = 120.0;

    [FixedAvaloniaFact]
    public async Task DragOverFirstLine_SelectsExpectedReadingOrderText()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);

        // Same race-avoidance as InPageLinkClickTests: the background
        // text-index build and PdfPage.Letters share the document's
        // single parser, so we wait for indexing to finish.
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        // Pick a page with plenty of body text — page 15 in this book
        // is well into prose, no figures, predictable reading order.
        const int targetPageNumber = 15;
        targetPageNumber.Should().BeLessThanOrEqualTo(vm.TotalPages);

        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        letters.Should().NotBeEmpty($"page {targetPageNumber} must have extractable letters");

        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        ordered.Count.Should().BeGreaterThan(20,
            "need a non-trivial run of letters to validate selection");

        // Anchor on the first letter and focus 10 letters later — small
        // enough run to stay on one line, large enough that a single-letter
        // off-by-one in the hit-test would be visible.
        var anchor = ordered[0];
        var focus = ordered[10];
        var expectedRange = TextSelectionEngine.RangeBetween(ordered, anchor, focus);
        var expectedText = TextSelectionEngine.JoinText(expectedRange);
        _out.WriteLine($"Expected selection ({expectedRange.Count} letters): \"{expectedText}\"");

        // Both letters must be on the same line for the simulated drag to
        // hit them — if focus wraps to a second line the X/Y mid-points
        // would be in the page margin between them.
        Math.Abs(GlyphCenterY(anchor) - GlyphCenterY(focus))
            .Should().BeLessThan(2.0, "anchor and focus must share a line");

        // Navigate to the page and switch to text-selection mode.
        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();
        viewer!.InteractionMode.Should().Be(InteractionMode.TextSelection,
            "the viewer's mode binding must be wired to vm.IsTextSelectionMode");

        // Convert the anchor/focus glyph centres from PDF points into
        // bitmap-native DIPs, then translate to window coords. The
        // OverlayCanvas is the control whose local space is bitmap-native
        // (it lives inside the ScaleTransform wrapper) — same control the
        // production code asks PointerEventArgs.GetPosition() against.
        var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
        var anchorWindow = ToWindowPoint(anchor, page, overlay, window);
        var focusWindow = ToWindowPoint(focus, page, overlay, window);
        _out.WriteLine($"anchor='{anchor.Value}' window={anchorWindow}, focus='{focus.Value}' window={focusWindow}");

        // Subscribe to TextSelected at the viewer to capture exactly what
        // the control reports — bypasses any VM-side post-processing that
        // could mask whether the in-control hit-test is correct.
        string? viewerReportedText = null;
        viewer.TextSelected += (_, e) => viewerReportedText = e.Text;

        // Simulate a drag: press at anchor → move to focus → release.
        // Two intermediate moves so the DraggedSelection focus-tracker
        // gets a chance to observe motion (the production handler gates
        // re-draws on focus-letter changes, so a single jump-move would
        // also work — but a real user produces a stream of moves).
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(anchorWindow, MouseButton.Left);
        });
        await Task.Delay(50);

        var midWindow = new Point((anchorWindow.X + focusWindow.X) / 2,
                                  (anchorWindow.Y + focusWindow.Y) / 2);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseMove(midWindow);
        });
        await Task.Delay(50);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseMove(focusWindow);
        });
        await Task.Delay(50);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseUp(focusWindow, MouseButton.Left);
        });
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"viewer reported: \"{viewerReportedText}\"");
        _out.WriteLine($"vm.SelectedText: \"{vm.SelectedText}\"");

        viewerReportedText.Should().NotBeNull(
            "TextSelected must fire after a complete press/move/release sequence");
        viewerReportedText.Should().Be(expectedText,
            "the selection between two specific letters must yield the run between them in reading order");

        // VM is updated asynchronously via SetSelectedTextAndCopyAsync —
        // give it a moment to land but still assert on it as that's
        // what the rest of the app sees.
        for (int i = 0; i < 10 && string.IsNullOrEmpty(vm.SelectedText); i++)
            await Task.Delay(50);
        vm.SelectedText.Should().Be(expectedText);
    }

    [FixedAvaloniaFact]
    public async Task DragOverFirstLine_AtNonDefaultZoom_SelectsExpectedReadingOrderText()
    {
        // Same single-line selection but with zoom kicked off the
        // 1.0x default. If the in-control hit-test mistakenly used
        // post-zoom DIPs (the bug we recently fixed for link clicks),
        // the selection at non-default zoom would land roughly off
        // by the zoom factor and never match the expected text.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        // Window must be large enough that the page at 2x zoom still
        // fits inside the viewport — otherwise the simulated click
        // lands off-window and never reaches the control. A 612-pt
        // page at 120 DPI is 1020 DIPs, ×2 = 2040 DIPs; sidebar+chrome
        // eats ~300 DIPs, so 2400 wide gives comfortable headroom.
        var window = new MainWindow { DataContext = vm, Width = 2400, Height = 1400 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        var anchor = ordered[0];
        var focus = ordered[10];
        Math.Abs(GlyphCenterY(anchor) - GlyphCenterY(focus))
            .Should().BeLessThan(2.0, "anchor and focus must share a line");
        var expectedText = TextSelectionEngine.JoinText(
            TextSelectionEngine.RangeBetween(ordered, anchor, focus));

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        // Manual zoom (not a fit mode) — direct ZoomLevel set otherwise
        // gets clamped back to FitWidth via ReapplyFitModeIfNeeded.
        vm.ZoomActualSizeCommand?.Execute().Subscribe();
        vm.ZoomInCommand?.Execute().Subscribe();
        vm.ZoomInCommand?.Execute().Subscribe();
        vm.ZoomInCommand?.Execute().Subscribe();
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer!.ZoomLevel.Should().BeGreaterThan(1.5,
            "zoom must propagate from VM to viewer for this regression check");
        viewer.ZoomLevel.Should().NotBe(1.0,
            "must be at non-default zoom to exercise post-zoom coord conversion");

        var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
        var anchorWindow = ToWindowPoint(anchor, page, overlay, window);
        var focusWindow = ToWindowPoint(focus, page, overlay, window);
        _out.WriteLine($"@{viewer.ZoomLevel:F2}x: anchor='{anchor.Value}' window={anchorWindow}, focus='{focus.Value}' window={focusWindow}");

        string? viewerReportedText = null;
        viewer.TextSelected += (_, e) => viewerReportedText = e.Text;

        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseDown(anchorWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(focusWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseUp(focusWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"@{viewer.ZoomLevel:F2}x: viewer reported \"{viewerReportedText}\", expected \"{expectedText}\"");
        viewerReportedText.Should().Be(expectedText,
            "selection must hit the same letter run regardless of zoom level");
    }

    [FixedAvaloniaFact]
    public async Task DragAcrossTwoLines_SelectsRangeIncludingLineBreak()
    {
        // Multi-line selection: anchor on line 1, focus deep into line 2.
        // The expected text contains a '\n' (added by JoinText whenever
        // consecutive letters are on different lines), so this also
        // verifies reading-order traversal across line boundaries.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);

        // Pick an anchor on the first line, then walk forward until we
        // find the first letter on a different line — that's the start
        // of line 2. Focus a few letters past that to land mid-line-2.
        var anchor = ordered[0];
        var anchorY = GlyphCenterY(anchor);
        int line2Start = -1;
        for (int i = 1; i < ordered.Count; i++)
        {
            if (Math.Abs(GlyphCenterY(ordered[i]) - anchorY) > 4.0)
            {
                line2Start = i;
                break;
            }
        }
        line2Start.Should().BeGreaterThan(0, "page must have a second line");
        var focus = ordered[Math.Min(line2Start + 5, ordered.Count - 1)];
        var expectedText = TextSelectionEngine.JoinText(
            TextSelectionEngine.RangeBetween(ordered, anchor, focus));
        expectedText.Should().Contain("\n", "selection should span at least one line break");

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var anchorWindow = ToWindowPoint(anchor, page, overlay, window);
        var focusWindow = ToWindowPoint(focus, page, overlay, window);

        string? viewerReportedText = null;
        viewer!.TextSelected += (_, e) => viewerReportedText = e.Text;

        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseDown(anchorWindow, MouseButton.Left));
        await Task.Delay(50);
        // Couple of intermediate moves so the focus tracker walks through
        // the page rather than jumping straight to focus.
        var midWindow = new Point((anchorWindow.X + focusWindow.X) / 2,
                                  (anchorWindow.Y + focusWindow.Y) / 2);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(midWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(focusWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseUp(focusWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"multi-line: viewer reported \"{viewerReportedText}\"");
        _out.WriteLine($"multi-line: expected     \"{expectedText}\"");
        viewerReportedText.Should().Be(expectedText);
    }

    [FixedAvaloniaFact]
    public async Task ClickOnSpecificVisibleLetter_HitTestReturnsThatLetter()
    {
        // Stronger version of the earlier tests: those used JoinText for
        // both expected and actual, so a JoinText bug or a hit-test that
        // consistently picks the wrong letter wouldn't fail them. Here
        // we click on a single specific letter (the 'H' of "Heartfelt"),
        // verify the resulting selection's text is exactly "H" — proving
        // the click landed on the letter the user clicked, not its
        // neighbour.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);

        // The 'H' at the very start of the page in reading order. Press
        // and release in place — that's a single-letter selection.
        var hLetter = ordered[0];
        hLetter.Value.Should().Be("H", "page 15's first letter in reading order is 'H' (Heartfelt)");

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var hWindow = ToWindowPoint(hLetter, page, overlay, window);

        string? viewerReportedText = null;
        viewer!.TextSelected += (_, e) => viewerReportedText = e.Text;

        // Click + immediately release at the same point — single-letter selection.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(hWindow, MouseButton.Left);
            window.MouseUp(hWindow, MouseButton.Left);
        });
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"single-click: viewer reported \"{viewerReportedText}\"");
        viewerReportedText.Should().Be("H",
            "clicking on the centre of the H glyph must select exactly that letter — " +
            "if the result is a different letter (or empty) the hit-test is off");
    }

    [FixedAvaloniaFact]
    public async Task SelectExactPhrase_ProducesPhraseTextWithoutTrailingExtras()
    {
        // The tightest test of the user's "wrong text selected" report:
        // pick a known visible phrase ("Heartfelt"), find the letters
        // that spell it, click on the first one, drag to the last one,
        // and assert the selection is *exactly* "Heartfelt" — not
        // "Heartfel" (focus picked the wrong letter) or "HeartfeltG"
        // (range overshot into the next word).
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        const string targetPhrase = "Heartfelt";

        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);

        // Find the contiguous run of letters in reading order whose
        // joined Values spell targetPhrase. This locates the visible
        // phrase rather than relying on emit/index order.
        int startIdx = -1;
        for (int i = 0; i + targetPhrase.Length <= ordered.Count; i++)
        {
            var slice = string.Concat(ordered.Skip(i).Take(targetPhrase.Length).Select(l => l.Value));
            if (slice == targetPhrase) { startIdx = i; break; }
        }
        startIdx.Should().BeGreaterThanOrEqualTo(0,
            $"page {targetPageNumber} must contain the phrase '{targetPhrase}' in its letters");

        var firstLetter = ordered[startIdx];
        var lastLetter = ordered[startIdx + targetPhrase.Length - 1];
        _out.WriteLine($"phrase '{targetPhrase}' spans letters [{startIdx}..{startIdx + targetPhrase.Length - 1}]");

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var startWindow = ToWindowPoint(firstLetter, page, overlay, window);
        var endWindow = ToWindowPoint(lastLetter, page, overlay, window);

        string? viewerReportedText = null;
        viewer!.TextSelected += (_, e) => viewerReportedText = e.Text;

        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseDown(startWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseUp(endWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"phrase: viewer reported \"{viewerReportedText}\"");
        viewerReportedText.Should().Be(targetPhrase,
            "selecting from the first to last letter of a known phrase must yield " +
            "exactly that phrase — no off-by-one, no extra trailing letters");
    }

    [FixedAvaloniaFact]
    public async Task AfterSelection_ClipboardHistoryGetsTheSelectedText()
    {
        // The "select then copy" flow: SelectedText is published, the
        // VM auto-copies via SetSelectedTextAndCopyAsync, and a new
        // ClipboardHistory entry is added with the selection. This test
        // pins down the auto-copy: if the clipboard history doesn't
        // grow, copy is broken even though SelectedText updated.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        var anchor = ordered[0];
        var focus = ordered[4];
        var expected = string.Concat(ordered.Take(5).Select(l => l.Value));

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var initialHistoryCount = vm.ClipboardHistory.Count;

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var startWindow = ToWindowPoint(anchor, page, overlay, window);
        var endWindow = ToWindowPoint(focus, page, overlay, window);

        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseDown(startWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseUp(endWindow, MouseButton.Left));

        // SetSelectedTextAndCopyAsync runs async — wait for the history
        // to grow or time out.
        for (int i = 0; i < 30 && vm.ClipboardHistory.Count == initialHistoryCount; i++)
            await Task.Delay(100);

        _out.WriteLine($"history count {initialHistoryCount} → {vm.ClipboardHistory.Count}");
        if (vm.ClipboardHistory.Count > initialHistoryCount)
            _out.WriteLine($"newest entry: \"{vm.ClipboardHistory[0].Text}\"");

        vm.SelectedText.Should().Be(expected);
        vm.ClipboardHistory.Count.Should().BeGreaterThan(initialHistoryCount,
            "selecting text in the viewer should automatically copy it and add a clipboard-history entry");
        vm.ClipboardHistory[0].Text.Should().Be(expected,
            "the newest clipboard-history entry must contain the same text the viewer selected");
        vm.ClipboardHistory[0].PageNumber.Should().Be(targetPageNumber);
    }

    [FixedAvaloniaFact]
    public async Task CtrlC_AfterPhraseSelection_CopiesExactlyTheSelectedPhrase()
    {
        // The user-reported "wrong text" bug — pressing Ctrl+C after a
        // letter-run selection. CopyTextAsync sees a non-empty
        // CurrentTextSelectionArea (the letter run's bounding box) and
        // re-extracts text from that rect via the text-extraction
        // service, which can pick up letters above/below/around the
        // run that aren't part of what the user selected. The fix:
        // Ctrl+C should use the already-correct SelectedText, not
        // re-extract from a 2-D bbox.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        var indexDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        const int targetPageNumber = 15;
        const string targetPhrase = "Heartfelt";
        var page = vm.PdfCoreDocument!.GetPage(targetPageNumber);
        var letters = page.Letters?.ToList() ?? new System.Collections.Generic.List<Letter>();
        var ordered = TextSelectionEngine.SortReadingOrder(letters);

        int startIdx = -1;
        for (int i = 0; i + targetPhrase.Length <= ordered.Count; i++)
        {
            var slice = string.Concat(ordered.Skip(i).Take(targetPhrase.Length).Select(l => l.Value));
            if (slice == targetPhrase) { startIdx = i; break; }
        }
        startIdx.Should().BeGreaterThanOrEqualTo(0);

        vm.CurrentPageIndex = targetPageNumber - 1;
        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var startWindow = ToWindowPoint(ordered[startIdx], page, overlay, window);
        var endWindow = ToWindowPoint(ordered[startIdx + targetPhrase.Length - 1], page, overlay, window);

        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseDown(startWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() =>
            window.MouseUp(endWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        // After the selection, vm.SelectedText is "Heartfelt".
        vm.SelectedText.Should().Be(targetPhrase, "selection drag must produce the exact phrase first");

        // Now invoke Ctrl+C (the CopyTextCommand). The buggy code path
        // re-extracts text from CurrentTextSelectionArea instead of
        // using SelectedText, so SelectedText would be overwritten with
        // a wider bbox extraction. After the fix, Ctrl+C uses
        // SelectedText directly, leaving it intact, and adds the same
        // text to ClipboardHistory.
        var historyCountBeforeCopy = vm.ClipboardHistory.Count;
        // Fire the command (non-blocking) and poll for the side-effect.
        // Awaiting Execute().ToTask() deadlocks here: the command body
        // awaits Dispatcher.UIThread.InvokeAsync, but we're holding the
        // UI-thread synchronisation context inside an [FixedAvaloniaFact]
        // and the await on ToTask() prevents the dispatcher from
        // pumping the InvokeAsync continuation.
        vm.CopyTextCommand.Execute().Subscribe();
        for (int i = 0; i < 50 && vm.ClipboardHistory.Count == historyCountBeforeCopy; i++)
            await Task.Delay(100);

        _out.WriteLine($"Ctrl+C: vm.SelectedText after copy = \"{vm.SelectedText}\"");
        _out.WriteLine($"Ctrl+C: history grew {historyCountBeforeCopy} → {vm.ClipboardHistory.Count}");
        if (vm.ClipboardHistory.Count > 0)
            _out.WriteLine($"  newest history entry: \"{vm.ClipboardHistory[0].Text}\"");

        vm.SelectedText.Should().Be(targetPhrase,
            "Ctrl+C must copy exactly the letter-run the user selected — not re-extract " +
            "from the run's bounding box, which can include extra glyphs the user didn't pick");
        vm.ClipboardHistory.Count.Should().BeGreaterThan(historyCountBeforeCopy,
            "Ctrl+C must add the copied text to ClipboardHistory regardless of OS clipboard availability");
        vm.ClipboardHistory[0].Text.Should().Be(targetPhrase,
            "the newest clipboard-history entry must contain the same letter-run the user selected");
    }

    private static double GlyphCenterY(Letter l)
        => (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) * 0.5;

    private static Point ToWindowPoint(Letter l, PdfPage page, Canvas overlay, Window window)
    {
        var r = l.GlyphRectangle;
        var center = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(
                page.PageNumber,
                new PdfRectangle(
                    (r.Left + r.Right) * 0.5,
                    (r.Bottom + r.Top) * 0.5,
                    (r.Left + r.Right) * 0.5,
                    (r.Bottom + r.Top) * 0.5)),
            RenderDpi);
        return overlay.TranslatePoint(new Point(center.X, center.Y), window) ?? default;
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
        }
        if (root is Decorator d && d.Child is Control dc)
        {
            var hit = FindNamedDescendant<T>(dc, name);
            if (hit != null) return hit;
        }
        if (root is ContentControl cc && cc.Content is Control ccc)
        {
            var hit = FindNamedDescendant<T>(ccc, name);
            if (hit != null) return hit;
        }
        return root.FindControl<T>(name);
    }
}
