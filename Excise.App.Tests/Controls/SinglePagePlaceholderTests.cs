using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            var image = viewer.SinglePagePart.PdfImage!;
            var page = viewer.Document!.GetPage(1);
            var published = viewer.SinglePagePart.SinglePagePublishCount;

            viewer.ViewMode = PdfViewMode.SinglePage;

            // No pump: the placeholder is in place inside the view-mode change.
            var placeholder = viewer.SinglePagePart.SinglePagePlaceholderForTests;
            placeholder.Should().NotBeNull("a matching continuous composite exists for page 1");
            image.Source.Should().BeSameAs(placeholder);
            image.Source.Should().NotBeSameAs(composite, "the slot composite must never be bound directly");
            image.Width.Should().BeApproximately(page.VisualWidth * 120.0 / 72.0, 0.01);
            image.Height.Should().BeApproximately(page.VisualHeight * 120.0 / 72.0, 0.01);
            viewer.SinglePagePart.SinglePagePublishCount.Should().Be(published, "the placeholder is not the final render");
            viewer.IsLoading.Should().BeTrue();
            CountNonWhite(placeholder!).Should().BeGreaterThan(0, "the copy carries the composite's ink");
            var placeholderPixels = placeholder!.PixelSize;

            await PumpUntilAsync(window, () => viewer.SinglePagePart.SinglePagePublishCount > published && !viewer.IsLoading);
            image.Source.Should().NotBeSameAs(placeholder);
            // Both views now render at device resolution, 96 × zoom × dpr (#1480
            // continuous, #1487 single-page), so at this test's zoom 1 × dpr 1 the
            // sharp render replaces the placeholder pixel for pixel instead of
            // 1.25× denser. Exact only where 96 × zoom × dpr is an integer: the
            // two paths ceil different products, so elsewhere they may differ by 1 px.
            ((Bitmap)image.Source!).PixelSize.Should().Be(placeholderPixels,
                "at zoom 1 × dpr 1 the single-page render and the continuous composite share one device resolution");
            viewer.SinglePagePart.SinglePagePlaceholderForTests.Should().BeNull();

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
            var image = viewer.SinglePagePart.PdfImage!;
            var page = viewer.Document!.GetPage(1);
            var sizedOnly = viewer.SinglePagePart.SinglePagePlaceholderSizedOnlyCount;

            // The zoom changes and the switch follows before any recomposite, so
            // the slot still holds a composite built for the old zoom.
            viewer.ZoomLevel = 2.0;
            viewer.ViewMode = PdfViewMode.SinglePage;

            viewer.SinglePagePart.SinglePagePlaceholderSizedOnlyCount.Should().Be(sizedOnly + 1);
            viewer.SinglePagePart.SinglePagePlaceholderForTests.Should().BeNull("a composite at another zoom must not be shown");
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

    /// <summary>
    /// #1842 Phase B, for #1926: the preview source can write the placeholder's pixels into
    /// memory the caller owns instead of a new WriteableBitmap. They must be the bitmap
    /// copy's pixels exactly, at the caller's stride, in the top-left of a larger
    /// destination, and nothing may be written when the destination is too small.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task CompositeCopyIntoCallerMemory_MatchesTheBitmapCopy_PixelForPixel()
    {
        var (window, viewer, items) = ShowContinuousBeforeOpen(pageCount: 3);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var page = viewer.Document!.GetPage(1);
            IPagePreviewSource preview = viewer.ContinuousPart;

            using var expected = preview.TryCopyCompositeForPage(1, page.VisualWidth, page.VisualHeight);
            expected.Should().NotBeNull("a matching continuous composite exists for page 1");
            CountNonWhite(expected!).Should().BeGreaterThan(0, "a blank copy would make the comparison vacuous");
            var size = preview.CompositeCopySize(1, page.VisualWidth, page.VisualHeight);
            size.Should().Be(expected!.PixelSize);
            int width = size!.Value.Width, height = size.Value.Height;

            // A padded stride and a destination larger both ways than the copy.
            const byte Untouched = 0x5A;
            var destinationSize = new PixelSize(width + 16, height + 3);
            int rowBytes = destinationSize.Width * 4;
            var buffer = new byte[rowBytes * destinationSize.Height];
            Array.Fill(buffer, Untouched);
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                preview.TryCopyCompositeInto(1, page.VisualWidth, page.VisualHeight,
                    pin.AddrOfPinnedObject(), rowBytes, destinationSize).Should().BeTrue();
            }
            finally
            {
                pin.Free();
            }

            using (var fb = expected.Lock())
            {
                var expectedRow = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(fb.Address + y * fb.RowBytes, expectedRow, 0, expectedRow.Length);
                    buffer.AsSpan(y * rowBytes, expectedRow.Length).SequenceEqual(expectedRow)
                        .Should().BeTrue($"row {y} must hold the bitmap copy's pixels");
                    buffer.AsSpan(y * rowBytes + expectedRow.Length, rowBytes - expectedRow.Length).ToArray()
                        .Should().OnlyContain(b => b == 0xFF, $"the rest of row {y} is written white, as documented");
                }
            }
            buffer.AsSpan(height * rowBytes).ToArray().Should().OnlyContain(b => b == Untouched,
                "rows below the copy belong to the caller");

            // Too small by one pixel: refused, and not one byte written.
            var small = new byte[width * 4 * height];
            Array.Fill(small, Untouched);
            var smallPin = GCHandle.Alloc(small, GCHandleType.Pinned);
            try
            {
                preview.TryCopyCompositeInto(1, page.VisualWidth, page.VisualHeight,
                    smallPin.AddrOfPinnedObject(), width * 4, new PixelSize(width - 1, height)).Should().BeFalse();
                preview.TryCopyCompositeInto(99, page.VisualWidth, page.VisualHeight,
                    smallPin.AddrOfPinnedObject(), width * 4, new PixelSize(width, height)).Should().BeFalse("there is no page 99");
            }
            finally
            {
                smallPin.Free();
            }
            small.Should().OnlyContain(b => b == Untouched);
            preview.CompositeCopySize(99, page.VisualWidth, page.VisualHeight).Should().BeNull();
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
            var placeholder = viewer.SinglePagePart.SinglePagePlaceholderForTests;
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
            viewer.SinglePagePart.PdfImage!.Source.Should().BeNull();
            viewer.SinglePagePart.SinglePagePlaceholderForTests.Should().BeNull();
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
        var items = viewer.ContinuousPart.ContinuousItems!;
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
