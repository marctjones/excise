using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Controls;
using Excise.App.Tests.Utilities;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476: <see cref="PriorityToolbarPanel"/> on its own, with fixed-size items so
/// every expected width is exact. <see cref="PriorityToolbarLayoutTests"/> pins the
/// decision; this pins that the panel really applies it inside Avalonia's layout:
/// that a restyle is re-measured in the SAME pass (not left stale until the next
/// one), that a settled layout stops measuring, and that the panel's ownership of
/// IsVisible does not swallow an item's own availability.
///
/// <para>Row: <c>[a p10] [b p20] | [c p30] [d p40] [e p50]</c>, spacing 4. Each item
/// is a 16 px icon plus a 40 px label; the styles hide the label at
/// <c>:icon-only</c> and make the icon 8 px at <c>:compact</c>. Row widths: Full 301,
/// IconOnly 101, Compact 61.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class PriorityToolbarPanelTests
{
    private sealed record Fixture(Window Window, PriorityToolbarPanel Panel, Border[] Items, Border Separator)
    {
        public Border Icon(int item) => Items[item].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("icon"));
        public Border Label(int item) => Items[item].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("label"));
    }

    private static Fixture Create(double width)
    {
        var items = new[] { 10, 20, 30, 40, 50 }.Select(Item).ToArray();
        var separator = new Border { Width = 1, Height = 16 };
        PriorityToolbarPanel.SetIsSeparator(separator, true);

        var panel = new PriorityToolbarPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.AddRange([items[0], items[1], separator, items[2], items[3], items[4]]);

        var window = new Window { Width = width, Height = 100, Content = panel };
        // Sizes come from styles, not local values: a local Width would outrank the
        // :compact setter and the icon could never shrink.
        window.Styles.Add(new Style(x => x.OfType<Border>().Class("icon"))
        {
            Setters = { new Setter(Layoutable.WidthProperty, 16.0), new Setter(Layoutable.HeightProperty, 16.0) },
        });
        window.Styles.Add(new Style(x => x.OfType<PriorityToolbarPanel>()
            .Class(PriorityToolbarPanel.IconOnlyPseudoClass).Descendant().OfType<Border>().Class("label"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
        window.Styles.Add(new Style(x => x.OfType<PriorityToolbarPanel>()
            .Class(PriorityToolbarPanel.CompactPseudoClass).Descendant().OfType<Border>().Class("icon"))
        {
            Setters = { new Setter(Layoutable.WidthProperty, 8.0) },
        });

        window.Show();
        Settle(window);
        return new Fixture(window, panel, items, separator);
    }

    private static Border Item(int priority)
    {
        var icon = new Border();
        icon.Classes.Add("icon");
        var label = new Border { Width = 40, Height = 16 };
        label.Classes.Add("label");
        var item = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
        };
        PriorityToolbarPanel.SetPriority(item, priority);
        return item;
    }

    private static void Resize(Fixture f, double width)
    {
        f.Window.Width = width;
        Settle(f.Window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [FixedAvaloniaFact]
    public void WideRow_KeepsLabelsAndEveryItem()
    {
        var f = Create(400);
        try
        {
            f.Panel.Stage.Should().Be(ToolbarStage.Full);
            f.Items.Should().OnlyContain(item => item.IsVisible && !PriorityToolbarPanel.GetIsOverflowed(item));
            f.Items.Select(item => item.Bounds.Width).Should().AllBeEquivalentTo(56.0);
            f.Label(0).IsEffectivelyVisible.Should().BeTrue();
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void RowTooWideForLabels_DropsLabelsFirst_AndMeasuresTheRestyleInTheSamePass()
    {
        var f = Create(200);
        try
        {
            f.Panel.Stage.Should().Be(ToolbarStage.IconOnly);
            f.Panel.LastPlan!.StageWidths[ToolbarStage.Full].Should().Be(301);
            f.Panel.LastPlan.StageWidths[ToolbarStage.IconOnly].Should().Be(101,
                "the planner's icon-only probe must see the restyled items in the SAME measure pass; " +
                "301 here would mean the hidden labels were not re-measured and the probe read stale sizes");
            f.Label(0).IsEffectivelyVisible.Should().BeFalse();
            f.Icon(0).Bounds.Width.Should().Be(16);
            f.Items.Select(item => item.Bounds.Width).Should().AllBeEquivalentTo(16.0);
            f.Items.Should().OnlyContain(item => item.IsVisible, "nothing hides while icon-only fits");
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void RowTooWideForIcons_ShrinksIconsBeforeHidingAnything()
    {
        var f = Create(80);
        try
        {
            f.Panel.Stage.Should().Be(ToolbarStage.Compact);
            f.Panel.LastPlan!.StageWidths[ToolbarStage.Compact].Should().Be(61,
                "the compact probe must measure the shrunken icons in the same pass");
            f.Icon(0).Bounds.Width.Should().Be(8);
            f.Items.Should().OnlyContain(item => item.IsVisible && !PriorityToolbarPanel.GetIsOverflowed(item));
            f.Separator.IsVisible.Should().BeTrue();
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void RowTooNarrowForCompact_HidesLowestPriorityGroups_AndTheStrandedSeparator()
    {
        var f = Create(40);
        try
        {
            f.Panel.Stage.Should().Be(ToolbarStage.Compact);
            f.Items.Take(2).Should().OnlyContain(item => !item.IsVisible && PriorityToolbarPanel.GetIsOverflowed(item));
            f.Items.Skip(2).Should().OnlyContain(item => item.IsVisible && !PriorityToolbarPanel.GetIsOverflowed(item));
            f.Separator.IsVisible.Should().BeFalse("nothing is left before it");

            var shown = f.Items.Skip(2).ToArray();
            for (var i = 1; i < shown.Length; i++)
                shown[i].Bounds.Left.Should().BeGreaterThanOrEqualTo(shown[i - 1].Bounds.Right, "items must not overlap");
            shown[^1].Bounds.Right.Should().BeLessThanOrEqualTo(40, "the row must end inside the panel");
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void Rewidening_RestoresLabelsAndEveryHiddenItem()
    {
        var f = Create(40);
        try
        {
            Resize(f, 400);

            f.Panel.Stage.Should().Be(ToolbarStage.Full);
            f.Items.Should().OnlyContain(item => item.IsVisible && !PriorityToolbarPanel.GetIsOverflowed(item));
            f.Separator.IsVisible.Should().BeTrue();
            f.Label(0).IsEffectivelyVisible.Should().BeTrue();
            f.Items.Select(item => item.Bounds.Width).Should().AllBeEquivalentTo(56.0);
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void ASettledLayout_StopsMeasuring_AtEveryStage()
    {
        var f = Create(400);
        try
        {
            foreach (var width in new[] { 400.0, 200, 80, 40, 400 })
            {
                Resize(f, width);
                var passes = f.Panel.MeasurePassCount;

                Settle(f.Window);
                Settle(f.Window);

                f.Panel.MeasurePassCount.Should().Be(passes,
                    $"at {width} px a layout with nothing changed must not measure the panel again; " +
                    "a growing count means the stage probing re-invalidates the panel it runs in");
                f.Panel.IsMeasureValid.Should().BeTrue();
                f.Panel.GetVisualDescendants().OfType<Layoutable>()
                    .Where(l => l.IsEffectivelyVisible)
                    .Should().OnlyContain(l => l.IsMeasureValid, $"at {width} px nothing may be left stale");
            }
        }
        finally
        {
            f.Window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void AnUnavailableItem_TakesNoSpace_AndOverflowNeverOverridesItsAvailability()
    {
        var f = Create(400);
        try
        {
            PriorityToolbarPanel.SetIsAvailable(f.Items[4], false);
            Settle(f.Window);
            f.Items[4].IsVisible.Should().BeFalse();
            PriorityToolbarPanel.GetIsOverflowed(f.Items[4]).Should().BeFalse("unavailable is not overflowed");
            f.Panel.LastPlan!.Width.Should().Be(301 - 56 - 4);

            // Overflow the lowest items, make one of them unavailable, then widen:
            // the widening must not bring back an item that is unavailable.
            Resize(f, 30);
            PriorityToolbarPanel.GetIsOverflowed(f.Items[0]).Should().BeTrue();
            PriorityToolbarPanel.SetIsAvailable(f.Items[0], false);
            Resize(f, 400);
            f.Items[0].IsVisible.Should().BeFalse("it is still unavailable");

            PriorityToolbarPanel.SetIsAvailable(f.Items[0], true);
            PriorityToolbarPanel.SetIsAvailable(f.Items[4], true);
            Settle(f.Window);
            f.Items.Should().OnlyContain(item => item.IsVisible);
            f.Panel.LastPlan!.Width.Should().Be(301);
        }
        finally
        {
            f.Window.Close();
        }
    }
}
