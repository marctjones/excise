using System;
using System.Collections.Generic;

namespace Excise.Avalonia.Controls;

/// <summary>
/// A reader's position in the continuous view expressed independently of the
/// current layout: the 1-based <see cref="Page"/> at the viewport top and the
/// <see cref="Fraction"/> (0..1) into that page. Capturing this BEFORE a
/// re-layout (zoom, rotate, page add/remove/move, view-mode switch) and
/// resolving it AFTER keeps the reader on the same content even though every
/// slot's absolute offset changed.
/// </summary>
internal readonly record struct ReadingAnchor(int Page, double Fraction)
{
    public bool IsValid => Page >= 1;
    public static readonly ReadingAnchor None = new(0, 0);
}

/// <summary>One slot's vertical geometry in continuous-view DIPs.</summary>
internal readonly record struct SlotBox(double Top, double Height);

/// <summary>
/// Pure reading-position math for the continuous view (#846 groundwork). Extracted
/// verbatim from the two divergent inline copies that lived in
/// <c>PdfViewerControl.ApplyContinuousZoom</c> (capture) and
/// <c>ApplyPendingZoomAnchor</c> (resolve) so the position-preservation behaviour
/// can be unit-tested headlessly and, later, shared by EVERY re-layout instead of
/// only zoom. No behaviour change: these are the exact formulas the zoom path
/// already used.
/// </summary>
internal static class ContinuousReadingAnchor
{
    /// <summary>
    /// The page at the viewport top and the fraction into it, for the current
    /// <paramref name="offsetY"/>. Mirrors the zoom-path capture loop: the anchor
    /// page is the first whose bottom edge (incl. the trailing gap) passes the
    /// offset, or the last page.
    /// </summary>
    public static ReadingAnchor Capture(IReadOnlyList<SlotBox> slots, double offsetY, double pageGapDip)
    {
        if (slots.Count == 0) return ReadingAnchor.None;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (offsetY < s.Top + s.Height + pageGapDip || i == slots.Count - 1)
            {
                double fraction = s.Height > 0
                    ? Math.Clamp((offsetY - s.Top) / s.Height, 0, 1)
                    : 0;
                return new ReadingAnchor(i + 1, fraction);
            }
        }
        return new ReadingAnchor(slots.Count, 0);
    }

    /// <summary>
    /// The (unclamped) target viewport-top offset that places <paramref name="anchor"/>
    /// at the top of the viewport in the given (post-re-layout) slot geometry.
    /// Returns 0 for an invalid anchor. The caller clamps to the reachable extent
    /// (see <see cref="ClampToExtent"/>) — kept separate so callers can compare the
    /// clamped result against the true target to know when the anchor has landed.
    /// </summary>
    public static double ResolveTarget(IReadOnlyList<SlotBox> slots, ReadingAnchor anchor)
    {
        if (anchor.Page < 1 || anchor.Page > slots.Count) return 0;
        var s = slots[anchor.Page - 1];
        return s.Top + anchor.Fraction * s.Height;
    }

    /// <summary>Clamp a target offset to the scrollable range [0, extent - viewport].</summary>
    public static double ClampToExtent(double target, double extentHeight, double viewportHeight)
    {
        double max = Math.Max(0, extentHeight - viewportHeight);
        return Math.Clamp(Math.Min(target, max), 0, double.MaxValue);
    }
}
