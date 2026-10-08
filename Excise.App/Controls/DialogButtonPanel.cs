using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Excise.App.Controls;

/// <summary>A dialog footer button's job, which decides where the platform puts it (#1999).</summary>
internal enum DialogButtonRole
{
    /// <summary>The default action (Save, Apply, Print, Start).</summary>
    Primary,

    /// <summary>Close without acting (Cancel, Close).</summary>
    Cancel,

    /// <summary>A further action, often one that changes data (Remove Protection, Reset to Defaults).</summary>
    Extra,
}

/// <summary>
/// A dialog footer that orders its buttons by platform convention (#1999):
/// <list type="bullet">
/// <item><b>Windows</b> (Fluent ContentDialog): Primary, Extra, Cancel — right-aligned.</item>
/// <item><b>macOS and Linux</b> (Apple HIG, GNOME HIG): Extra on the far left, then
/// Cancel, Primary on the right, the default action last.</item>
/// </list>
/// <para>The panel reorders its <see cref="Panel.Children"/> once they are loaded
/// rather than only arranging them, so Tab order and the automation tree follow the
/// same order the user sees. Declare buttons in any order; tag each with
/// <see cref="RoleProperty"/>.</para>
/// </summary>
internal sealed class DialogButtonPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<DialogButtonPanel, double>(nameof(Spacing), 8);

    public static readonly AttachedProperty<DialogButtonRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<DialogButtonPanel, Control, DialogButtonRole>("Role", DialogButtonRole.Primary);

    /// <summary>Tests set this to lay the panel out as another platform would; null means the running OS.</summary>
    internal static bool? WindowsOrderOverride { get; set; }

    static DialogButtonPanel()
    {
        AffectsMeasure<DialogButtonPanel>(SpacingProperty);
        AffectsParentMeasure<DialogButtonPanel>(RoleProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static DialogButtonRole GetRole(Control element) => element.GetValue(RoleProperty);
    public static void SetRole(Control element, DialogButtonRole value) => element.SetValue(RoleProperty, value);

    internal static bool UsesWindowsOrder => WindowsOrderOverride ?? OperatingSystem.IsWindows();

    /// <summary>Position of a role in the platform's left-to-right order.</summary>
    internal static int Rank(DialogButtonRole role, bool windows) => windows
        ? role switch { DialogButtonRole.Primary => 0, DialogButtonRole.Extra => 1, _ => 2 }
        : role switch { DialogButtonRole.Extra => 0, DialogButtonRole.Cancel => 1, _ => 2 };

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ApplyPlatformOrder();
    }

    /// <summary>Reorder <see cref="Panel.Children"/> into the platform's order (stable within a role).</summary>
    internal void ApplyPlatformOrder()
    {
        var windows = UsesWindowsOrder;
        var ordered = Children
            .Select((child, index) => (child, index))
            .OrderBy(x => Rank(GetRole(x.child), windows))
            .ThenBy(x => x.index)
            .Select(x => x.child)
            .ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Children.IndexOf(ordered[target]);
            if (current != target)
                Children.Move(current, target);
        }
    }

    // Extra buttons on macOS/Linux sit at the left edge, away from the
    // Cancel/Primary pair; everything else is one right-aligned row.
    private bool IsLeftGroup(Control child) => !UsesWindowsOrder && GetRole(child) == DialogButtonRole.Extra;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (!child.IsVisible)
                continue;
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            visible++;
        }
        width += Math.Max(0, visible - 1) * Spacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = Children.Where(c => c.IsVisible && IsLeftGroup(c)).ToList();
        var right = Children.Where(c => c.IsVisible && !IsLeftGroup(c)).ToList();

        var x = 0.0;
        foreach (var child in left)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        var rightWidth = right.Sum(c => c.DesiredSize.Width) + Math.Max(0, right.Count - 1) * Spacing;
        x = Math.Max(x, finalSize.Width - rightWidth);
        foreach (var child in right)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }
}
