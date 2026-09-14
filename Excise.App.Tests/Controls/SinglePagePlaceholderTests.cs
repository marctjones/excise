using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Xunit;

namespace Excise.App.Tests.Controls;

/// <summary>
/// Edit-mode switch study, option 3 (#1473): on the switch to single-page the
/// viewer sizes the page from its geometry and shows a viewer-owned copy of the
/// continuous composite until the sharp render lands, then releases the copy
/// only after the Image binding has moved off it (#1466/#1467).
/// </summary>
[Collection("AvaloniaTests")]
public class SinglePagePlaceholderTests
{
    [FixedAvaloniaFact]
    public async Task SwitchFromContinuous_ShowsASizedCompositeCopyAtOnce_ThenReleasesItWhenTheRenderLands()
    {
        var (window, viewer, items) = ShowContinuousBeforeOpen(pageCount: 3);
        try
        {
            var composite = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1);
            var image = viewer.FindControl<Image>("PdfImage")!;
            var page = viewer.Document!.GetPage(1);
            var published = viewer.SinglePagePublishCount;

            viewer.ViewMode = PdfViewMode.SinglePage;

            // No pump: the placeholder is in place inside the view-mode change.
            var placeholder = viewer.SinglePagePlaceholderForTests;
            placeholder.Should().NotBeNull("a matching continuous composite exists for page 1");
            image.Source.Should().BeSameAs(placeholder);
            image.Source.Should().NotBeSameAs(composite, "the slot composite must never be bound directly");
            image.Width.Should().BeApproximately(page.VisualWidth * 120.0 / 72.0, 0.01);
            image.Height.Should().BeApproximately(page.VisualHeight * 120.0 / 72.0, 0.01);
            viewer.SinglePagePublishCount.Should().Be(published, "the placeholder is not the final render");
            viewer.IsLoading.Should().BeTrue();
            CountNonWhite(placeholder!).Should().BeGreaterThan(0, "the copy carries the composite's ink");

            await PumpUntilAsync(window, () => viewer.SinglePagePublishCount > published && !viewer.IsLoading);
            image.Source.Should().NotBeSameAs(placeholder);
            viewer.SinglePagePlaceholderForTests.Should().BeNull();

            for (var i = 0; i < 5; i++)
            {
                RenderFrames(window, viewer);
                Dispatcher.UIThread.RunJobs();
            }
            IsDisposed(placeholder!).Should().BeTrue("the copy is released once the binding has moved off it");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task CompositeAtAnotherZoom_OnlySizesThePage()
    {
        var (window, viewer, items) = ShowContinuousBeforeOpen(pageCount: 3);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var image = viewer.FindControl<Image>("PdfImage")!;
            var page = viewer.Document!.GetPage(1);
            var sizedOnly = viewer.SinglePagePlaceholderSizedOnlyCount;

            // The zoom changes and the switch follows before any recomposite, so
            // the slot still holds a composite built for the old zoom.
            viewer.ZoomLevel = 2.0;
            viewer.ViewMode = PdfViewMode.SinglePage;

            viewer.SinglePagePlaceholderSizedOnlyCount.Should().Be(sizedOnly + 1);
            viewer.SinglePagePlaceholderForTests.Should().BeNull("a composite at another zoom must not be shown");
            image.Source.Should().BeNull();
            image.Width.Should().BeApproximately(page.VisualWidth * 120.0 / 72.0, 0.01,
                "the page is still sized so the overlay has its real geometry");

            await PumpUntilAsync(window, () => image.Source != null && !viewer.IsLoading);
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task DocumentClearedWhilePlaceholderIsBound_ReleasesIt_WithoutObjectDisposedDuringFrames()
    {
        var (window, viewer, items) = ShowContinuousBeforeOpen(pageCount: 3);
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => errors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            viewer.ViewMode = PdfViewMode.SinglePage;
            var placeholder = viewer.SinglePagePlaceholderForTests;
            placeholder.Should().NotBeNull();
            RenderFrames(window, viewer);

            viewer.Document = null;
            for (var i = 0; i < 10; i++)
            {
                RenderFrames(window, viewer);
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            errors.Should().BeEmpty();
            viewer.FindControl<Image>("PdfImage")!.Source.Should().BeNull();
            viewer.SinglePagePlaceholderForTests.Should().BeNull();
            IsDisposed(placeholder!).Should().BeTrue();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
        }
    }

    private static (Window Window, PdfViewerControl Viewer, ItemsControl Items) ShowContinuousBeforeOpen(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-placeholder-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);

        var viewer = new PdfViewerControl { ViewMode = PdfViewMode.Continuous };
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        viewer.Document = Excise.Core.Document.PdfDocument.Open(bytes);
        var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
        return (window, viewer, items);
    }

    private static int CountNonWhite(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var row = new byte[fb.RowBytes];
        var count = 0;
        for (var y = 0; y < fb.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = x * 4;
                if (row[p] < 200 || row[p + 1] < 200 || row[p + 2] < 200) count++;
            }
        }
        return count;
    }

    private static void RenderFrames(Window window, PdfViewerControl viewer)
    {
        window.UpdateLayout();
        using (window.CaptureRenderedFrame()) { }
        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var target = new RenderTargetBitmap(new PixelSize(w, h));
        target.Render(viewer);
    }

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

    private static async Task PumpUntilAsync(Window window, Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("condition not met within 30 s");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }
}
