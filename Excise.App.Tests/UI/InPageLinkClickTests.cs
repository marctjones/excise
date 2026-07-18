using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// User report: clicking a chapter title inside the rendered PDF
/// (e.g. on the table-of-contents page) doesn't navigate. The Pragmatic
/// book has 193 internal-link annotations on its TOC pages — these should
/// fire LinkClicked → set CurrentPageIndex.
/// </summary>
[Collection("AvaloniaTests")]
public class InPageLinkClickTests
{
    private readonly ITestOutputHelper _out;
    public InPageLinkClickTests(ITestOutputHelper o) { _out = o; }

    private const double RenderDpi = 120.0;

    /// <summary>
    /// Was a hardcoded "/home/marc/Downloads/..." path that exists on no
    /// machine but the original author's — this test has silently skipped
    /// everywhere else since it was written (the #619 "invisible coverage
    /// loss" pattern; same bug found and fixed in MultiEmbeddedFontLayoutTests.cs
    /// this session). Resolved via the same FindRepoFile convention every
    /// other local-corpus test in this project uses.
    /// </summary>
    private static string? FindPragmaticBook()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "local-real-world",
                "business-success-with-open-source_P1.0.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [FixedAvaloniaFact]
    public async Task ClickOnTocLink_NavigatesToDestinationPage()
    {
        var pragmaticBook = FindPragmaticBook();
        Assert.SkipWhen(pragmaticBook == null, "Pragmatic book corpus fixture not available locally.");
        // #653: this test's path was broken (hardcoded personal path) for so
        // long it never actually ran. Once fixed, it failed for a reason that
        // had nothing to do with the book's size or a layout-timing race (the
        // original hypothesis): the app's default ViewMode is Continuous
        // (restored in 543ada9), and in Continuous mode PdfViewerControl hides
        // the single-page ScrollViewer (`_scrollViewer.IsVisible = false` in
        // OnViewModeChanged). This test computes its click point via
        // InteractionLayer.TranslatePoint(..., window), which walks the
        // visual ancestor chain and returns null the moment any ancestor
        // (here, that hidden ScrollViewer) isn't part of a live, visible
        // layout — silently defaulting the click to window (0,0). See the
        // longer note at the InteractionLayer lookup below for why
        // InteractionLayer's own Bounds is a red herring, not the real
        // signal. Every other test in this file/MouseInputTests.cs that
        // clicks/hovers via InteractionLayer forces single-page first
        // (IsTextSelectionMode/IsRedactionMode = true does this as a side
        // effect); this test never did, so it silently depended on
        // single-page having been the default. Continuous mode has since
        // grown its own link hit-testing (#667; covered by
        // ContinuousLinkInteractionTests). This test forces single-page
        // explicitly, which is the correct scope for "does single-page link
        // click work".

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        // This test covers the SINGLE-PAGE link path; the continuous-mode
        // path (#667) is covered by ContinuousLinkInteractionTests. Force
        // single-page explicitly instead of relying on whatever the app's
        // current default happens to be.
        vm.ViewMode = PdfViewMode.SinglePage;

        await vm.LoadDocumentAsync(pragmaticBook);

        // Wait for the background text-index build to finish — its parser
        // walk shares state with PdfDocument's shared parser, and a
        // concurrent GetLinks() call here would race the lexer.
        // OperationStatus carries an "Indexing for search…" label while
        // the build runs and clears once done.
        // Under parallel test load, indexing can take longer, so use a generous timeout.
        var indexDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        // Plus a small grace period after status clears.
        await Task.Delay(500);

        // Find a page with at least one valid internal link (TOC pages
        // are around page 7-12 in this book).
        int linkPage = -1;
        PdfLink? targetLink = null;
        for (int p = 7; p <= 30 && p <= vm.TotalPages; p++)
        {
            var links = vm.PdfCoreDocument!.GetPage(p).GetLinks();
            var first = links.FirstOrDefault(l => l.DestinationPage != p);
            if (first != null) { linkPage = p; targetLink = first; break; }
        }
        targetLink.Should().NotBeNull("Pragmatic book has clickable TOC links");
        _out.WriteLine($"Using link on page {linkPage}: rect={targetLink!.Rect.Left:F0},{targetLink.Rect.Bottom:F0}-{targetLink.Rect.Right:F0},{targetLink.Rect.Top:F0} → page {targetLink.DestinationPage}");

        // Navigate to the link's host page so the renderer + overlays are set up.
        vm.CurrentPageIndex = linkPage - 1;
        for (int i = 0; i < 20; i++) { await Task.Delay(150); window.UpdateLayout(); }

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();

        // Convert the link's PDF-points rect to viewer DIPs through the same
        // mapper used by PdfViewerControl hit testing.
        var page = vm.PdfCoreDocument!.GetPage(linkPage);
        var center = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(
                page.PageNumber,
                new PdfRectangle(
                    (targetLink.Rect.Left + targetLink.Rect.Right) * 0.5,
                    (targetLink.Rect.Bottom + targetLink.Rect.Top) * 0.5,
                    (targetLink.Rect.Left + targetLink.Rect.Right) * 0.5,
                    (targetLink.Rect.Bottom + targetLink.Rect.Top) * 0.5)),
            RenderDpi);

        // Find the InteractionLayer Canvas to translate to window coords.
        // Note: InteractionLayer itself has no explicit size and its own
        // Bounds is always 0,0,0,0 in *either* view mode (production click
        // dispatch is deliberately attached at the UserControl root instead
        // — see the InitializeComponent comment in PdfViewerControl.axaml.cs
        // — "Pre-fix attachment was on _interactionLayer (zero-sized —
        // never received events)"). What actually matters for
        // TranslatePoint below to succeed is that every ANCESTOR (in
        // particular PdfScrollViewer) is visible and laid out — Continuous
        // view mode hides that ScrollViewer (#653), which is what silently
        // degenerates TranslatePoint to null (?? default = window (0,0)),
        // not InteractionLayer's own size.
        var interaction = FindNamedDescendant<Canvas>(viewer!, "InteractionLayer");
        interaction.Should().NotBeNull("InteractionLayer must exist");
        _out.WriteLine($"InteractionLayer Bounds={interaction!.Bounds}");

        // Sanity: hit-test the click point against the actual visual tree.
        var pointInWindow = interaction.TranslatePoint(new Point(center.X, center.Y), window) ?? default;
        _out.WriteLine($"Click target dip=({center.X:F1},{center.Y:F1}) → window=({pointInWindow.X:F1},{pointInWindow.Y:F1})");
        var hit = window.InputHitTest(pointInWindow) as Control;
        _out.WriteLine($"Hit at click point: {hit?.GetType().Name} Name='{hit?.Name}'");
        // Walk up to see what would receive the click event.
        Control? walk = hit;
        for (int i = 0; i < 6 && walk != null; i++)
        {
            _out.WriteLine($"  ancestor[{i}] {walk.GetType().Name} Name='{walk.Name}'");
            walk = walk.Parent as Control;
        }

        int linkFiredCount = 0;
        int observedDestPage = -1;
        viewer!.LinkClicked += (s2, args) =>
        {
            linkFiredCount++;
            observedDestPage = args.PageNumber;
            _out.WriteLine($"LinkClicked fired with page={args.PageNumber}");
        };

        // Diagnostic: also subscribe a raw PointerPressed listener at the
        // viewer level to confirm the click event is reaching the
        // PdfViewerControl at all.
        bool sawAnyPointerEvent = false;
        viewer.AddHandler(InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs ev) =>
            {
                sawAnyPointerEvent = true;
                var p = ev.GetPosition(viewer);
                _out.WriteLine($"raw PointerPressed at viewer-relative {p}");
            },
            handledEventsToo: true);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(pointInWindow, MouseButton.Left);
            window.MouseUp(pointInWindow, MouseButton.Left);
        });
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"After click: rawPointerEvent={sawAnyPointerEvent}, " +
                       $"linkFiredCount={linkFiredCount}, observedDestPage={observedDestPage}, " +
                       $"vm.CurrentPageIndex={vm.CurrentPageIndex + 1}");

        linkFiredCount.Should().Be(1,
            "PdfViewerControl.LinkClicked must fire exactly once per click within a link "
            + "annotation rect — the Tunnel|Bubble handler registration dispatched pointer "
            + "presses twice (#675)");
        observedDestPage.Should().Be(targetLink.DestinationPage);
        vm.CurrentPageIndex.Should().Be(targetLink.DestinationPage - 1,
            $"VM should navigate to the link's destination page ({targetLink.DestinationPage})");
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
