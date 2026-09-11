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
