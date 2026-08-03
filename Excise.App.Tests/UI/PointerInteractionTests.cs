using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
using Excise.Avalonia.Services;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #827 batch A — real-gesture, effect-asserting pointer tests for interactive
/// surfaces that previously had only VM-command-level ("B") or no ("C")
/// coverage: external/dangerous link clicks, FormAuthoring drag-to-create,
/// the form-field checkbox toggle, thumbnail drag-reorder / click-navigate /
/// batch-select, and search-result row clicks.
///
/// Each test performs the REAL gesture on the REAL element (window.MouseDown/
/// MouseMove/MouseUp or a routed input event on the actual control) and
/// asserts the downstream effect — never invoking the VM method/command
/// directly. Where an existing suite already drives a real element for a
/// surface (form text-field Enter-commit and choice-combo commit live in
/// <see cref="FormFieldsOverlayTests"/>), only the genuinely-uncovered path is
/// added here.
/// </summary>
[Collection("AvaloniaTests")]
public class PointerInteractionTests : IDisposable
{
    /// <summary>
    /// Every window this class shows is closed after the test, including
    /// when it fails (#706). These tests previously showed windows and never
    /// closed them, so they accumulated for the whole run and perturbed
    /// pointer routing and focus for every later test.
    /// </summary>
    private readonly ShownWindowTracker _windows = new();

    public void Dispose() => _windows.Dispose();

    private readonly ITestOutputHelper _out;
    public PointerInteractionTests(ITestOutputHelper o) { _out = o; }

    private const double RenderDpi = 120.0;

    // ─────────────────────────── Link clicks (1, 2) ───────────────────────────

    [FixedAvaloniaFact]
    public async Task ExternalLinkClick_RealPointer_FiresEventAndRunsConfirmDialog()
    {
        var path = WriteTempPdf(BuildSinglePageLinkPdf(
            "<< /Type /Annot /Subtype /Link /Rect [72 400 320 440] " +
            "/A << /S /URI /URI (https://example.com/) >> >>"));
        try
        {
            var (vm, dialog) = CreateViewModelWithDialog();
            dialog.ConfirmResult = false; // decline — never reaches a real browser open
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            _windows.Show(window);
            await Task.Delay(150);
            vm.ViewMode = PdfViewMode.SinglePage;

            await vm.LoadDocumentAsync(path);
            var viewer = await SettleSinglePage(window, vm);

            string? firedUri = null;
            viewer.ExternalLinkClicked += (_, e) => firedUri = e.Uri;

            await ClickContentRect(window, viewer, vm, new PdfRectangle(72, 400, 320, 440));

            firedUri.Should().Be("https://example.com/",
                "a real click on an ExternalUri link rect must raise ExternalLinkClicked with the URI");
            dialog.ConfirmCallCount.Should().Be(1,
                "the click must route through OnExternalLinkClicked → OpenExternalLinkCommand, " +
                "which always confirms before navigating (PDFs are a phishing vector)");
            dialog.LastConfirmMessage.Should().Contain("https://example.com/",
                "the confirm dialog must show the actual target URL");
        }
        finally { TryDelete(path); }
    }

    [FixedAvaloniaFact]
    public async Task DangerousLinkClick_RealPointer_FiresEventAndRunsRefusal()
    {
        var path = WriteTempPdf(BuildSinglePageLinkPdf(
            "<< /Type /Annot /Subtype /Link /Rect [72 400 320 440] /A << /S /Launch >> >>"));
        try
        {
            var (vm, dialog) = CreateViewModelWithDialog();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            _windows.Show(window);
            await Task.Delay(150);
            vm.ViewMode = PdfViewMode.SinglePage;

            await vm.LoadDocumentAsync(path);
            var viewer = await SettleSinglePage(window, vm);

            string? firedAction = null;
            viewer.DangerousLinkClicked += (_, e) => firedAction = e.ActionType;

            await ClickContentRect(window, viewer, vm, new PdfRectangle(72, 400, 320, 440));

            firedAction.Should().Be("Launch",
                "a real click on a /Launch link must raise DangerousLinkClicked with the action type");
            dialog.MessageCallCount.Should().Be(1,
                "the click must route through OnDangerousLinkClicked → ShowDangerousLinkRefusalCommand");
            dialog.LastMessageTitle.Should().Be("Link Blocked");
            dialog.LastMessageBody.Should().Contain("launches an external application or file");
            dialog.ConfirmCallCount.Should().Be(0,
                "a dangerous link is refused outright — it must never reach the open-link confirm dialog");
        }
        finally { TryDelete(path); }
    }

    // ─────────────────────── FormAuthoring drag (3) ───────────────────────

    [FixedAvaloniaFact]
    public async Task FormAuthoringDrag_RealPointer_FiresFormFieldRectDrawnWithFlippedRect()
    {
        var path = WriteTempPdf(BuildBarePdf());
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            _windows.Show(window);
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);
            vm.CurrentPageIndex = 0;
            vm.ToggleFormAuthoringModeCommand.Execute().Subscribe();
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.InteractionMode.Should().Be(InteractionMode.FormAuthoring);
            await Settle(window);

            var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
            var page = vm.PdfCoreDocument!.GetPage(1);
            // Drag a field box over PDF content rect [120,500]-[360,560].
            var contentRect = new PdfRectangle(120, 500, 360, 560);
            var (start, end) = ToWindowDragPoints(contentRect, page, overlay, window);

            FormFieldRectDrawnEventArgs? drawn = null;
            viewer.FormFieldRectDrawn += (_, e) => drawn = e;

            await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));
            for (int i = 0; i < 3; i++) { await Task.Delay(80); window.UpdateLayout(); }

            drawn.Should().NotBeNull("a real drag in FormAuthoring mode must fire FormFieldRectDrawn");
            drawn!.PageNumber.Should().Be(1, "the field belongs to the page under the drag");
            // The event carries Y-flipped PDF points (bottom-left origin),
            // round-tripped from the window drag — assert it lands in the
            // expected PDF band, not screen space.
            var r = drawn.Rect;
            r.Left.Should().BeApproximately(120, 12);
            r.Right.Should().BeApproximately(360, 12);
            r.Bottom.Should().BeApproximately(500, 12);
            r.Top.Should().BeApproximately(560, 12);
            r.Top.Should().BeGreaterThan(r.Bottom, "PDF rects are bottom-left origin (Top > Bottom)");
            _out.WriteLine($"FormFieldRectDrawn rect={r} page={drawn.PageNumber}");
        }
        finally { TryDelete(path); }
    }

    // ─────────────────── Form-field checkbox toggle (4) ───────────────────

    [FixedAvaloniaFact]
    public async Task FormFieldCheckbox_RealToggle_CommitsValueAndMarksDirty()
    {
        // Item 4 completion: text-field Enter-commit and choice-combo commit are
        // already covered by FormFieldsOverlayTests (EditingTextField_… /
        // RadioButtonGroup_…). The single-checkbox path
        // (CreateButtonFieldInput → IsCheckedChanged → CommitFieldEdit) had no
        // gesture test — RadioButtonGroup_… even asserts checkboxes are absent.
        var path = WriteTempPdf(BuildCheckboxFormPdf());
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            _windows.Show(window);
            await Task.Delay(150);
            // Single-page so the form-field overlay (single-page FormFieldsLayer)
            // is the laid-out surface and its inputs get real, clickable bounds.
            vm.ViewMode = PdfViewMode.SinglePage;

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            var formLayer = FindNamedDescendant<Canvas>(viewer, "FormFieldsLayer")!;
            for (int i = 0; i < 40 && formLayer.Children.OfType<CheckBox>().FirstOrDefault() == null; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var checkBox = formLayer.Children.OfType<CheckBox>().Single();
            checkBox.IsChecked.Should().BeFalse("fixture field value is Off");
            for (int i = 0; i < 40 && checkBox.Bounds == default(Rect); i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }
            checkBox.Bounds.Should().NotBe(default(Rect),
                "the overlay checkbox must be laid out before it can be clicked");

            FormFieldEditedEventArgs? edited = null;
            viewer.FormFieldEdited += (_, e) => edited = e;

            // Real click on the actual overlay checkbox.
            await ClickControlCentre(window, checkBox);

            checkBox.IsChecked.Should().BeTrue("clicking must toggle the checkbox on");
            edited.Should().NotBeNull("toggling the checkbox must fire FormFieldEdited");
            edited!.NewValue.Should().Be("Yes");
            vm.PdfCoreDocument!.GetAcroForm()!.FindField("Accept")!.Value.Should().Be("Yes",
                "the toggle must mutate the underlying PdfField value");
            vm.FileState.HasUnsavedChanges.Should().BeTrue(
                "editing a form field must dirty the document");
        }
        finally { TryDelete(path); }
    }

    // ───────────────────────── Thumbnails (5, 6, 7) ─────────────────────────

    [FixedAvaloniaFact]
    public async Task ThumbnailDragReorder_RealPointer_ReordersPagesAndNoOpsOnSelfDrop()
    {
        var path = TestPdfGenerator.CreateMultiPagePdf(
            Path.Combine(Path.GetTempPath(), $"excise-thumbs-{Guid.NewGuid():N}.pdf"), 4);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1400 };
            _windows.Show(window);
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);
            var thumbs = await SettleThumbs(window, vm, 0, 1);

            // Page 1's content marker — CreateMultiPagePdf stamps "Page N Content".
            new TextExtractor(vm.PdfCoreDocument!.GetPage(1)).ExtractText()
                .Should().Contain("Page 1 Content");

            // No-op guard first: drag A onto itself must not reorder.
            await DragThumbnail(window, thumbs[0], thumbs[0]);
            await Task.Delay(120);
            new TextExtractor(vm.PdfCoreDocument!.GetPage(1)).ExtractText()
                .Should().Contain("Page 1 Content", "dropping a thumbnail on itself is a no-op");

            // Real reorder: drag page 1 (index 0) → page-2 slot (index 1).
            await DragThumbnail(window, thumbs[0], thumbs[1]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline &&
                   !new TextExtractor(vm.PdfCoreDocument!.GetPage(2)).ExtractText().Contains("Page 1 Content"))
            {
                await Task.Delay(80);
                window.UpdateLayout();
            }

            new TextExtractor(vm.PdfCoreDocument!.GetPage(2)).ExtractText()
                .Should().Contain("Page 1 Content",
                    "dragging thumbnail 0 onto slot 1 must move the original first page to position 2 (index 1)");
        }
        finally { TryDelete(path); }
    }

    [FixedAvaloniaFact]
    public async Task ThumbnailClick_RealPointer_NavigatesToThatPage()
    {
        var path = TestPdfGenerator.CreateMultiPagePdf(
            Path.Combine(Path.GetTempPath(), $"excise-thumbs-{Guid.NewGuid():N}.pdf"), 4);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1400 };
            _windows.Show(window);
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);
            var thumbs = await SettleThumbs(window, vm, 1);

            vm.CurrentPageIndex.Should().Be(0);

            // A plain click (down+up on the same thumbnail) must navigate.
            await DragThumbnail(window, thumbs[0], thumbs[0]);
            for (int i = 0; i < 30 && vm.CurrentPageIndex != 1; i++)
            {
                await KeyboardTestHelpers.FlushDispatcherAsync();
                await Task.Delay(40);
            }

            vm.CurrentPageIndex.Should().Be(1,
                "clicking the second thumbnail must navigate the viewer to page index 1");
        }
        finally { TryDelete(path); }
    }

    [FixedAvaloniaFact]
    public async Task ThumbnailBatchCheckbox_RealClick_AddsPageToSelectionSet()
    {
        var path = TestPdfGenerator.CreateMultiPagePdf(
            Path.Combine(Path.GetTempPath(), $"excise-thumbs-{Guid.NewGuid():N}.pdf"), 4);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1400 };
            _windows.Show(window);
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);
            var thumbs = await SettleThumbs(window, vm, 1);

            var checkBox = thumbs[0].GetVisualDescendants().OfType<CheckBox>().First();
            checkBox.IsChecked.Should().NotBe(true);
            vm.PageThumbnails[1].IsMarkedForPageOperation.Should().BeFalse();

            await ClickControlCentre(window, checkBox);

            checkBox.IsChecked.Should().BeTrue();
            vm.PageThumbnails[1].IsMarkedForPageOperation.Should().BeTrue(
                "checking a thumbnail's batch checkbox must add its page to the batch-operation selection set");
        }
        finally { TryDelete(path); }
    }

    // ────────────────────────── Search row click (8) ──────────────────────────

    [FixedAvaloniaFact]
    public async Task SearchResultRow_RealPointer_JumpsToMatchPage()
    {
        var path = TestPdfGenerator.CreateMultiPagePdf(
            Path.Combine(Path.GetTempPath(), $"excise-search-{Guid.NewGuid():N}.pdf"), 4);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1400 };
            _windows.Show(window);
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);

            // Populate matches directly (deterministic; the click gesture, not
            // the extractor, is what's under test) and open the results panel.
            var match = new PdfSearchService.SearchMatch
            {
                PageIndex = 2,
                MatchedText = "target",
                Context = "…target…",
            };
            vm.SearchMatches = new ObservableCollection<PdfSearchService.SearchMatch> { match };
            vm.IsSearchVisible = true;
            vm.ShowSearchResultsPanel.Should().BeTrue();

            var rowPoint = await SettleSearchResultRow(window, vm);
            vm.CurrentPageIndex.Should().NotBe(2);

            await ClickPoint(window, rowPoint);
            for (int i = 0; i < 30 && vm.CurrentPageIndex != 2; i++)
            {
                await KeyboardTestHelpers.FlushDispatcherAsync();
                await Task.Delay(40);
            }

            vm.CurrentPageIndex.Should().Be(2,
                "clicking a search-result row must jump the viewer to that match's page");
        }
        finally { TryDelete(path); }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private sealed class RecordingDialogService : IUserDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public string? LastConfirmMessage { get; private set; }
        public bool ConfirmResult { get; set; }
        public int MessageCallCount { get; private set; }
        public string? LastMessageTitle { get; private set; }
        public string? LastMessageBody { get; private set; }

        public Task ShowMessageAsync(string title, string message)
        {
            MessageCallCount++;
            LastMessageTitle = title;
            LastMessageBody = message;
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }
    }

    private static (MainWindowViewModel vm, RecordingDialogService dialog) CreateViewModelWithDialog()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dialog = new RecordingDialogService();
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
        return (vm, dialog);
    }

    private static async Task Settle(Window window)
    {
        for (int i = 0; i < 12; i++) { await Task.Delay(120); window.UpdateLayout(); }
    }

    /// <summary>Force single-page layout and wait until the overlay is laid out.</summary>
    private async Task<PdfViewerControl> SettleSinglePage(Window window, MainWindowViewModel vm)
    {
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var scroll = FindNamedDescendant<ScrollViewer>(viewer, "PdfScrollViewer")!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && (!scroll.IsVisible || scroll.Bounds == default(Rect)))
        {
            await Task.Delay(120);
            window.UpdateLayout();
        }
        scroll.IsVisible.Should().BeTrue("single-page ScrollViewer must be visible to translate a click point");
        return viewer;
    }

    /// <summary>Click the centre of a PDF content rect (single-page overlay basis).</summary>
    private async Task ClickContentRect(Window window, PdfViewerControl viewer,
        MainWindowViewModel vm, PdfRectangle contentRect)
    {
        var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
        var page = vm.PdfCoreDocument!.GetPage(1);
        var cx = (contentRect.Left + contentRect.Right) * 0.5;
        var cy = (contentRect.Bottom + contentRect.Top) * 0.5;
        var pt = ToWindowPoint(new PdfRectangle(cx, cy, cx, cy), page, overlay, window);
        _out.WriteLine($"Click content-centre ({cx:F0},{cy:F0}) → window {pt}");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(pt, MouseButton.Left);
            window.MouseUp(pt, MouseButton.Left);
        });
        for (int i = 0; i < 4; i++) { await Task.Delay(80); window.UpdateLayout(); }
    }

    /// <summary>
    /// Wait until the thumbnails at the given page indices are realised and
    /// hit-testable at the same time, returning their buttons. The thumbnail
    /// scroll viewport only shows a couple of thumbnails at once, so callers
    /// must request adjacent indices that fit together (e.g. 0 and 1).
    /// </summary>
    private async Task<Button[]> SettleThumbs(Window window, MainWindowViewModel vm, params int[] pageIndices)
    {
        vm.IsThumbnailsSidebarVisible.Should().BeTrue("thumbnails sidebar is visible by default");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        Button?[] found = new Button?[pageIndices.Length];
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(120);
            window.UpdateLayout();
            for (int i = 0; i < pageIndices.Length; i++)
                found[i] = PickHitTestableThumb(window, pageIndices[i]);
            if (found.All(b => b != null)) break;
        }
        found.Should().NotContainNulls(
            "the requested thumbnails must all be realised and hit-testable together");
        return found.Select(b => b!).ToArray();
    }

    /// <summary>
    /// The realised, on-screen thumbnail <see cref="Button"/> for a page index.
    /// The visual tree can hold several Button instances per thumbnail (template
    /// / realisation churn); only the one whose centre actually hit-tests back
    /// to its own <c>PageThumbnail</c> can receive a real pointer gesture — the
    /// others give a valid TranslatePoint but a click there lands on the
    /// scroll presenter. Pick by hit-test, never by list position.
    /// </summary>
    private static Button? PickHitTestableThumb(Window window, int pageIndex)
    {
        foreach (var b in window.GetVisualDescendants()
                     .OfType<Button>()
                     .Where(x => x.DataContext is Excise.App.Models.PageThumbnail t && t.PageIndex == pageIndex
                                 && x.Bounds != default(Rect)))
        {
            var c = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window);
            if (c is null) continue;
            var hit = window.InputHitTest(c.Value) as Visual;
            while (hit != null)
            {
                if (hit is Control ctl && ctl.DataContext is Excise.App.Models.PageThumbnail pt
                    && pt.PageIndex == pageIndex)
                    return b;
                hit = hit.GetVisualParent();
            }
        }
        return null;
    }

    /// <summary>
    /// Real left-click at the centre of a control (pointer press+release in
    /// bounds), then pump the dispatcher so the synthesised Click job runs —
    /// the proven headless pattern from SecurityDialogUiTests.ClickAsync.
    /// </summary>
    private static async Task ClickControlCentre(Window window, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window) ?? default;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static async Task ClickPoint(Window window, Point p)
    {
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static async Task DragThumbnail(Window window, Button from, Button to)
    {
        // No BringIntoView here: the callers have already confirmed both
        // thumbnails are laid out and hit-testable at the resting scroll
        // position (SettleThumbs). Scrolling now would move each target into the
        // same viewport slot and collapse `from` and `to` onto one point.
        var pFrom = from.TranslatePoint(new Point(from.Bounds.Width / 2, from.Bounds.Height / 2), window) ?? default;
        var pTo = to.TranslatePoint(new Point(to.Bounds.Width / 2, to.Bounds.Height / 2), window) ?? default;
        window.MouseDown(pFrom, MouseButton.Left);
        window.MouseMove(pTo);
        window.MouseUp(pTo, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    /// <summary>
    /// The realised search-result <see cref="Button"/> and a window point that
    /// hit-tests back to it. Same rationale as <see cref="PickHitTestableThumb"/>:
    /// the ItemsControl inside the results ScrollViewer can hold a stale extra
    /// Button whose TranslatePoint is valid but whose screen area is covered by
    /// the scroll presenter, so pick the instance a real click can actually land on.
    /// </summary>
    private async Task<Point> SettleSearchResultRow(Window window, MainWindowViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(120);
            window.UpdateLayout();
            // Scan every laid-out control inside a match row (the row Button's
            // own centre can sit over a null-background gap that hit-tests to the
            // scroll presenter); the TextBlocks that carry the match text are
            // solid content. Return the first point that actually hit-tests back
            // into a SearchMatch-bound element — that's where a real click lands.
            foreach (var ctl in window.GetVisualDescendants()
                         .OfType<Control>()
                         .Where(c => c.Bounds != default(Rect) && IsInSearchMatchRow(c)))
            {
                var c = ctl.TranslatePoint(new Point(ctl.Bounds.Width / 2, ctl.Bounds.Height / 2), window);
                if (c is null) continue;
                var hit = window.InputHitTest(c.Value) as Visual;
                while (hit != null)
                {
                    if (hit is Control h && DataContextIsSearchMatch(h))
                        return c.Value;
                    hit = hit.GetVisualParent();
                }
            }
        }
        throw new Xunit.Sdk.XunitException(
            "the search-results panel must render a hit-testable clickable row per match");
    }

    private static bool DataContextIsSearchMatch(Control c) =>
        c.DataContext is PdfSearchService.SearchMatch;

    private static bool IsInSearchMatchRow(Control c)
    {
        Visual? v = c;
        while (v != null)
        {
            if (v is Control ctl && DataContextIsSearchMatch(ctl)) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static Point ToWindowPoint(PdfRectangle contentRect, PdfPage page, Canvas overlay, Window window)
    {
        var viewerPoint = PdfCoordinateMapper.ToViewerDips(
            page, PdfPageRect.FromContentPoints(page.PageNumber, contentRect), RenderDpi);
        return overlay.TranslatePoint(new Point(viewerPoint.X, viewerPoint.Y), window) ?? default;
    }

    private static (Point Start, Point End) ToWindowDragPoints(
        PdfRectangle contentRect, PdfPage page, Canvas overlay, Window window)
    {
        var viewerRect = PdfCoordinateMapper.ToViewerDips(
            page, PdfPageRect.FromContentPoints(page.PageNumber, contentRect), RenderDpi);
        var start = overlay.TranslatePoint(new Point(viewerRect.X, viewerRect.Y), window) ?? default;
        var end = overlay.TranslatePoint(new Point(viewerRect.Right, viewerRect.Y2), window) ?? default;
        return (start, end);
    }

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-pointer-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>One-page PDF (612×792, empty content) carrying the given /Annots entry.</summary>
    private static byte[] BuildSinglePageLinkPdf(string annotObject)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(annotObject);
        sb.AppendLine("endobj");
        return FinishPdf(sb, new[] { o1, o2, o3, o4, o5 });
    }

    /// <summary>One-page PDF with a single checkbox (Btn) form field defaulting Off.</summary>
    private static byte[] BuildCheckboxFormPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Btn /T (Accept) /V /Off /AS /Off " +
                      "/Rect [72 680 96 704] /P 3 0 R /AP << /N << /Yes <<>> /Off <<>> >> >> >>");
        sb.AppendLine("endobj");
        return FinishPdf(sb, new[] { o1, o2, o3, o4, o5 });
    }

    private static byte[] BuildBarePdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        return FinishPdf(sb, new[] { o1, o2, o3, o4 });
    }

    private static byte[] FinishPdf(StringBuilder sb, long[] objOffsets)
    {
        long xref = sb.Length;
        int count = objOffsets.Length + 1;
        sb.AppendLine("xref");
        sb.AppendLine($"0 {count}");
        sb.AppendLine("0000000000 65535 f ");
        foreach (var off in objOffsets)
            sb.AppendLine($"{off:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($"<< /Size {count} /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static T? FindNamedDescendant<T>(Control root, string? name) where T : Control
    {
        if ((name == null || root.Name == name) && root is T t) return t;
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
        return name == null ? null : root.FindControl<T>(name);
    }
}
