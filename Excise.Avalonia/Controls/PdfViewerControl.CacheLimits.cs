using System;
using System.Threading;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Host-adjustable cache and render limits, so an application can trade
/// memory and CPU against scroll-back speed while a document is open. Each
/// setter takes effect at once and follows the same lifetime rules as
/// <see cref="TrimCaches"/>: UI thread only, the visible band's tiles and the
/// bitmap on screen are never dropped, and in-flight renders are not cancelled.
/// </summary>
public partial class PdfViewerControl
{
    /// <summary>
    /// Byte budget of the continuous view's tile cache (default 200 MiB). UI
    /// thread only. Lowering it evicts least-recently-used tiles at once until
    /// the cache fits, but never a tile of the current bands: if those alone
    /// exceed the new budget the cache stays above it until the bands move.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long ContinuousTileCacheByteBudget
    {
        get => ContinuousPart.ContinuousCacheByteBudgetSetting;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ContinuousPart.ContinuousCacheByteBudgetSetting = value;
            ContinuousPart.EnforceContinuousCacheBudget();
        }
    }

    /// <summary>
    /// A tile budget this viewer shares with other viewers, or null for none
    /// (the default). UI thread only. While set, this viewer's tile cache is
    /// bounded by <see cref="ContinuousTileCacheByteBudget"/> AND by what the
    /// shared budget leaves it; see <see cref="PdfViewerTileBudget"/> for who
    /// gives way. Setting it applies the budget at once. A host that discards
    /// the viewer sets it back to null, or the shared budget keeps the viewer
    /// (and counts its tiles) until then.
    /// </summary>
    public PdfViewerTileBudget? SharedTileBudget
    {
        get => _sharedTileBudget;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (ReferenceEquals(value, _sharedTileBudget))
                return;
            _sharedTileBudget?.Detach(this);
            _sharedTileBudget = value;
            value?.Attach(this);
            ContinuousPart.EnforceContinuousCacheBudget();
        }
    }

    private PdfViewerTileBudget? _sharedTileBudget;

    /// <summary>
    /// A shared budget needs <paramref name="bytesWanted"/> for another viewer: the
    /// continuous view gives up tiles, least valuable first (see its own summary).
    /// </summary>
    internal (int Tiles, long Bytes) EvictTilesForSharedBudget(long bytesWanted, bool includeBands) =>
        ContinuousPart.EvictTilesForSharedBudget(bytesWanted, includeBands);

    /// <summary>Evict the continuous view's tiles down to its budget; returns how many.</summary>
    internal int EnforceContinuousCacheBudget() => ContinuousPart.EnforceContinuousCacheBudget();


    /// <summary>
    /// Resident bytes the continuous view's tile cache holds now (4 bytes per
    /// pixel). UI thread only. Computed on read, so it does not depend on a
    /// metrics listener being enabled.
    /// </summary>
    public long ContinuousTileCacheResidentBytes
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            return ContinuousPart.ContinuousCacheResidentBytes();
        }
    }

    /// <summary>
    /// How many rendered pages the single-page view keeps for instant
    /// back-navigation (default 6). UI thread only. Lowering it disposes the
    /// least-recently-used bitmaps at once, never the one on screen.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int SinglePageCacheCapacity
    {
        get => _singlePageRenderLifetime.GetCacheDiagnostics().Capacity;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            var shown = PdfImage?.Source as WriteableBitmap;
            _singlePageRenderLifetime.SetCapacity(value, bitmap => ReferenceEquals(bitmap, shown));
        }
    }

    /// <summary>
    /// How many continuous-view band renders may run at once (default
    /// clamp(CPU count - 1, 2, 6)). UI thread only. Renders that start after
    /// the change use the new width; renders already waiting or running finish
    /// under the old one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int ContinuousRenderConcurrency
    {
        get => ContinuousPart.ContinuousRenderConcurrency;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ContinuousPart.ContinuousRenderConcurrency = value;
        }
    }

}
