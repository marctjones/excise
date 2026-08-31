using Excise.Core.ColorSpaces;
using Excise.Rendering.Transparency;
using SkiaSharp;

namespace Excise.Rendering.Tests;

public sealed class DeviceCmykExecutionStateTests
{
    [Fact]
    public void InactiveState_DoesNotCreateBackdropOrTrackRgbPaint()
    {
        using var state = new DeviceCmykExecutionState(
            PreviewColorSpace(),
            rootBitmap: null,
            startsInTransparencyGroup: false);

        state.MarkBackdropDirtyFromRgbPaint();

        Assert.False(state.IsInTransparencyGroup);
        Assert.Null(state.Backdrop);
        Assert.False(state.BackdropDirtyFromRgbPaint);
    }

    [Fact]
    public void EnterChildGroup_RecordsReviewedLifetimeState()
    {
        using var bitmap = new SKBitmap(3, 2);
        using var state = new DeviceCmykExecutionState(
            PreviewColorSpace(),
            bitmap,
            startsInTransparencyGroup: true);
        var parentBackdrop = new DeviceCmykBackdrop(3, 2);
        parentBackdrop.Set(1, 1, new DeviceCmykColor(0.1, 0.2, 0.3, 0.4));

        state.EnterChildGroup(new DeviceCmykChildGroupRequest(
            IsIsolated: false,
            IsKnockout: true,
            ParentIsKnockout: true,
            ParentBackdrop: parentBackdrop,
            ParentLeft: 0,
            ParentTop: 0,
            Width: 3,
            Height: 2));

        Assert.True(state.IsInTransparencyGroup);
        Assert.True(state.IsInKnockoutGroup);
        Assert.False(state.IsInIsolatedGroup);
        Assert.True(state.PreserveZeroAlphaShape);
        Assert.NotSame(state.Backdrop, state.KnockoutInitialBackdrop);
        Assert.Equal(parentBackdrop.Get(1, 1), state.Backdrop!.Get(1, 1));
        Assert.Equal(state.Backdrop.Get(1, 1), state.KnockoutInitialBackdrop!.Get(1, 1));
        Assert.Same(state.KnockoutInitialBackdrop, state.SelectBackdropForChild());
    }

    [Fact]
    public void CompleteChildGroup_SynchronizesDirtyBackdropOnce()
    {
        using var bitmap = new SKBitmap(2, 2);
        using var state = new DeviceCmykExecutionState(
            PreviewColorSpace(),
            bitmap,
            startsInTransparencyGroup: true);
        var synchronizationCount = 0;
        state.MarkBackdropDirtyFromRgbPaint();

        var first = state.CompleteChildGroup(() => synchronizationCount++);
        var second = state.CompleteChildGroup(() => synchronizationCount++);

        Assert.True(first.IsAvailable);
        Assert.Same(state.Backdrop, first.Backdrop);
        Assert.True(second.IsAvailable);
        Assert.Equal(1, synchronizationCount);
        Assert.False(state.BackdropDirtyFromRgbPaint);
    }

    [Fact]
    public void CompleteChildGroup_FailedSynchronizationRetainsDirtyState()
    {
        using var bitmap = new SKBitmap(2, 2);
        using var state = new DeviceCmykExecutionState(
            PreviewColorSpace(),
            bitmap,
            startsInTransparencyGroup: true);
        state.MarkBackdropDirtyFromRgbPaint();

        Assert.Throws<InvalidOperationException>(() =>
            state.CompleteChildGroup(() => throw new InvalidOperationException("sync failed")));

        Assert.True(state.BackdropDirtyFromRgbPaint);
    }

    [Fact]
    public void BlendMask_IsGrowOnlyAndDisposedWithOwner()
    {
        using var bitmap = new SKBitmap(4, 4);
        var state = new DeviceCmykExecutionState(
            PreviewColorSpace(),
            bitmap,
            startsInTransparencyGroup: true);

        var first = state.GetBlendMask(2, 3);
        var reused = state.GetBlendMask(1, 1);
        var grown = state.GetBlendMask(4, 3);

        Assert.Same(first, reused);
        Assert.NotSame(first, grown);
        Assert.Equal(4, grown.Width);
        Assert.Equal(3, grown.Height);

        state.Dispose();
        Assert.Throws<ObjectDisposedException>(() => state.GetBlendMask(1, 1));
    }

    private static PdfColorSpace PreviewColorSpace()
        => PdfColorSpace.DeviceCMYK;
}
