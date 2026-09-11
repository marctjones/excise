using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1466: replaced and cleared continuous-view composites are released, and
/// releasing them never lets an Image read a disposed bitmap. Avalonia 12's
/// <c>Bitmap.Dispose</c> releases its <c>IRef</c>, after which any read through
/// the wrapper throws <see cref="ObjectDisposedException"/>; the composite is
/// bound to an Image, so its release is deferred until the binding has moved.
/// This drives real recomposites on Skia and renders frames through both paths
/// that read a bitmap: the compositor (<c>CaptureRenderedFrame</c>) and an
/// immediate render of the control (<see cref="RenderTargetBitmap"/>).
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousCompositeReleaseRenderTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousCompositeReleaseRenderTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public async Task RecomposingRepeatedlyWhileRenderingFrames_ReleasesEveryReplacedComposite_WithoutObjectDisposed()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        var seen = new HashSet<WriteableBitmap>(ReferenceEqualityComparer.Instance);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            foreach (var s in slots)
            {
                if (s.Bitmap != null) seen.Add(s.Bitmap);
                var observed = s;
                observed.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PdfPageSlot.Bitmap) && observed.Bitmap != null)
                        seen.Add(observed.Bitmap);
                };
            }

            // 1. The binding moves synchronously, which is what makes a deferred
            //    release safe.
            var slot = slots[0];
            var image = ImageFor(items, slot);
            image.Source.Should().BeSameAs(slot.Bitmap, "fixture: page 1's Image shows the slot's composite");

            var shown = slot.Bitmap!;
            var replacement = new WriteableBitmap(shown.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            slot.SetComposite(replacement, slot.CompositeKey,
                slot.TileDisplayX, slot.TileDisplayY, slot.TileDisplayWidth, slot.TileDisplayHeight);

            image.Source.Should().BeSameAs(replacement,
                "{Binding Bitmap} must move the Image off the old composite before SetComposite returns");
            IsDisposed(shown).Should().BeFalse("the old composite is not disposed inside SetComposite");
            RenderFrames(window, viewer);
            Dispatcher.UIThread.RunJobs();
            IsDisposed(shown).Should().BeTrue("once the dispatcher has run, the replaced composite is released");
            RenderFrames(window, viewer);

            // 2. Real recomposites. Each zoom step changes the composite key, so
            //    every pass publishes a new band bitmap and releases the previous.
            const int steps = 12;
            for (int i = 0; i < steps; i++)
            {
                var before = slot.Bitmap;
                viewer.ZoomLevel = i % 2 == 0 ? 1.1 : 1.0;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                    window, viewer, items, pageNumber: 1, notThis: before);
                RenderFrames(window, viewer);
            }

            Dispatcher.UIThread.RunJobs();
            RenderFrames(window, viewer);

            var live = new HashSet<WriteableBitmap>(
                slots.Where(s => s.Bitmap != null).Select(s => s.Bitmap!), ReferenceEqualityComparer.Instance);
            _out.WriteLine($"composites observed={seen.Count} live={live.Count} " +
                           $"residentBytes={viewer.ContinuousCompositeResidentBytes()}");

            seen.Count.Should().BeGreaterThan(steps, "fixture: every zoom step must have published a composite");
            foreach (var bitmap in seen)
            {
                IsDisposed(bitmap).Should().Be(!live.Contains(bitmap),
                    "a composite that no slot shows is released, and a shown one never is");
            }

            long liveBytes = live.Sum(b => PdfViewerControl.ContinuousTileByteSize(b.PixelSize.Width, b.PixelSize.Height));
            viewer.ContinuousCompositeResidentBytes().Should().Be(liveBytes,
                "composite accounting counts exactly the composites the slots hold");
            liveBytes.Should().BeLessThanOrEqualTo(PdfViewerControl.ContinuousCompositeByteBound);
            dispatcherErrors.Should().BeEmpty("no dispatcher job may throw while composites are released");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    /// <summary>
    /// The #1466 bound is checked in production after every published composite:
    /// composites above it raise the over-bound warning, and composites inside it
    /// do not. The override shrinks the bound so ordinary composites exceed it.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task CompositesAboveTheByteBound_RaiseTheOverBoundWarning_AndCompositesInsideItDoNot()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 1);
        try
        {
            var original = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1);
            viewer.ContinuousCompositeResidentBytes().Should().BeGreaterThan(1,
                "fixture: the page must hold a composite for a 1-byte bound to be exceeded");
            viewer.ContinuousCompositeOverBoundCount.Should().Be(0,
                "a one-page document at the default zoom is far inside the documented bound");

            viewer.ContinuousCompositeByteBoundOverride = 1;
            viewer.ZoomLevel = 1.1;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1, notThis: original);

            _out.WriteLine($"overBound={viewer.ContinuousCompositeOverBoundCount} " +
                           $"residentBytes={viewer.ContinuousCompositeResidentBytes()}");
            viewer.ContinuousCompositeOverBoundCount.Should().BeGreaterThan(0,
                "a composite published while the slots' composites exceed the bound must raise the warning");
        }
        finally
        {
            viewer.ContinuousCompositeByteBoundOverride = null;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    /// <summary>
    /// #1466, found live: paging through a 126-page document with Next Page kept
    /// every composite ever published (37 pages, 940 MB at page 126). Composites
    /// were released only from <c>ContainerClearing</c>, and Avalonia 12 raises
    /// that event after <c>ClearContainerForItemOverride</c> has cleared the
    /// presenter's Content, which clears its DataContext too, so the handler
    /// never saw a <see cref="PdfPageSlot"/>. A page that is no longer realized
    /// must not keep a composite, and the ones it dropped must be released.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task PagingThroughTheDocument_KeepsCompositesOnlyForRealizedPages_AndReleasesTheRest()
    {
        const int pageCount = 30;
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount);
        int clearings = 0;
        EventHandler<ContainerClearingEventArgs> onClearing = (_, _) => clearings++;
        items.ContainerClearing += onClearing;
        var seen = new HashSet<WriteableBitmap>(ReferenceEqualityComparer.Instance);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            foreach (var s in slots)
            {
                if (s.Bitmap != null) seen.Add(s.Bitmap);
                var observed = s;
                observed.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PdfPageSlot.Bitmap) && observed.Bitmap != null)
                        seen.Add(observed.Bitmap);
                };
            }

            for (int page = 2; page <= pageCount; page++)
            {
                viewer.CurrentPage = page;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, page);
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var realized = new HashSet<PdfPageSlot>(
                items.GetRealizedContainers().Select(c => c.DataContext).OfType<PdfPageSlot>(),
                ReferenceEqualityComparer.Instance);
            var holding = slots.Where(s => s.Bitmap != null).ToList();
            _out.WriteLine($"clearings={clearings} realized=[{string.Join(",", realized.Select(s => s.PageNumber).Order())}] " +
                           $"holding=[{string.Join(",", holding.Select(s => s.PageNumber))}] composites observed={seen.Count} " +
                           $"residentBytes={viewer.ContinuousCompositeResidentBytes()}");

            clearings.Should().BeGreaterThan(0, "fixture: paging 30 pages must recycle containers");
            seen.Count.Should().BeGreaterThanOrEqualTo(pageCount, "fixture: every visited page must have published a composite");
            holding.Where(s => !realized.Contains(s)).Select(s => s.PageNumber).Should().BeEmpty(
                "a page that is no longer realized must not keep its composite");

            var live = new HashSet<WriteableBitmap>(holding.Select(s => s.Bitmap!), ReferenceEqualityComparer.Instance);
            foreach (var bitmap in seen)
            {
                IsDisposed(bitmap).Should().Be(!live.Contains(bitmap),
                    "a composite that no slot shows is released, and a shown one never is");
            }
            viewer.ContinuousCompositeResidentBytes().Should().Be(
                live.Sum(b => PdfViewerControl.ContinuousTileByteSize(b.PixelSize.Width, b.PixelSize.Height)),
                "composite accounting counts exactly the composites the slots hold");
        }
        finally
        {
            items.ContainerClearing -= onClearing;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    private static Image ImageFor(ItemsControl items, PdfPageSlot slot)
    {
        var container = items.ContainerFromItem(slot)
            ?? throw new InvalidOperationException($"page {slot.PageNumber} is not realized");
        return container.GetVisualDescendants().OfType<Image>().First();
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
}
