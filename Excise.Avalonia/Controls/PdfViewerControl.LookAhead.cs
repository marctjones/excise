using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Excise.Rendering;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Render-ahead (#1564): once the visible page has finished drawing and the
/// viewer is idle, render the page a page turn would show next (then the one
/// before it) at the current zoom, so the turn is a cache hit and draws in one
/// step.
/// </summary>
/// <remarks>
/// <para>
/// Why: the #1544 speed benchmark (2026-09-17) measured excise page turns
/// finishing in 73–120 ms (Altona p95 394 ms) against 30–40 ms for Preview and
/// Acrobat, and most excise turns drew in TWO steps (IRS 52 of 60, Altona 31
/// of 45): the page moved, then its pixels arrived 36–70 ms later. The
/// continuous view renders only the viewport plus
/// <see cref="ContinuousTileOverscanDip"/> (256 DIP); at fit-width a page is
/// ~1000 DIP tall, so the next page was never rendered before the turn.
/// </para>
/// <para>
/// What it renders. A page turn in the continuous view is
/// <see cref="NextPage"/> → <see cref="ScrollToPageContinuous(int)"/>, which
/// sets <c>Offset.Y</c> to the target slot's top. Look-ahead therefore
/// simulates the render pass at THAT offset — same
/// <see cref="RequiredTileCells"/>, same <see cref="CellKey"/>, same per-page
/// batch — rather than "rendering page N+1". The keys it caches are the keys
/// the real pass will ask for, and a page's missing cells form the same batch
/// the real pass would issue, so the band render and its slicing are the same
/// calls and the composite is pixel-identical (pinned by
/// <c>RenderAheadTests</c>). Look-ahead changes timing only.
/// </para>
/// <para>
/// Cost and bounds. One look-ahead batch at a time, behind the same render
/// gate as visible bands (width ≥ 2, so a visible band always gets a slot),
/// and only when no visible band is in flight. Its tiles are charged to the
/// existing tile budget (<see cref="ContinuousCacheByteBudget"/>) and may evict
/// scroll-back tiles, never a tile of the current bands. At fit-width on a 2×
/// display a letter page is ~1464×1894 px, ~11 MiB, so N±1 costs ~22 MiB of a
/// 200 MiB budget. Every trim level drops them (they are outside the current
/// bands), and a trim never re-arms look-ahead: the #1478 contract is that a
/// trim re-renders nothing.
/// </para>
/// <para>
/// No timer, no polling (#1462). Look-ahead is scheduled from exactly two
/// places — the end of a render pass with nothing left to render, and the end
/// of a band render — and each step either starts a batch for cells it has not
/// tried at this scroll position or marks the position done. Keys are tried at
/// most once per position (a tile the budget refuses is not retried), so the
/// chain ends and an idle viewer does no work.
/// </para>
/// </remarks>
public partial class PdfViewerControl
{
    /// <summary>
    /// Whether the viewer renders the next and previous page ahead (#1564).
    /// Default on. Tests turn it off to compare a cold page turn with a
    /// pre-rendered one.
    /// </summary>
    internal bool RenderAheadEnabled
    {
        get => _renderAheadEnabled;
        set
        {
            _renderAheadEnabled = value;
            ContinuousPart.SetRenderAheadEnabled(value);
            SinglePagePart.SetRenderAheadEnabled(value);
            if (!value)
            {
                ContinuousPart.CancelContinuousLookAhead();
                SinglePagePart.CancelSinglePageLookAhead();
            }
        }
    }

    private bool _renderAheadEnabled = true;

}
