using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Excise.App.Controls;

/// <summary>
/// A single-row toolbar panel that degrades to fit the width it is given
/// instead of scrolling or wrapping (#1476).
///
/// <para><b>Stages.</b> Each measure pass tries <see cref="ToolbarStage.Full"/>,
/// then <see cref="ToolbarStage.IconOnly"/>, then <see cref="ToolbarStage.Compact"/>,
/// and stops at the first that fits. The panel does not know what a label or an
/// icon is: it sets the <c>:icon-only</c> pseudo-class (IconOnly and Compact) and
/// <c>:compact</c> (Compact), and styles in the host decide what those mean,
/// e.g. <c>PriorityToolbarPanel:icon-only TextBlock.toolbar-label</c>. If the row
/// still does not fit at Compact, whole <see cref="PriorityProperty"/> groups are
/// hidden, lowest first. <see cref="PriorityToolbarLayout"/> holds the decision.</para>
///
/// <para><b>The panel owns its direct children's <see cref="Visual.IsVisible"/>.</b>
/// An item that should only appear in some state (redaction Apply, the typewriter
/// inspector) binds <see cref="IsAvailableProperty"/>, not <c>IsVisible</c>; a
/// binding on <c>IsVisible</c> would be overwritten by the first narrow layout
/// and never come back. Hiding through <c>IsVisible</c> rather than opacity keeps
/// an overflowed button out of the tab order, hit testing and the automation
/// tree. Its command must stay reachable from the menu; that is the host's
/// contract, pinned by <c>ToolbarWidthTests</c>.</para>
///
/// <para><b>Why measuring several stages in one pass is safe.</b> Switching a
/// pseudo-class restyles descendants synchronously and invalidates only them;
/// <see cref="InvalidateStaleAncestors"/> walks each stale descendant up to the
/// direct child so the child really re-measures. Changing a child's
/// <c>IsVisible</c> or desired size while this panel is measuring reaches the panel
/// through <c>ChildDesiredSizeChanged</c>, which Avalonia ignores during a
/// measure, so the pass does not re-invalidate itself.</para>
/// </summary>
[PseudoClasses(IconOnlyPseudoClass, CompactPseudoClass)]
internal sealed class PriorityToolbarPanel : Panel
{
    internal const string IconOnlyPseudoClass = ":icon-only";
    internal const string CompactPseudoClass = ":compact";

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PriorityToolbarPanel, double>(nameof(Spacing), 4);

    /// <summary>Higher stays longer. Items sharing a value hide together.</summary>
    public static readonly AttachedProperty<int> PriorityProperty =
        AvaloniaProperty.RegisterAttached<PriorityToolbarPanel, Control, int>("Priority");

    /// <summary>A divider: never hidden by priority, shown only between two shown items.</summary>
    public static readonly AttachedProperty<bool> IsSeparatorProperty =
        AvaloniaProperty.RegisterAttached<PriorityToolbarPanel, Control, bool>("IsSeparator");

    /// <summary>False removes the child from the row entirely. Bind this instead of IsVisible.</summary>
    public static readonly AttachedProperty<bool> IsAvailableProperty =
        AvaloniaProperty.RegisterAttached<PriorityToolbarPanel, Control, bool>("IsAvailable", defaultValue: true);

    /// <summary>
    /// Set by the panel: true while the child is hidden because the row did not
    /// fit. Read-only by contract; deliberately NOT a parent-measure property,
    /// because the panel writes it during its own measure.
    /// </summary>
    public static readonly AttachedProperty<bool> IsOverflowedProperty =
        AvaloniaProperty.RegisterAttached<PriorityToolbarPanel, Control, bool>("IsOverflowed");

    private readonly List<(Control Child, bool Shown)> _row = new();

    static PriorityToolbarPanel()
    {
        AffectsMeasure<PriorityToolbarPanel>(SpacingProperty);
        AffectsParentMeasure<PriorityToolbarPanel>(PriorityProperty, IsSeparatorProperty, IsAvailableProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The stage of the most recent measure pass.</summary>
    public ToolbarStage Stage { get; private set; }

    /// <summary>The most recent plan; for tests and diagnostics.</summary>
    internal ToolbarPlan? LastPlan { get; private set; }

    /// <summary>The width constraint of the most recent measure pass.</summary>
    internal double LastAvailableWidth { get; private set; }

    /// <summary>How many times <see cref="MeasureOverride"/> has run; a stable
    /// layout must not keep incrementing it.</summary>
    internal int MeasurePassCount { get; private set; }

    public static int GetPriority(Control element) => element.GetValue(PriorityProperty);
    public static void SetPriority(Control element, int value) => element.SetValue(PriorityProperty, value);

    public static bool GetIsSeparator(Control element) => element.GetValue(IsSeparatorProperty);
    public static void SetIsSeparator(Control element, bool value) => element.SetValue(IsSeparatorProperty, value);

    public static bool GetIsAvailable(Control element) => element.GetValue(IsAvailableProperty);
    public static void SetIsAvailable(Control element, bool value) => element.SetValue(IsAvailableProperty, value);

    public static bool GetIsOverflowed(Control element) => element.GetValue(IsOverflowedProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePassCount++;
        LastAvailableWidth = availableSize.Width;

        var participants = new List<Control>(Children.Count);
        var items = new List<ToolbarItem>(Children.Count);
        foreach (var child in Children)
        {
            var available = GetIsAvailable(child);
            // Every available child is measured visible, including one the
            // previous pass overflowed: a width that is only remembered goes
            // stale when the child's content changes.
            SetVisible(child, available);
            if (!available)
            {
                SetOverflowed(child, false);
                continue;
            }

            participants.Add(child);
            items.Add(new ToolbarItem(GetPriority(child), GetIsSeparator(child)));
        }

        var childConstraint = new Size(double.PositiveInfinity, availableSize.Height);
        var rowHeight = 0.0;

        IReadOnlyList<double> MeasureAt(ToolbarStage stage)
        {
            ApplyStage(stage);
            var widths = new double[participants.Count];
            for (var i = 0; i < participants.Count; i++)
            {
                var child = participants[i];
                InvalidateStaleAncestors(child);
                child.Measure(childConstraint);
                widths[i] = child.DesiredSize.Width;
                // Full is always measured first, so the row keeps its full-stage
                // height at every width instead of shrinking as icons do.
                rowHeight = System.Math.Max(rowHeight, child.DesiredSize.Height);
            }

            return widths;
        }

        var plan = PriorityToolbarLayout.Plan(items, MeasureAt, availableSize.Width, Spacing);
        LastPlan = plan;
        // The planner's last measure call was for plan.Stage, so the pseudo-classes
        // and every child's DesiredSize already match it.

        _row.Clear();
        for (var i = 0; i < participants.Count; i++)
        {
            var child = participants[i];
            var hiddenSeparator = items[i].IsSeparator && !plan.Shown[i];
            SetOverflowed(child, plan.Overflowed[i]);
            SetVisible(child, !plan.Overflowed[i] && !hiddenSeparator);
            _row.Add((child, plan.Shown[i]));
        }

        var width = double.IsPositiveInfinity(availableSize.Width)
            ? plan.Width
            : System.Math.Min(plan.Width, availableSize.Width);
        return new Size(width, rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var first = true;
        foreach (var (child, shown) in _row)
        {
            if (!child.IsVisible)
                continue;

            if (!shown)
            {
                // Visible but zero-width (its own content is collapsed): no spacing.
                child.Arrange(new Rect(x, 0, 0, finalSize.Height));
                continue;
            }

            if (!first)
                x += Spacing;
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
            first = false;
        }

        return finalSize;
    }

    private void ApplyStage(ToolbarStage stage)
    {
        Stage = stage;
        PseudoClasses.Set(IconOnlyPseudoClass, stage != ToolbarStage.Full);
        PseudoClasses.Set(CompactPseudoClass, stage == ToolbarStage.Compact);
    }

    /// <summary>
    /// A restyle invalidates the descendant whose property changed (a label's
    /// IsVisible, an icon's Width) but not the Button, ContentPresenter and
    /// StackPanel above it, and <see cref="Layoutable.Measure"/> on a valid child
    /// with an unchanged constraint returns its stale size. Invalidate every
    /// ancestor of a stale descendant up to and including the direct child.
    /// </summary>
    private static void InvalidateStaleAncestors(Control child)
    {
        foreach (var descendant in child.GetVisualDescendants())
        {
            if (descendant is not Layoutable { IsMeasureValid: false } stale)
                continue;

            var node = stale.GetVisualParent();
            while (node is Layoutable layoutable)
            {
                layoutable.InvalidateMeasure();
                if (ReferenceEquals(node, child))
                    break;
                node = node.GetVisualParent();
            }
        }
    }

    private static void SetVisible(Control child, bool visible)
    {
        if (child.IsVisible != visible)
            child.IsVisible = visible;
    }

    private static void SetOverflowed(Control child, bool overflowed)
    {
        if (child.GetValue(IsOverflowedProperty) != overflowed)
            child.SetValue(IsOverflowedProperty, overflowed);
    }
}
