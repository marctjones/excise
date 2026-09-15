using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.App.Controls;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1476: the pure staging decision behind the main toolbar. No Avalonia here,
/// so the order (labels, then icon size, then priority) is pinned independently
/// of fonts, themes and the window.
///
/// <para>Fixture row: <c>[a p10] [b p20] | [c p30] [d p40] [e p50]</c>, spacing 4.
/// Each item is 56 wide with its label, 16 icon-only and 8 compact; the
/// separator is 1. Row widths: Full 301, IconOnly 101, Compact 61.</para>
/// </summary>
public class PriorityToolbarLayoutTests
{
    private const double Spacing = 4;

    private static readonly ToolbarItem[] Row =
    [
        new(10, false), new(20, false), new(0, true), new(30, false), new(40, false), new(50, false),
    ];

    private static IReadOnlyList<double> RowWidths(ToolbarStage stage) =>
        Row.Select(item => item.IsSeparator
                ? 1.0
                : stage switch
                {
                    ToolbarStage.Full => 56.0,
                    ToolbarStage.IconOnly => 16.0,
                    _ => 8.0,
                })
            .ToArray();

    private static (ToolbarPlan Plan, List<ToolbarStage> Measured) Plan(
        double available,
        IReadOnlyList<ToolbarItem>? items = null,
        Func<ToolbarStage, IReadOnlyList<double>>? widths = null)
    {
        var measured = new List<ToolbarStage>();
        var plan = PriorityToolbarLayout.Plan(
            items ?? Row,
            stage =>
            {
                measured.Add(stage);
                return (widths ?? RowWidths)(stage);
            },
            available,
            Spacing);
        return (plan, measured);
    }

    [Fact]
    public void RowThatFitsWithLabels_KeepsEverything_AndIsMeasuredOnce()
    {
        var (plan, measured) = Plan(301);

        plan.Stage.Should().Be(ToolbarStage.Full);
        plan.Width.Should().Be(301);
        plan.Overflowed.Should().AllBeEquivalentTo(false);
        plan.Shown.Should().AllBeEquivalentTo(true);
        measured.Should().Equal(ToolbarStage.Full);
    }

    [Theory]
    [InlineData(300, (int)ToolbarStage.IconOnly)]
    [InlineData(101, (int)ToolbarStage.IconOnly)]
    [InlineData(100, (int)ToolbarStage.Compact)]
    [InlineData(61, (int)ToolbarStage.Compact)]
    public void LabelsGoBeforeIconsShrink_AndIconsShrinkBeforeAnythingHides(double available, int expectedStage)
    {
        // The stage enum is internal to Excise.App, and a public theory cannot take
        // an internal parameter type, so it travels as its int value.
        var expected = (ToolbarStage)expectedStage;
        var (plan, measured) = Plan(available);

        plan.Stage.Should().Be(expected);
        plan.Overflowed.Should().AllBeEquivalentTo(false, "nothing hides while a smaller stage still fits");
        measured.Should().Equal(
            expected == ToolbarStage.IconOnly
                ? [ToolbarStage.Full, ToolbarStage.IconOnly]
                : [ToolbarStage.Full, ToolbarStage.IconOnly, ToolbarStage.Compact],
            "stages are measured widest first and only as far as needed");
        plan.Width.Should().BeLessThanOrEqualTo(available);
    }

    [Fact]
    public void RowTooWideAtCompact_HidesTheLowestPriorityFirst()
    {
        // Hiding a (p10) leaves b | c d e = 4*8 + 1 + 4*4 = 49.
        var (plan, _) = Plan(49);

        plan.Stage.Should().Be(ToolbarStage.Compact);
        plan.Overflowed.Should().Equal(true, false, false, false, false, false);
        plan.Shown.Should().Equal(false, true, true, true, true, true);
        plan.Width.Should().Be(49);
    }

    [Fact]
    public void HidingEverythingBeforeASeparator_AlsoHidesTheSeparator()
    {
        // 49 does not fit 40, so b goes too; the divider would now lead the row.
        var (plan, _) = Plan(40);

        plan.Overflowed.Should().Equal(true, true, false, false, false, false);
        plan.Shown.Should().Equal(new[] { false, false, false, true, true, true },
            "a separator is shown only between two shown items");
        plan.Width.Should().Be(3 * 8 + 2 * Spacing);
    }

    [Fact]
    public void ItemsSharingAPriority_HideTogether()
    {
        ToolbarItem[] items = [new(10, false), new(10, false), new(50, false)];
        IReadOnlyList<double> Widths(ToolbarStage _) => [8.0, 8.0, 8.0];

        // All three need 32; hiding only the first would need 20 and fit, but the
        // pair shares a priority, so both go.
        var (plan, _) = Plan(20, items, Widths);

        plan.Overflowed.Should().Equal(true, true, false);
        plan.Width.Should().Be(8);
    }

    [Fact]
    public void ANarrowerRow_NeverShowsMoreThanAWiderOne()
    {
        var previousStage = ToolbarStage.Full;
        var previousHidden = 0;

        for (var available = 320.0; available >= 0; available -= 1)
        {
            var (plan, _) = Plan(available);
            var hidden = plan.Overflowed.Count(o => o);

            ((int)plan.Stage).Should().BeGreaterThanOrEqualTo((int)previousStage, $"at {available}");
            hidden.Should().BeGreaterThanOrEqualTo(previousHidden, $"at {available}");
            plan.Width.Should().BeLessThanOrEqualTo(available + PriorityToolbarLayout.Epsilon, $"at {available}");

            previousStage = plan.Stage;
            previousHidden = hidden;
        }
    }

    [Fact]
    public void NoRoomAtAll_HidesEveryItem()
    {
        var (plan, _) = Plan(5);

        plan.Stage.Should().Be(ToolbarStage.Compact);
        plan.Overflowed.Should().Equal(true, true, false, true, true, true);
        plan.Shown.Should().AllBeEquivalentTo(false);
        plan.Width.Should().Be(0);
    }

    [Fact]
    public void UnconstrainedWidth_KeepsLabels()
    {
        var (plan, measured) = Plan(double.PositiveInfinity);

        plan.Stage.Should().Be(ToolbarStage.Full);
        measured.Should().Equal(ToolbarStage.Full);
    }

    [Fact]
    public void AZeroWidthItem_TakesNoSpacing_AndIsNotOverflowed()
    {
        ToolbarItem[] items = [new(10, false), new(20, false), new(30, false)];
        IReadOnlyList<double> Widths(ToolbarStage _) => [10.0, 0.0, 10.0];

        var (plan, _) = Plan(1000, items, Widths);

        plan.Width.Should().Be(10 + Spacing + 10);
        plan.Shown.Should().Equal(true, false, true);
        plan.Overflowed.Should().AllBeEquivalentTo(false);
    }

    [Fact]
    public void Separators_NeverLead_Trail_OrDouble()
    {
        ToolbarItem[] items =
        [
            new(0, true), new(10, false), new(0, true), new(0, true), new(20, false), new(0, true),
        ];
        IReadOnlyList<double> Widths(ToolbarStage _) => [1.0, 10.0, 1.0, 1.0, 10.0, 1.0];

        var (plan, _) = Plan(1000, items, Widths);

        plan.Shown.Should().Equal(false, true, true, false, true, false);
        plan.Width.Should().Be(10 + 1 + 10 + 2 * Spacing);
    }

    [Fact]
    public void AMeasureCallbackReturningTheWrongNumberOfWidths_Throws()
    {
        var act = () => PriorityToolbarLayout.Plan(Row, _ => [1.0], 100, Spacing);

        act.Should().Throw<InvalidOperationException>();
    }
}
