using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using global::Avalonia.Reactive;
using global::Avalonia.Threading;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Avalonia.Controls;


/// <summary>
/// One rendered grid cell of a continuous-view page (#848), in page-local DIPs
/// (the Border's own coordinate space). Immutable: a tile is created only once
/// its bitmap is ready, and its grid position never changes — the whole point of
/// the content-addressed grid is that a cell painted at its position is always
/// correct for that position. Bound one-per-Image by the DataTemplate.
/// </summary>
/// <summary>
/// One page in the continuous (reading) view. Observable so the data-template's
/// Border size and single displayed <see cref="Bitmap"/> update as zoom changes
/// and the page renders. The grid of tiles is rendered and cached per cell
/// (bounded memory, never-stale coverage — #848), but they are COMPOSITED into
/// this one bitmap for display, so there is exactly one Image per page and hence
/// no inter-tile seams.
/// </summary>
internal sealed class PdfPageSlot : INotifyPropertyChanged
{
    private double _displayWidth;
    private double _displayHeight;
    private double _topDip;
    private double _tileDisplayX;
    private double _tileDisplayY;
    private double _tileDisplayWidth;
    private double _tileDisplayHeight;
    private WriteableBitmap? _bitmap;

    internal PdfPageSlot(int pageNumber, double widthPt, double heightPt, double zoom)
    {
        PageNumber = pageNumber;
        WidthPt = widthPt;
        HeightPt = heightPt;
        ApplyZoom(zoom);
    }

    public int PageNumber { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    /// <summary>The page's /Rotate when laid out; a 180° turn keeps the size (#1876).</summary>
    internal int Rotation { get; init; }

    /// <summary>
    /// Text-selection highlight rectangles for this page, in page-local DIPs
    /// (the Border's own coordinate space), bound by the continuous-view
    /// DataTemplate to a Canvas overlay (#815). Populated by the continuous
    /// selection gesture; empty when nothing on this page is selected.
    /// </summary>
    internal System.Collections.ObjectModel.ObservableCollection<PdfSelectionHighlight> SelectionRects { get; } = new();

    /// <summary>
    /// The fillable AcroForm inputs for this page, positioned in page-local DIPs
    /// (#1807). Filled only while the slot is realized and emptied when it
    /// scrolls away, so a 400-page form never holds 400 pages of text boxes.
    /// </summary>
    internal System.Collections.ObjectModel.ObservableCollection<global::Avalonia.Controls.Control> FormFieldControls { get; } = new();

    /// <summary>Zoom the current <see cref="FormFieldControls"/> were laid out for; NaN = none built.</summary>
    internal double FormFieldsBuiltForZoom { get; set; } = double.NaN;

    /// <summary>Cheap identity of the field set the controls were built from, to notice a changed page.</summary>
    internal int FormFieldsSignature { get; set; }

    internal double TopDip { get => _topDip; private set => Set(ref _topDip, value); }
    public double DisplayWidth { get => _displayWidth; private set => Set(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; private set => Set(ref _displayHeight, value); }

    /// <summary>The composited band bitmap and where it sits in page-local DIPs.</summary>
    public WriteableBitmap? Bitmap { get => _bitmap; private set => Set(ref _bitmap, value); }
    public double TileDisplayX { get => _tileDisplayX; private set => Set(ref _tileDisplayX, value); }
    public double TileDisplayY { get => _tileDisplayY; private set => Set(ref _tileDisplayY, value); }
    public double TileDisplayWidth { get => _tileDisplayWidth; private set => Set(ref _tileDisplayWidth, value); }
    public double TileDisplayHeight { get => _tileDisplayHeight; private set => Set(ref _tileDisplayHeight, value); }

    /// <summary>
    /// Full tile key of the band the current <see cref="Bitmap"/> was composited
    /// for (the top-left cell's key stands in for the band + zoom). Lets the
    /// recompose skip rebuilding an identical band.
    /// </summary>
    internal PdfViewerControl.ContinuousTileKey CompositeKey { get; private set; }

    internal void ApplyZoom(double zoom)
    {
        DisplayWidth = WidthPt * PdfViewerControl.PointsToDip * zoom;
        DisplayHeight = HeightPt * PdfViewerControl.PointsToDip * zoom;
    }

    internal void ApplyLayout(double topDip, double zoom)
    {
        TopDip = topDip;
        ApplyZoom(zoom);
    }

    /// <summary>
    /// Publish a freshly composited band bitmap and its page-local DIP placement.
    /// The slot takes ownership of <paramref name="bitmap"/>; the composite it
    /// replaces is released once the binding has moved off it (#1466).
    /// </summary>
    internal void SetComposite(WriteableBitmap bitmap, PdfViewerControl.ContinuousTileKey compositeKey,
        double xDip, double yDip, double widthDip, double heightDip)
    {
        var previous = _bitmap;
        CompositeKey = compositeKey;
        TileDisplayX = xDip;
        TileDisplayY = yDip;
        TileDisplayWidth = widthDip;
        TileDisplayHeight = heightDip;
        Bitmap = bitmap;
        ReleaseAfterBindingMoves(previous, bitmap);
    }

    /// <summary>Keep showing the composite, but let the next recompose replace it even for the same band (#1876).</summary>
    internal void MarkCompositeStale() => CompositeKey = default;

    /// <summary>Stop showing a composite and release it once the binding has moved off it (#1466).</summary>
    internal void ClearComposite()
    {
        var previous = _bitmap;
        Bitmap = null;
        CompositeKey = default;
        ReleaseAfterBindingMoves(previous, null);
    }

    /// <summary>
    /// Dispose a composite this slot no longer shows (#1466), but never
    /// synchronously.
    /// </summary>
    /// <remarks>
    /// A composite is bound to an Image (<c>{Binding Bitmap}</c>,
    /// PdfViewerControl.axaml), and Avalonia 12's <c>Bitmap.Dispose</c> releases
    /// its <c>IRef&lt;IBitmapImpl&gt;</c>: an Image still pointing at the
    /// disposed wrapper throws <see cref="ObjectDisposedException"/> on its next
    /// measure or render. By the time this runs, <see cref="Bitmap"/> has already
    /// changed and raised PropertyChanged, which the binding applies to
    /// <c>Image.Source</c> synchronously on the UI thread. The dispose is still
    /// posted at <see cref="DispatcherPriority.Background"/>, below the layout
    /// and render passes, so anything that picked up the old wrapper earlier in
    /// the same dispatcher turn finishes first. Frames the compositor already
    /// recorded are unaffected: render data holds its own cloned, ref-counted
    /// <c>IRef</c> to the pixels.
    /// <para>
    /// Every composite passes through <see cref="_bitmap"/> once —
    /// RecomposeSlotCore allocates a new one for each SetComposite — so each is
    /// posted at most once, and re-publishing the current instance posts
    /// nothing. If the dispatcher never runs the job (application shutdown), the
    /// bitmap falls back to the finalizer, as every composite did before.
    /// </para>
    /// </remarks>
    private static void ReleaseAfterBindingMoves(WriteableBitmap? previous, WriteableBitmap? current)
    {
        if (previous == null || ReferenceEquals(previous, current)) return;
        Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
