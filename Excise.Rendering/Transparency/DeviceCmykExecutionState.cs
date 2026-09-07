using Excise.Core.ColorSpaces;
using SkiaSharp;

namespace Excise.Rendering.Transparency;

/// <summary>
/// Owns the mutable lifetime of the Skia DeviceCMYK preview path. Pixel
/// compositing remains in <c>RenderContext</c>; this type centralizes the state
/// transitions and native scratch resource that those reviewed operations use.
/// </summary>
internal sealed class DeviceCmykExecutionState : IDisposable
{
    private SKBitmap? _blendMask;
    private bool _disposed;

    public DeviceCmykExecutionState(
        PdfColorSpace previewColorSpace,
        SKBitmap? rootBitmap,
        bool startsInTransparencyGroup)
    {
        PreviewColorSpace = previewColorSpace;
        TransparencyGroupDepth = startsInTransparencyGroup ? 1 : 0;
        Backdrop = startsInTransparencyGroup && rootBitmap != null
            ? new DeviceCmykBackdrop(rootBitmap.Width, rootBitmap.Height)
            : null;
    }

    public int TransparencyGroupDepth { get; private set; }
    public int KnockoutGroupDepth { get; private set; }
    public int IsolatedGroupDepth { get; private set; }
    public bool PreserveZeroAlphaShape { get; private set; }
    public bool BackdropDirtyFromRgbPaint { get; private set; }
    public DeviceCmykBackdrop? Backdrop { get; }
    public DeviceCmykBackdrop? KnockoutInitialBackdrop { get; private set; }
    public PdfColorSpace PreviewColorSpace { get; }

    public bool IsInTransparencyGroup =>
        TransparencyGroupDepth > 0 && Backdrop != null;

    public bool IsInKnockoutGroup => KnockoutGroupDepth > 0;

    public bool IsInIsolatedGroup => IsolatedGroupDepth > 0;

    public void EnterChildGroup(DeviceCmykChildGroupRequest request)
    {
        ThrowIfDisposed();
        PreserveZeroAlphaShape = request.ParentIsKnockout && !request.IsIsolated;

        if (!request.IsIsolated && Backdrop != null && request.ParentBackdrop != null)
        {
            SeedBackdrop(
                request.ParentBackdrop,
                request.ParentLeft,
                request.ParentTop,
                request.Width,
                request.Height);
        }

        if (request.IsKnockout)
        {
            KnockoutGroupDepth++;
            KnockoutInitialBackdrop = Backdrop?.Clone();
        }

        if (request.IsIsolated)
            IsolatedGroupDepth++;
    }

    public DeviceCmykBackdrop? SelectBackdropForChild()
        => IsInKnockoutGroup && KnockoutInitialBackdrop != null
            ? KnockoutInitialBackdrop
            : Backdrop;

    public void MarkBackdropDirtyFromRgbPaint()
    {
        if (IsInTransparencyGroup)
            BackdropDirtyFromRgbPaint = true;
    }

    public DeviceCmykChildGroupResult CompleteChildGroup(Action synchronizeBackdrop)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(synchronizeBackdrop);

        if (Backdrop == null)
            return DeviceCmykChildGroupResult.Unavailable;

        if (BackdropDirtyFromRgbPaint)
        {
            synchronizeBackdrop();
            BackdropDirtyFromRgbPaint = false;
        }

        return new DeviceCmykChildGroupResult(Backdrop);
    }

    public SKBitmap GetBlendMask(int width, int height)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var existing = _blendMask;
        if (existing != null && existing.Width >= width && existing.Height >= height)
            return existing;

        var newWidth = Math.Max(width, existing?.Width ?? 0);
        var newHeight = Math.Max(height, existing?.Height ?? 0);
        existing?.Dispose();
        _blendMask = new SKBitmap(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        return _blendMask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _blendMask?.Dispose();
        _blendMask = null;
        _disposed = true;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private void SeedBackdrop(
        DeviceCmykBackdrop sourceBackdrop,
        int parentLeft,
        int parentTop,
        int width,
        int height)
    {
        // Row-wise byte copy — see DeviceCmykBackdrop.CopyRegionFrom for why
        // this is byte-identical to the per-pixel Get/Set loop it replaces.
        Backdrop!.CopyRegionFrom(sourceBackdrop, parentLeft, parentTop, width, height);
    }
}

internal readonly record struct DeviceCmykChildGroupRequest(
    bool IsIsolated,
    bool IsKnockout,
    bool ParentIsKnockout,
    DeviceCmykBackdrop? ParentBackdrop,
    int ParentLeft,
    int ParentTop,
    int Width,
    int Height);

internal readonly record struct DeviceCmykChildGroupResult(DeviceCmykBackdrop? Backdrop)
{
    public static DeviceCmykChildGroupResult Unavailable => new(null);

    public bool IsAvailable => Backdrop != null;
}
