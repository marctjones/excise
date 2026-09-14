using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Excise.Avalonia.Controls;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Waits for the single-page view to show a laid-out page before a test clicks,
/// hovers or translates a page point through the overlay.
/// </summary>
/// <remarks>
/// A visible, laid-out <c>PdfScrollViewer</c> is NOT enough. The overlay canvas
/// reports zero bounds and takes its origin from the ZoomHost, which is sized by
/// the page Image. Until the single-page render lands, the Image has no Source
/// and no size, so the ZoomHost is a zero-size point centred in the viewport and
/// every page point translates to the wrong window position.
/// <para>
/// Before #1473 a document opened in the continuous view (the app default)
/// rendered the hidden single page at open, so entering an editing mode showed an
/// already-sized page and a click straight after the toggle happened to land.
/// Now the page renders when single-page becomes visible. Measured on
/// TypewriterWorkflowTests' click test: Source null, ZoomHost (405,384,0,0), and
/// the page centre translated to (1026,990) in a 1280x900 window.
/// </para>
/// </remarks>
internal static class SinglePageViewerWaits
{
    internal static async Task WaitForSinglePageLaidOutAsync(
        Window window, PdfViewerControl viewer, TimeSpan? timeout = null)
    {
        var image = viewer.FindControl<Image>("PdfImage")
            ?? throw new InvalidOperationException("PdfViewerControl has no PdfImage");
        var zoomHost = viewer.FindControl<LayoutTransformControl>("ZoomHost")
            ?? throw new InvalidOperationException("PdfViewerControl has no ZoomHost");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        bool Ready() =>
            viewer.ViewMode == PdfViewMode.SinglePage
            && image.Source != null
            && !viewer.IsLoading
            && zoomHost.Bounds.Width > 0
            && zoomHost.Bounds.Height > 0;

        while (true)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (Ready())
            {
                window.UpdateLayout();
                return;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"single-page view not laid out: viewMode={viewer.ViewMode} source={image.Source != null} " +
                    $"loading={viewer.IsLoading} zoomHost={zoomHost.Bounds}");
            await Task.Delay(25);
        }
    }
}
