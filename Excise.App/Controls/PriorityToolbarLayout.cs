using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.App.Controls;

/// <summary>
/// How far the toolbar has degraded to fit its width (#1476). Each stage keeps
/// everything the previous stage removed removed.
/// </summary>
internal enum ToolbarStage
{
    /// <summary>Icons and text labels.</summary>
    Full = 0,

    /// <summary>Text labels hidden; tooltips and automation names still carry them.</summary>
    IconOnly = 1,

    /// <summary>Icon-only with smaller icons and tighter padding. Only this stage hides items.</summary>
    Compact = 2,
}

/// <summary>One participating toolbar child, as the planner sees it.</summary>
internal readonly record struct ToolbarItem(int Priority, bool IsSeparator);

/// <summary>
/// The outcome of <see cref="PriorityToolbarLayout.Plan"/>.
/// </summary>
/// <param name="Stage">The stage the row is laid out in.</param>
/// <param name="Shown">Per item: laid out with spacing. False for overflowed items,
/// separators with no shown item on both sides, and zero-width items.</param>
/// <param name="Overflowed">Per item: hidden because the row did not fit. Always
/// false for separators.</param>
/// <param name="Width">Width of the shown row at <paramref name="Stage"/>.</param>
/// <param name="StageWidths">Width of the row with nothing overflowed, for every
/// stage that was measured. Stages the planner did not need are absent.</param>
internal sealed record ToolbarPlan(
    ToolbarStage Stage,
    bool[] Shown,
    bool[] Overflowed,
    double Width,
    IReadOnlyDictionary<ToolbarStage, double> StageWidths);

/// <summary>
/// The pure decision behind <see cref="PriorityToolbarPanel"/>: given how wide
/// each item is at each stage, which stage to use and which items to hide.
///
/// <para>The order is the product requirement from #1476, applied strictly:
/// drop the text labels; then shrink the icons; then hide items, lowest
/// <see cref="ToolbarItem.Priority"/> first. Items that share a priority hide
/// together (rotate left/right, Open/Save), so a pair never loses one half.
/// Nothing wraps and nothing scrolls.</para>
///
/// <para>Stages are measured lazily, widest first, so a toolbar that fits at
/// <see cref="ToolbarStage.Full"/> is measured once.</para>
/// </summary>
internal static class PriorityToolbarLayout
{
    /// <summary>Sub-pixel slack so a row that fits exactly is not treated as overflowing.</summary>
    internal const double Epsilon = 0.01;

    private static readonly ToolbarStage[] StagesWidestFirst =
        [ToolbarStage.Full, ToolbarStage.IconOnly, ToolbarStage.Compact];

    /// <param name="items">Participating items, in layout order.</param>
    /// <param name="measure">Width of every item at a stage, same order as <paramref name="items"/>.
    /// Called at most once per stage, widest stage first, and the last call is always
    /// for the returned <see cref="ToolbarPlan.Stage"/>.</param>
    /// <param name="available">Width the row may occupy; may be infinite.</param>
    /// <param name="spacing">Gap between adjacent shown items.</param>
    public static ToolbarPlan Plan(
        IReadOnlyList<ToolbarItem> items,
        Func<ToolbarStage, IReadOnlyList<double>> measure,
        double available,
        double spacing)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(measure);

        var noOverflow = new bool[items.Count];
        var stageWidths = new Dictionary<ToolbarStage, double>();
        IReadOnlyList<double> widths = Array.Empty<double>();

        foreach (var stage in StagesWidestFirst)
        {
            widths = Checked(measure(stage), items.Count);
            var shown = ShownItems(items, widths, noOverflow);
            var width = RowWidth(widths, shown, spacing);
            stageWidths[stage] = width;
            if (width <= available + Epsilon)
                return new ToolbarPlan(stage, shown, noOverflow, width, stageWidths);
        }

        // Still too wide at Compact: hide whole priority groups, lowest first,
        // until the rest fits. `widths` holds the Compact measurements.
        var overflowed = new bool[items.Count];
        var priorities = items.Where(item => !item.IsSeparator)
            .Select(item => item.Priority)
            .Distinct()
            .Order();
        foreach (var threshold in priorities)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].IsSeparator && items[i].Priority <= threshold)
                    overflowed[i] = true;
            }

            var shown = ShownItems(items, widths, overflowed);
            var width = RowWidth(widths, shown, spacing);
            if (width <= available + Epsilon)
                return new ToolbarPlan(ToolbarStage.Compact, shown, overflowed, width, stageWidths);
        }

        // Unreachable when there is at least one item: the last threshold hides
        // every item and an empty row has width 0. Kept for an empty item list.
        return new ToolbarPlan(ToolbarStage.Compact, new bool[items.Count], overflowed, 0, stageWidths);
    }

    /// <summary>
    /// Which items take part in the row. A separator is shown only between two
    /// shown items, so hiding a group never leaves a leading, trailing or doubled
    /// divider. A zero-width item (a host whose content is collapsed) takes no
    /// spacing.
    /// </summary>
    internal static bool[] ShownItems(
        IReadOnlyList<ToolbarItem> items, IReadOnlyList<double> widths, IReadOnlyList<bool> overflowed)
    {
        var shown = new bool[items.Count];
        var pendingSeparator = -1;
        var anyShownBefore = false;

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsSeparator)
            {
                if (anyShownBefore && pendingSeparator < 0)
                    pendingSeparator = i;
                continue;
            }

            if (overflowed[i] || widths[i] <= 0)
                continue;

            if (pendingSeparator >= 0)
            {
                shown[pendingSeparator] = true;
                pendingSeparator = -1;
            }

            shown[i] = true;
            anyShownBefore = true;
        }

        return shown;
    }

    internal static double RowWidth(IReadOnlyList<double> widths, IReadOnlyList<bool> shown, double spacing)
    {
        var total = 0.0;
        var count = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            if (!shown[i])
                continue;
            total += widths[i];
            count++;
        }

        return count == 0 ? 0 : total + spacing * (count - 1);
    }

    private static IReadOnlyList<double> Checked(IReadOnlyList<double> widths, int expected)
    {
        if (widths is null || widths.Count != expected)
        {
            throw new InvalidOperationException(
                $"The toolbar measure callback returned {widths?.Count ?? 0} widths for {expected} items.");
        }

        return widths;
    }
}
