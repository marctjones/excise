using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The single-page half of the mode-switch reading-position carry (#693): the fraction of
/// the page above the viewport top, and the bounded wait that applies a carried fraction
/// once a freshly rendered page gives the scroller a real extent.
/// </summary>
internal sealed partial class SinglePageView
{
    // Intra-page position carried across a view-mode switch (#693).
    private double _pendingSingleFraction = -1;
    private IDisposable? _pendingSingleFractionSub;

    /// <summary>Fraction of the page above the viewport top in single-page view.</summary>
    private double SingleIntraPageFraction()
    {
        if (PdfScrollViewer == null) return 0;
        var extent = PdfScrollViewer.Extent.Height;
        if (extent <= 1) return 0;
        return Math.Clamp(PdfScrollViewer.Offset.Y / extent, 0, 0.99);
    }

    /// <summary>
    /// The single-page ScrollViewer clamps Offset to a zero extent until the
    /// freshly-rendered page has laid out — and layout may be arbitrarily far
    /// away (headless hosts only lay out on explicit pumps), so
    /// dispatcher-post retries drain uselessly before it. Instead, wait on
    /// the Extent property itself and apply the carried fraction the moment
    /// the content gets a real size.
    /// </summary>
    private bool _applyingSingleFraction;

    private void ApplyPendingSingleFraction()
    {
        // Same re-entrancy guard as ApplyPendingZoomAnchor: GetObservable
        // emits the current value synchronously on subscribe, which would
        // re-enter here before the subscription field is assigned.
        if (_applyingSingleFraction) return;
        _applyingSingleFraction = true;
        try
        {
            ApplyPendingSingleFractionCore();
        }
        finally
        {
            _applyingSingleFraction = false;
        }
    }

    private void ApplyPendingSingleFractionCore()
    {
        if (_pendingSingleFraction < 0 || PdfScrollViewer == null)
        {
            _pendingSingleFractionSub?.Dispose();
            _pendingSingleFractionSub = null;
            return;
        }
        var extent = PdfScrollViewer.Extent.Height;
        if (extent <= 1)
        {
            _pendingSingleFractionSub ??= PdfScrollViewer
                .GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new AnonymousObserver<Size>(_ => ApplyPendingSingleFraction()));
            return;
        }
        _pendingSingleFractionSub?.Dispose();
        _pendingSingleFractionSub = null;
        PdfScrollViewer.Offset = new Vector(PdfScrollViewer.Offset.X, _pendingSingleFraction * extent);
        _pendingSingleFraction = -1;
    }
}
