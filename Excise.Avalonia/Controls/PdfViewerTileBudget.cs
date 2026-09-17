using System;
using System.Collections.Generic;
using global::Avalonia.Threading;

namespace Excise.Avalonia.Controls;

/// <summary>
/// One continuous-view tile budget shared by several
/// <see cref="PdfViewerControl"/>s, so an application with N document windows
/// holds one budget rather than N (#1551 follow-up).
/// </summary>
/// <remarks>
/// <para>
/// Each viewer attached through <see cref="PdfViewerControl.SharedTileBudget"/>
/// keeps its own tile LRU and its own
/// <see cref="PdfViewerControl.ContinuousTileCacheByteBudget"/> cap. When a
/// viewer caches a tile and the tiles of all attached viewers together exceed
/// <see cref="ByteBudget"/>, the excess is taken from the OTHER viewers first,
/// in this order:
/// </para>
/// <list type="number">
/// <item>background viewers' render-ahead tiles (#1564), then their other
/// tiles outside their current bands, least recently used first;</item>
/// <item>only when the viewer asking is the <see cref="Foreground"/> one:
/// background viewers' band tiles too. Those are already baked into the
/// background page composites, so nothing on screen changes; the background
/// window re-renders them when it next scrolls (the same rule as
/// <see cref="PdfViewerCacheTrimLevel.Critical"/>).</item>
/// </list>
/// <para>
/// The foreground viewer never gives tiles to a background one: a background
/// viewer's budget is what the others leave it, but never less than its own
/// current bands, so a visible background window still draws. Whatever the
/// others hold is then charged to the asking viewer, which evicts its own tiles
/// exactly as it does alone.
/// </para>
/// <para>
/// With one attached viewer nothing is taken from anyone and the viewer's
/// budget is <c>min(ByteBudget, its own cap)</c>, so a single window behaves
/// exactly as it does without a shared budget when both are set to the same
/// value. The total can exceed <see cref="ByteBudget"/> only by current-band
/// tiles, which no budget evicts.
/// </para>
/// <para>UI thread only, like every cache mutation in the viewer.</para>
/// </remarks>
public sealed class PdfViewerTileBudget
{
    private readonly List<PdfViewerControl> _viewers = new();
    private long _byteBudget;
    private PdfViewerControl? _foreground;

    /// <summary>A shared budget of <paramref name="byteBudget"/> bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public PdfViewerTileBudget(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _byteBudget = byteBudget;
    }

    /// <summary>
    /// Bytes all attached viewers' tile caches may hold together. UI thread
    /// only. Lowering it evicts at once, the foreground viewer's needs first.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long ByteBudget
    {
        get => _byteBudget;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            if (value == _byteBudget)
                return;
            _byteBudget = value;
            Enforce();
        }
    }

    /// <summary>
    /// The viewer the user is looking at (the focused window's), or null when
    /// none is. UI thread only. Must be attached to this budget; a viewer that
    /// is not is ignored. Changing it evicts nothing: it only decides who gives
    /// way the next time the budget is exceeded.
    /// </summary>
    public PdfViewerControl? Foreground
    {
        get => _foreground;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            _foreground = value != null && _viewers.Contains(value) ? value : null;
        }
    }

    /// <summary>The attached viewers' tile bytes together. UI thread only.</summary>
    public long ResidentBytes
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            long total = 0;
            foreach (var viewer in _viewers)
                total += viewer.ContinuousTileCacheResidentBytes;
            return total;
        }
    }

    /// <summary>How many viewers are attached.</summary>
    public int ViewerCount => _viewers.Count;

    /// <summary>How many tiles this budget has taken from a viewer for another one.</summary>
    internal int CrossViewerEvictionCount { get; private set; }

    internal void Attach(PdfViewerControl viewer)
    {
        if (!_viewers.Contains(viewer))
            _viewers.Add(viewer);
    }

    internal void Detach(PdfViewerControl viewer)
    {
        _viewers.Remove(viewer);
        if (ReferenceEquals(_foreground, viewer))
            _foreground = null;
    }

    /// <summary>
    /// The byte budget <paramref name="requester"/> may fill now, after taking
    /// what the rules above allow from the other viewers.
    /// <paramref name="requesterFloor"/> is what the requester must keep (its
    /// current bands); it is ignored when the requester is alone.
    /// </summary>
    internal long BudgetFor(PdfViewerControl requester, long requesterResident, long requesterFloor)
    {
        if (_viewers.Count <= 1 || !_viewers.Contains(requester))
            return _byteBudget;

        long others = 0;
        foreach (var viewer in _viewers)
        {
            if (!ReferenceEquals(viewer, requester))
                others += viewer.ContinuousTileCacheResidentBytes;
        }

        long excess = requesterResident + others - _byteBudget;
        if (excess > 0)
        {
            bool requesterIsForeground = ReferenceEquals(requester, _foreground);
            for (int pass = 0; pass < 2 && excess > 0; pass++)
            {
                bool includeBands = pass == 1;
                if (includeBands && !requesterIsForeground)
                    break;
                foreach (var viewer in _viewers)
                {
                    if (excess <= 0)
                        break;
                    if (ReferenceEquals(viewer, requester) || ReferenceEquals(viewer, _foreground))
                        continue;
                    var (tiles, bytes) = viewer.EvictTilesForSharedBudget(excess, includeBands);
                    CrossViewerEvictionCount += tiles;
                    excess -= bytes;
                    others -= bytes;
                }
            }
        }

        return Math.Max(_byteBudget - others, requesterFloor);
    }

    /// <summary>Re-apply the budget to every viewer, the foreground one first.</summary>
    private void Enforce()
    {
        if (_foreground != null)
            _foreground.EnforceContinuousCacheBudget();
        foreach (var viewer in _viewers.ToArray())
        {
            if (!ReferenceEquals(viewer, _foreground))
                viewer.EnforceContinuousCacheBudget();
        }
    }
}
