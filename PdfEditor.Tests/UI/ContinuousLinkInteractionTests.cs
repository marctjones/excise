using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Avalonia.Controls;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

/// <summary>
/// In-page link click and hover in CONTINUOUS view mode (#667).
///
/// Continuous scroll is the app's default view mode, yet link hit-testing
/// originally existed only for single-page mode — on a fresh install,
/// clicking a table-of-contents link inside the rendered page did nothing.
/// The #653-fixed tests (InPageLinkClickTests / MouseInputTests) force
/// single-page, which is correct for what they test but leaves the default
/// mode's link path uncovered. These tests drive continuous mode directly:
/// real headless pointer events through the window, poll-based waits, same
/// Pragmatic-book TOC fixture.
///
/// The window point is computed independently of the production hit-test:
/// production maps pointer → ItemsControl coords → slot TopDip math →
/// ContinuousDips → content points, while this test goes content points →
/// ContinuousDips → the realized container's ACTUAL layout position →
/// window. If the slot math ever drifted from the real layout, these would
/// disagree and the click would miss.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousLinkInteractionTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousLinkInteractionTests(ITestOutputHelper o) { _out = o; }

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

    /// <summary>
    /// The centered page Border inside a realized slot container (the
    /// container itself is a full-width ContentPresenter).
    /// </summary>
    private static Avalonia.Controls.Border? PageBorderOf(Control container) =>
        container as Avalonia.Controls.Border
        ?? (container as Avalonia.Controls.Presenters.ContentPresenter)?.Child as Avalonia.Controls.Border;

    private sealed record ContinuousLinkSetup(
        MainWindowViewModel Vm,
        MainWindow Window,
        PdfViewerControl Viewer,
        int LinkPage,
        PdfLink TargetLink,
        Point PointInWindow);

    /// <summary>
    /// Shared setup: open the book, stay in continuous mode, navigate to a
    /// TOC page, wait for its slot container to realize, and translate the
    /// topmost internal link's center into window coordinates through the
    /// container's actual layout position.
    /// </summary>
    private async Task<ContinuousLinkSetup?> ArrangeTocLinkInContinuousModeAsync(string pragmaticBook)
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        // Continuous is the app default; assert rather than assume so this
        // test screams if the default ever changes out from under it, then
        // set it explicitly anyway for independence from the default.
        vm.ViewMode = PdfViewMode.Continuous;

        await vm.LoadDocumentAsync(pragmaticBook);

        // Wait for the background text-index build (shares parser state; a
        // concurrent GetLinks() would race the lexer — same wait the
        // single-page link tests use).
        var indexDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < indexDeadline &&
               (vm.OperationStatus?.StartsWith("Indexing") ?? false))
            await Task.Delay(200);
        await Task.Delay(500);

        vm.IsContinuousView.Should().BeTrue("this test exists to cover the continuous-mode link path");

        // Topmost internal link on an early TOC page — topmost so it is
        // guaranteed visible once the page top is scrolled to the viewport top.
        int linkPage = -1;
        PdfLink? targetLink = null;
        for (int p = 7; p <= 30 && p <= vm.TotalPages; p++)
        {
            var links = vm.PdfCoreDocument!.GetPage(p).GetLinks();
            var best = links
                .Where(l => l.Kind == PdfLinkKind.InternalDestination && l.DestinationPage != p)
                .OrderByDescending(l => l.Rect.Top)
                .FirstOrDefault();
            if (best != null) { linkPage = p; targetLink = best; break; }
        }
        targetLink.Should().NotBeNull("Pragmatic book has clickable TOC links");
        _out.WriteLine($"Using link on page {linkPage}: " +
            $"rect={targetLink!.Rect.Left:F0},{targetLink.Rect.Bottom:F0}-{targetLink.Rect.Right:F0},{targetLink.Rect.Top:F0} " +
            $"→ page {targetLink.DestinationPage}");

        vm.CurrentPageIndex = linkPage - 1;

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();

        // Poll: continuous ScrollViewer visible + the target page's container
        // realized with a real layout slot (the #653 lesson — poll the thing
        // that actually gates the point translation, with a deadline).
        var continuousScroll = viewer!.FindControl<ScrollViewer>("ContinuousScrollViewer");
        var items = viewer.FindControl<ItemsControl>("ContinuousItems");
        continuousScroll.Should().NotBeNull();
        items.Should().NotBeNull();

        Control? container = null;
        var layoutDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < layoutDeadline)
        {
            window.UpdateLayout();
            container = items!.ContainerFromIndex(linkPage - 1);
            if (continuousScroll!.IsVisible && container is { } c && c.Bounds.Height > 0)
                break;
            await Task.Delay(150);
        }
        continuousScroll!.IsVisible.Should().BeTrue(
            "ContinuousScrollViewer must be visible in continuous mode");
        container.Should().NotBeNull("the target page's slot container must realize");
        container!.Bounds.Height.Should().BeGreaterThan(0, "the slot container must be laid out");

        // The realized container (a ContentPresenter) spans the FULL items
        // width; the page itself is the centered Border inside it. Translate
        // page-local dips through the Border, not the container, or the
        // point lands in the letterbox margin left of the page.
        var pageBorder = PageBorderOf(container);
        pageBorder.Should().NotBeNull("the slot's DataTemplate Border must exist");

        // Link center: PDF content points → continuous dips within the page
        // (the production scale: PointsToDip * zoom, Y-flip and /Rotate via
        // PdfCoordinateMapper) → window through the container's real position.
        var page = vm.PdfCoreDocument!.GetPage(linkPage);
        double cx = (targetLink.Rect.Left + targetLink.Rect.Right) * 0.5;
        double cy = (targetLink.Rect.Bottom + targetLink.Rect.Top) * 0.5;
        var centerDips = PdfCoordinateMapper.ToContinuousDips(
            page,
            PdfPageRect.FromContentPoints(linkPage, new PdfRectangle(cx, cy, cx, cy)),
            PdfViewerControl.PointsToDip * viewer.ZoomLevel);

        var pointInWindow = pageBorder!.TranslatePoint(new Point(centerDips.X, centerDips.Y), window);
        pointInWindow.Should().NotBeNull(
            "the realized page border must translate to window coordinates (its ancestors are visible and laid out)");
        _out.WriteLine($"Link center page-dips=({centerDips.X:F1},{centerDips.Y:F1}) " +
            $"→ window=({pointInWindow!.Value.X:F1},{pointInWindow.Value.Y:F1}), zoom={viewer.ZoomLevel:F3}");

        // The topmost TOC link must actually be inside the window, or the
        // simulated pointer event lands on nothing.
        pointInWindow.Value.X.Should().BeInRange(0, window.Width);
        pointInWindow.Value.Y.Should().BeInRange(0, window.Height);

        return new ContinuousLinkSetup(vm, window, viewer, linkPage, targetLink, pointInWindow.Value);
    }

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task ClickOnTocLink_InContinuousMode_NavigatesToDestinationPage()
    {
        var pragmaticBook = FindPragmaticBook();
        Assert.SkipWhen(pragmaticBook == null, "Pragmatic book corpus fixture not available locally.");

        var setup = await ArrangeTocLinkInContinuousModeAsync(pragmaticBook!);
        setup.Should().NotBeNull();
        var (vm, window, viewer, _, targetLink, pointInWindow) = setup!;

        bool linkFired = false;
        int observedDestPage = -1;
        viewer.LinkClicked += (_, args) =>
        {
            linkFired = true;
            observedDestPage = args.PageNumber;
            _out.WriteLine($"LinkClicked fired with page={args.PageNumber}");
        };

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(pointInWindow, MouseButton.Left);
            window.MouseUp(pointInWindow, MouseButton.Left);
        });

        var clickDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < clickDeadline && !linkFired)
        {
            await Task.Delay(100);
            window.UpdateLayout();
        }

        linkFired.Should().BeTrue(
            "clicking a TOC link in CONTINUOUS mode (the app default) must fire LinkClicked — " +
            "this was the #667 gap: link hit-testing existed only for single-page mode");
        observedDestPage.Should().Be(targetLink.DestinationPage);
        vm.CurrentPageIndex.Should().Be(targetLink.DestinationPage - 1,
            $"VM should navigate to the link's destination page ({targetLink.DestinationPage})");
    }

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task HoverOverTocLink_InContinuousMode_ShowsHandCursorAndRaisesLinkHovered()
    {
        var pragmaticBook = FindPragmaticBook();
        Assert.SkipWhen(pragmaticBook == null, "Pragmatic book corpus fixture not available locally.");

        var setup = await ArrangeTocLinkInContinuousModeAsync(pragmaticBook!);
        setup.Should().NotBeNull();
        var (vm, window, viewer, linkPage, targetLink, pointInWindow) = setup!;

        string? lastHoverText = null;
        bool hoverRaised = false;
        viewer.LinkHovered += (_, args) =>
        {
            hoverRaised = true;
            lastHoverText = args.DisplayText;
            _out.WriteLine($"LinkHovered: '{args.DisplayText ?? "<null>"}'");
        };

        // Hover ON the link.
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(pointInWindow));
        var hoverDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < hoverDeadline && lastHoverText == null)
        {
            await Task.Delay(100);
            window.UpdateLayout();
        }

        hoverRaised.Should().BeTrue("moving the pointer over a link in continuous mode must raise LinkHovered");
        lastHoverText.Should().Be($"Go to page {targetLink.DestinationPage}",
            "the hover feedback must describe the internal destination");
        viewer.Cursor.Should().NotBeNull("hovering a link must set the hand cursor");
        viewer.Cursor.Should().NotBe(Cursor.Default,
            "hovering a link must switch away from the default cursor");

        // Hover OFF the link: a point on the same page, horizontally inside
        // the page but vertically clear of every link rect on it. Computed
        // from the actual link list so it cannot accidentally sit on another
        // TOC entry.
        var page = vm.PdfCoreDocument!.GetPage(linkPage);
        var links = page.GetLinks();
        double xPt = (targetLink.Rect.Left + targetLink.Rect.Right) * 0.5;
        double? offYPt = null;
        var box = page.CropBox.Normalize();
        for (double y = box.Top - 4; y > box.Bottom + 4; y -= 3)
        {
            double yy = y;
            bool clear = links.All(l =>
                xPt < l.Rect.Left - 2 || xPt > l.Rect.Right + 2 ||
                yy < l.Rect.Bottom - 4 || yy > l.Rect.Top + 4);
            if (!clear) continue;

            var probeDips = PdfCoordinateMapper.ToContinuousDips(
                page,
                PdfPageRect.FromContentPoints(linkPage, new PdfRectangle(xPt, yy, xPt, yy)),
                PdfViewerControl.PointsToDip * viewer.ZoomLevel);
            var items = viewer.FindControl<ItemsControl>("ContinuousItems");
            var container = items!.ContainerFromIndex(linkPage - 1)!;
            var probeWindow = PageBorderOf(container)!
                .TranslatePoint(new Point(probeDips.X, probeDips.Y), window);
            // The off-link probe must still land inside the viewer's part of
            // the window, or the move event never reaches the viewer and the
            // hover state (correctly) never clears.
            if (probeWindow is { } pw &&
                pw.Y > pointInWindow.Y && pw.Y < window.Height - 10 &&
                pw.X >= 0 && pw.X <= window.Width)
            {
                offYPt = yy;
                await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(pw));
                break;
            }
        }
        offYPt.Should().NotBeNull("a visible off-link point must exist on the TOC page");

        var clearDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < clearDeadline && lastHoverText != null)
        {
            await Task.Delay(100);
            window.UpdateLayout();
        }

        lastHoverText.Should().BeNull("moving off the link must clear the hover feedback");
        viewer.Cursor.Should().Be(Cursor.Default, "moving off the link must restore the default cursor");
    }
}
