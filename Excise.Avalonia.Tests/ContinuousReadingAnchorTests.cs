using System.Collections.Generic;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Characterization tests for the continuous-view reading-position math (#846
/// Phase A). These make the position-preservation behaviour OBSERVABLE headlessly
/// — the behaviour that was previously only reachable through a live GUI and was
/// therefore surface-patched. They pin, per re-layout, whether capturing the
/// reader's (page, fraction) before and resolving it after keeps the reader on the
/// same CONTENT.
///
/// The finding they encode: the math preserves position perfectly for re-layouts
/// that keep page IDENTITY (zoom, rotate) — so #846's rotate displacement is NOT a
/// math bug, it is that the mutation path never calls this math (it uses a fragile
/// one-shot scroll). Re-layouts that change page identity (remove/move) expose a
/// second, distinct gap: the anchor keys on page NUMBER, so it lands on the wrong
/// content after an earlier page is removed.
/// </summary>
public class ContinuousReadingAnchorTests
{
    private const double Gap = 12;

    private static IReadOnlyList<SlotBox> Layout(params double[] heights)
    {
        var boxes = new SlotBox[heights.Length];
        double top = 0;
        for (int i = 0; i < heights.Length; i++)
        {
            boxes[i] = new SlotBox(top, heights[i]);
            top += heights[i] + Gap;
        }
        return boxes;
    }

    private static double FractionInto(IReadOnlyList<SlotBox> slots, int page, double offset)
        => (offset - slots[page - 1].Top) / slots[page - 1].Height;

    [Fact]
    public void CaptureThenResolve_SameLayout_IsIdentity()
    {
        var slots = Layout(1000, 1000, 1000, 1000, 1000);
        double offset = slots[2].Top + 0.5 * slots[2].Height; // mid page 3

        var anchor = ContinuousReadingAnchor.Capture(slots, offset, Gap);
        anchor.Page.Should().Be(3);
        anchor.Fraction.Should().BeApproximately(0.5, 1e-9);

        ContinuousReadingAnchor.ResolveTarget(slots, anchor).Should().BeApproximately(offset, 1e-6,
            "resolving an anchor against the layout it was captured from must return the same offset");
    }

    [Fact]
    public void Zoom_PreservesPageAndFraction()
    {
        var before = Layout(1000, 1000, 1000, 1000, 1000);
        double offset = before[3].Top + 0.25 * before[3].Height; // 25% into page 4
        var anchor = ContinuousReadingAnchor.Capture(before, offset, Gap);

        // Zoom in 1.5x: every page height scales (gap is unscaled here, but the
        // point is the fraction is layout-independent).
        var after = Layout(1500, 1500, 1500, 1500, 1500);
        double target = ContinuousReadingAnchor.ResolveTarget(after, anchor);

        anchor.Page.Should().Be(4);
        FractionInto(after, 4, target).Should().BeApproximately(0.25, 1e-9,
            "zoom keeps the reader at the same page + fraction");
    }

    [Fact]
    public void RotateOnePage_HeightChanges_StillKeepsReaderOnSamePageAndFraction()
    {
        // The #846 scenario, at the math level: the reader is mid page 3; page 3 is
        // rotated portrait->landscape (shorter); pages 4+ shift up. The captured
        // fraction must resolve to the SAME fraction of page 3 in the new layout.
        var before = Layout(1000, 1000, 1000, 1000, 1000);
        double offset = before[2].Top + 0.5 * before[2].Height; // mid page 3
        var anchor = ContinuousReadingAnchor.Capture(before, offset, Gap);

        var after = Layout(1000, 1000, 700, 1000, 1000); // page 3 now landscape (shorter)
        double target = ContinuousReadingAnchor.ResolveTarget(after, anchor);

        anchor.Page.Should().Be(3);
        FractionInto(after, 3, target).Should().BeApproximately(0.5, 1e-9,
            "rotating the reader's page must keep them at the same fraction OF THAT PAGE — " +
            "the math does this correctly, so #846's rotate displacement is an ORCHESTRATION gap " +
            "(the mutation path never calls this), not a math bug");
    }

    [Fact]
    public void RemovePage_AnchorKeysOnPageNumber_LandsOnWrongContent_IdentityGap()
    {
        // Reader is on page 4. Removing an EARLIER page (page 2) shifts identities:
        // the content the reader was viewing (old page 4) becomes new page 3. But
        // the anchor holds page NUMBER 4, so it resolves onto new page 4 (== old
        // page 5) — one page too far. This is the distinct identity gap remove/move
        // must handle (adjust the anchor page when pages before it are removed).
        var before = Layout(1000, 1000, 1000, 1000, 1000);
        double offset = before[3].Top + 0.3 * before[3].Height; // 30% into page 4
        var anchor = ContinuousReadingAnchor.Capture(before, offset, Gap);
        anchor.Page.Should().Be(4);

        var after = Layout(1000, 1000, 1000, 1000); // page 2 removed → 4 pages
        double target = ContinuousReadingAnchor.ResolveTarget(after, anchor);

        // Current behaviour: still resolves page NUMBER 4 (the reader's old page 5).
        FractionInto(after, 4, target).Should().BeApproximately(0.3, 1e-9);
        // The reader WANTED to stay on their old page-4 content, which is now page 3.
        // Documenting the gap: page-number anchoring does not preserve identity here.
        anchor.Page.Should().NotBe(3,
            "characterizes the identity gap: after removing an earlier page the anchor should " +
            "have shifted to page 3 to keep the reader's content, but it still says page 4");
    }
}
