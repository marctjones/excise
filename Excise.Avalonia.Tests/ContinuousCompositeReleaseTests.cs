using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1466: a continuous-view page composite (<see cref="PdfPageSlot.Bitmap"/>) that
/// is replaced or cleared is released deterministically — but only after the
/// dispatcher has moved on, because unlike a tile it is bound to an Image, and a
/// disposed Avalonia 12 bitmap throws on the Image's next measure or render.
/// </summary>
/// <remarks>
/// The end-to-end half — a real Image bound to the slot, frames rendered while
/// composites are replaced, no ObjectDisposedException — runs on Skia in
/// Excise.App.Tests (ContinuousCompositeReleaseRenderTests).
/// </remarks>
public class ContinuousCompositeReleaseTests
{
    private static readonly PdfViewerControl.ContinuousTileKey KeyA = new(1, 120, 816, 1056, 0, 0);
    private static readonly PdfViewerControl.ContinuousTileKey KeyB = new(1, 120, 816, 1056, 0, 1);

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static WriteableBitmap NewComposite() =>
        new(new PixelSize(512, 512), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

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

    private static void Publish(PdfPageSlot slot, WriteableBitmap bitmap, PdfViewerControl.ContinuousTileKey key) =>
        slot.SetComposite(bitmap, key, 0, 0, 512, 512);

    [Fact]
    public async Task ReplacingAComposite_ReleasesTheOldOne_OnlyAfterTheDispatcherRuns()
    {
        await OnUiThread(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var slot = new PdfPageSlot(1, 612, 792, 1.0);
            var first = NewComposite();
            var second = NewComposite();

            Publish(slot, first, KeyA);
            Publish(slot, second, KeyB);

            slot.Bitmap.Should().BeSameAs(second);
            IsDisposed(first).Should().BeFalse(
                "the old composite may still be read in this dispatcher turn; it must not be disposed inside SetComposite");

            Dispatcher.UIThread.RunJobs();

            IsDisposed(first).Should().BeTrue("a composite no slot shows any more is released, not left to the finalizer");
            IsDisposed(second).Should().BeFalse("the composite being shown is never released");
            return true;
        });
    }

    [Fact]
    public async Task ClearingAComposite_ReleasesIt_OnlyAfterTheDispatcherRuns()
    {
        await OnUiThread(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var slot = new PdfPageSlot(1, 612, 792, 1.0);
            var shown = NewComposite();
            Publish(slot, shown, KeyA);

            slot.ClearComposite();

            slot.Bitmap.Should().BeNull();
            IsDisposed(shown).Should().BeFalse();
            Dispatcher.UIThread.RunJobs();
            IsDisposed(shown).Should().BeTrue(
                "a cleared composite (recycled container, document change) is released once the binding has moved");
            return true;
        });
    }

    [Fact]
    public async Task RepublishingTheShownComposite_DoesNotReleaseIt()
    {
        await OnUiThread(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var slot = new PdfPageSlot(1, 612, 792, 1.0);
            var shown = NewComposite();

            Publish(slot, shown, KeyA);
            Publish(slot, shown, KeyB);
            Dispatcher.UIThread.RunJobs();

            IsDisposed(shown).Should().BeFalse("re-publishing the current instance releases nothing");
            return true;
        });
    }

    [Fact]
    public async Task TheRelease_RunsAfterLayoutAndRenderWorkAlreadyQueued()
    {
        await OnUiThread(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var slot = new PdfPageSlot(1, 612, 792, 1.0);
            var old = NewComposite();
            Publish(slot, old, KeyA);

            // Replace, then queue work at the priorities the layout and render
            // passes run at. Both must still see a usable bitmap.
            Publish(slot, NewComposite(), KeyB);
            bool readAtRender = false, readAtLoaded = false;
            Dispatcher.UIThread.Post(() => readAtRender = !IsDisposed(old), DispatcherPriority.Render);
            Dispatcher.UIThread.Post(() => readAtLoaded = !IsDisposed(old), DispatcherPriority.Loaded);

            Dispatcher.UIThread.RunJobs();

            readAtRender.Should().BeTrue("the release is queued below Render priority");
            readAtLoaded.Should().BeTrue("the release is queued below Loaded priority");
            IsDisposed(old).Should().BeTrue();
            return true;
        });
    }
}
