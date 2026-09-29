using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1928: the viewer has no end-of-life hook, so leaving the visual tree is the last thing
/// the single-page view hears. Detach releases every cached page bitmap except the one the
/// Image is bound to, instead of leaving their pixel memory to the finalizer, and leaves the
/// cache usable for a reattached viewer.
/// </summary>
/// <remarks>
/// Runs on the shared headless session without a Skia backend (see
/// <see cref="ContinuousTileCacheDisposalTests"/>): disposal is observable because
/// Avalonia 12's <c>Bitmap.PixelSize</c> throws <see cref="ObjectDisposedException"/> once
/// the bitmap is released.
/// </remarks>
public class SinglePageDetachReleaseTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static WriteableBitmap NewPage() =>
        new(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static (PdfViewerControl Viewer, Window Window, WriteableBitmap[] Pages) ShownViewerWithCachedPages(int count)
    {
        var viewer = new PdfViewerControl();
        var window = new Window { Width = 320, Height = 240, Content = viewer };
        window.Show();
        Settle(window);

        var pages = Enumerable.Range(0, count).Select(_ => NewPage()).ToArray();
        for (int i = 0; i < pages.Length; i++)
            viewer.SinglePagePart.AddToCache(i + 1, 96, pages[i], new Size(64, 64));
        viewer.SinglePagePart.CacheDiagnostics().EntryCount.Should().Be(count,
            "fixture: every page fits the default single-page cache");
        return (viewer, window, pages);
    }

    [Fact]
    public async Task Detach_ReleasesEveryCachedPage_ExceptTheOneTheImageShows()
    {
        await OnUiThread(() =>
        {
            var (viewer, window, pages) = ShownViewerWithCachedPages(3);
            var shown = pages[1];
            viewer.SinglePagePart.PdfImage.Source = shown;

            window.Content = null;
            Settle(window);

            pages.Where(p => !ReferenceEquals(p, shown)).Should().OnlyContain(p => IsDisposed(p),
                "a detached viewer releases its cached pages now, not at finalization (#1928)");
            IsDisposed(shown).Should().BeFalse("the Image is still bound to the page it shows");
            viewer.SinglePagePart.CacheDiagnostics().EntryCount.Should().Be(1);
            viewer.SinglePagePart.CacheDiagnostics().IsDisposed.Should().BeFalse(
                "detach is not end of life: the viewer may be reattached");

            window.Close();
            return true;
        });
    }

    [Fact]
    public async Task Detach_WithNothingShown_ReleasesTheWholeCache()
    {
        await OnUiThread(() =>
        {
            // Continuous view hides the single-page Image and drops its source (#1473).
            var (viewer, window, pages) = ShownViewerWithCachedPages(2);
            viewer.SinglePagePart.PdfImage.Source = null;

            window.Close();
            Settle(window);

            pages.Should().OnlyContain(p => IsDisposed(p));
            viewer.SinglePagePart.CacheDiagnostics().EntryCount.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task ReattachedViewer_CachesPagesAgain()
    {
        await OnUiThread(() =>
        {
            var (viewer, window, _) = ShownViewerWithCachedPages(2);

            window.Content = null;
            Settle(window);
            window.Content = viewer;
            Settle(window);

            var next = NewPage();
            viewer.SinglePagePart.AddToCache(5, 96, next, new Size(64, 64));
            viewer.SinglePagePart.CacheDiagnostics().EntryCount.Should().Be(1);
            IsDisposed(next).Should().BeFalse();

            window.Close();
            return true;
        });
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
