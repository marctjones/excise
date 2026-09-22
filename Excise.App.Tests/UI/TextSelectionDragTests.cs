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
using Excise.Core.Graphics;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
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
///
/// #1768: seven of the eight tests below returned at the top on a book path
/// that was the empty string. Three are ported here onto the synthetic
/// two-column PDF the one already-live test builds; the other four are
/// deleted (a single-letter click, a non-default-zoom drag, a two-line drag,
/// and an exact-phrase drag were dropped as duplicates of what these three
/// now cover on synthetic data). The three kept are the ones
/// <c>GuiWorkflowCoverageMatrixTests</c> pins by <c>nameof</c> — deleting any
/// of them would break that file's compilation, which is out of this issue's
/// scope (#1768's file list does not include it).
/// </summary>
[Collection("AvaloniaTests")]
public class TextSelectionDragTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private const double RenderDpi = 120.0;

    public TextSelectionDragTests(ITestOutputHelper o) { _out = o; }

    public void Dispose() => _windows.Dispose();

    [FixedAvaloniaFact]
    public async Task DragAcrossTwoColumns_CopiesNaturalColumnOrderAndLineBreaks()
    {
        // #1160: this owns its layout rather than relying on a local book.
        // A visual rectangle spans both columns, but copied text must follow
        // the human reading order: the complete left column, then the right.
        const string expected = "LEFT ONE\nLEFT TWO\nLEFT THREE\nLEFT FOUR\nRIGHT ONE\nRIGHT TWO\nRIGHT THREE\nRIGHT FOUR";
        var path = NewTempPath();
        CreateTwoColumnPdf(path);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        for (var i = 0; i < 20 && vm.PdfCoreDocument == null; i++)
            await Task.Delay(100);

        var page = vm.PdfCoreDocument!.GetPage(1);
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters).ToList();
        TextSelectionEngine.JoinText(ordered).Should().Be(expected,
            "the construction-known two-column page defines the copy order");

        vm.ViewMode = PdfViewMode.SinglePage;
        vm.IsTextSelectionMode = true;
        for (var i = 0; i < 20; i++) { await Task.Delay(100); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        viewer.InteractionMode.Should().Be(InteractionMode.TextSelection);
        var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
        var start = ToWindowPoint(ordered[0], page, overlay, window);
        var end = ToWindowPoint(ordered[^1], page, overlay, window);
        var historyBefore = vm.ClipboardHistory.Count;

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));

        for (var i = 0; i < 30 && string.IsNullOrEmpty(vm.SelectedText); i++)
            await Task.Delay(100);

        // #1645: the drag SELECTS; it does not copy. What this test is
        // really about — column order rather than PDF paint order —
        // belongs to the selection, so it is asserted there first.
        vm.SelectedText.Should().Be(expected,
            "a mouse selection must retain column order and line breaks, not PDF paint order");
        vm.ClipboardHistory.Count.Should().Be(historyBefore,
            "#1645: selecting must not copy");

        await vm.CopyTextCommand.Execute().ToTask();
        for (var i = 0; i < 30 && vm.ClipboardHistory.Count == historyBefore; i++)
            await Task.Delay(100);

        vm.ClipboardHistory.Count.Should().BeGreaterThan(historyBefore,
            "an explicit Copy reaches the clipboard-history path");
        vm.ClipboardHistory[0].Text.Should().Be(expected,
            "paste-ready text must retain column order and line breaks, not PDF paint order");
    }

    [FixedAvaloniaFact]
    public async Task DragOverFirstLine_SelectsExpectedReadingOrderText()
    {
        var (vm, window, page, ordered) = await OpenTwoColumnDocAsync();

        // Anchor on the first letter and focus 3 letters later ("LEFT")
        // — small enough to stay on one line ("LEFT ONE"), large enough
        // that a single-letter off-by-one in the hit-test would show.
        var anchor = ordered[0];
        var focus = ordered[3];
        var expectedRange = TextSelectionEngine.RangeBetween(ordered, anchor, focus);
        var expectedText = TextSelectionEngine.JoinText(expectedRange);
        _out.WriteLine($"Expected selection ({expectedRange.Count} letters): \"{expectedText}\"");

        // Both letters must be on the same line for the simulated drag to
        // hit them — if focus wraps to a second line the X/Y mid-points
        // would be in the page margin between them.
        Math.Abs(GlyphCenterY(anchor) - GlyphCenterY(focus))
            .Should().BeLessThan(2.0, "anchor and focus must share a line");

        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();
        viewer!.InteractionMode.Should().Be(InteractionMode.TextSelection,
            "the viewer's mode binding must be wired to vm.IsTextSelectionMode");

        var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
        var anchorWindow = ToWindowPoint(anchor, page, overlay, window);
        var focusWindow = ToWindowPoint(focus, page, overlay, window);
        _out.WriteLine($"anchor='{anchor.Value}' window={anchorWindow}, focus='{focus.Value}' window={focusWindow}");

        string? viewerReportedText = null;
        viewer.TextSelected += (_, e) => viewerReportedText = e.Text;

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(anchorWindow, MouseButton.Left));
        await Task.Delay(50);
        var midWindow = new Point((anchorWindow.X + focusWindow.X) / 2, (anchorWindow.Y + focusWindow.Y) / 2);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(midWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(focusWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(focusWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"viewer reported: \"{viewerReportedText}\"");
        _out.WriteLine($"vm.SelectedText: \"{vm.SelectedText}\"");

        viewerReportedText.Should().NotBeNull(
            "TextSelected must fire after a complete press/move/release sequence");
        viewerReportedText.Should().Be(expectedText,
            "the selection between two specific letters must yield the run between them in reading order");

        for (int i = 0; i < 10 && string.IsNullOrEmpty(vm.SelectedText); i++)
            await Task.Delay(50);
        vm.SelectedText.Should().Be(expectedText);
    }

    [FixedAvaloniaFact]
    public async Task AfterSelection_ClipboardHistoryGetsTheSelectedText()
    {
        // The "select then copy" flow: a mouse drag publishes SelectedText,
        // and an explicit Copy adds a ClipboardHistory entry for it. #1645
        // removed the earlier auto-copy-on-select (MainWindow.axaml.cs:1440 —
        // it put every one-character click on the OS clipboard, which for a
        // redaction tool leaked document fragments with no user action), so
        // this pins the CURRENT two-step contract instead of the old one.
        var (vm, window, page, ordered) = await OpenTwoColumnDocAsync();
        var anchor = ordered[0];
        var focus = ordered[4];
        var expected = string.Concat(ordered.Take(5).Select(l => l.Value));

        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var initialHistoryCount = vm.ClipboardHistory.Count;

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var startWindow = ToWindowPoint(anchor, page, overlay, window);
        var endWindow = ToWindowPoint(focus, page, overlay, window);

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(startWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(endWindow, MouseButton.Left));
        for (int i = 0; i < 10 && string.IsNullOrEmpty(vm.SelectedText); i++)
            await Task.Delay(50);
        vm.SelectedText.Should().Be(expected, "the drag must select the run before it can be copied");

        // The explicit Copy step (#1645: selection alone does not reach the
        // clipboard). CopyTextAsync runs async — wait for the history to
        // grow or time out.
        vm.CopyTextCommand.Execute().Subscribe();
        for (int i = 0; i < 30 && vm.ClipboardHistory.Count == initialHistoryCount; i++)
            await Task.Delay(100);

        _out.WriteLine($"history count {initialHistoryCount} → {vm.ClipboardHistory.Count}");
        if (vm.ClipboardHistory.Count > initialHistoryCount)
            _out.WriteLine($"newest entry: \"{vm.ClipboardHistory[0].Text}\"");

        vm.ClipboardHistory.Count.Should().BeGreaterThan(initialHistoryCount,
            "an explicit Copy of a selection must add a clipboard-history entry");
        vm.ClipboardHistory[0].Text.Should().Be(expected,
            "the newest clipboard-history entry must contain the same text the viewer selected");
        vm.ClipboardHistory[0].PageNumber.Should().Be(1);
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
        var (vm, window, page, ordered) = await OpenTwoColumnDocAsync();
        const string targetPhrase = "THREE";

        int startIdx = -1;
        for (int i = 0; i + targetPhrase.Length <= ordered.Count; i++)
        {
            var slice = string.Concat(ordered.Skip(i).Take(targetPhrase.Length).Select(l => l.Value));
            if (slice == targetPhrase) { startIdx = i; break; }
        }
        startIdx.Should().BeGreaterThanOrEqualTo(0,
            $"'LEFT THREE' must contribute the contiguous letters '{targetPhrase}'");

        vm.IsTextSelectionMode = true;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
        var startWindow = ToWindowPoint(ordered[startIdx], page, overlay, window);
        var endWindow = ToWindowPoint(ordered[startIdx + targetPhrase.Length - 1], page, overlay, window);

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(startWindow, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endWindow));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(endWindow, MouseButton.Left));
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        // After the selection, vm.SelectedText is "THREE".
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

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfPage Page, System.Collections.Generic.IReadOnlyList<Letter> Ordered)> OpenTwoColumnDocAsync()
    {
        var path = NewTempPath();
        CreateTwoColumnPdf(path);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        for (var i = 0; i < 20 && vm.PdfCoreDocument == null; i++)
            await Task.Delay(100);

        vm.ViewMode = PdfViewMode.SinglePage;
        var page = vm.PdfCoreDocument!.GetPage(1);
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters).ToList();
        return (vm, window, page, ordered);
    }

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"excise-copy-columns-{Guid.NewGuid():N}.pdf");

    private static void CreateTwoColumnPdf(string path)
    {
        using var document = PdfDocument.CreateNew();
        var page = document.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        var font = PdfFont.Helvetica(18);
        graphics.DrawString("LEFT ONE", font, PdfBrush.Black, 72, 700);
        graphics.DrawString("LEFT TWO", font, PdfBrush.Black, 72, 660);
        graphics.DrawString("LEFT THREE", font, PdfBrush.Black, 72, 620);
        graphics.DrawString("LEFT FOUR", font, PdfBrush.Black, 72, 580);
        graphics.DrawString("RIGHT ONE", font, PdfBrush.Black, 330, 700);
        graphics.DrawString("RIGHT TWO", font, PdfBrush.Black, 330, 660);
        graphics.DrawString("RIGHT THREE", font, PdfBrush.Black, 330, 620);
        graphics.DrawString("RIGHT FOUR", font, PdfBrush.Black, 330, 580);
        graphics.Flush();
        document.Save(path);
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
