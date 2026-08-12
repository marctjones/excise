using System;
using System.Linq;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.Core.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Configuration surface for the reading-order strategy (#774, Part 3): the
/// preferences view-model exposes all three strategies, defaults to the
/// highest-quality one, and the choice round-trips through the persisted
/// window settings.
/// </summary>
public class PreferencesReadingOrderTests
{
    [Fact]
    public void PreferencesViewModel_ExposesAllStrategies_DefaultingToColumnAware()
    {
        var vm = new PreferencesViewModel();

        vm.ReadingOrderStrategyOptions.Should().BeEquivalentTo(new[]
        {
            ReadingOrderStrategy.ColumnAware,
            ReadingOrderStrategy.Simple,
            ReadingOrderStrategy.RawStream,
        });
        vm.SelectedReadingOrderStrategy.Should().Be(ReadingOrderStrategy.ColumnAware,
            "column-aware is the best-default copy behaviour");
    }

    [Fact]
    public void PreferencesViewModel_ResetToDefaults_RestoresColumnAware()
    {
        var vm = new PreferencesViewModel { SelectedReadingOrderStrategy = ReadingOrderStrategy.RawStream };
        vm.ResetToDefaultsCommand.Execute().Subscribe();
        vm.SelectedReadingOrderStrategy.Should().Be(ReadingOrderStrategy.ColumnAware);
    }

    [Theory]
    [InlineData("ColumnAware", ReadingOrderStrategy.ColumnAware)]
    [InlineData("Simple", ReadingOrderStrategy.Simple)]
    [InlineData("RawStream", ReadingOrderStrategy.RawStream)]
    public void WindowSettings_ReadingOrderStrategy_ParsesBackToEnum(string stored, ReadingOrderStrategy expected)
    {
        var settings = new WindowSettings { ReadingOrderStrategy = stored };
        Enum.TryParse<ReadingOrderStrategy>(settings.ReadingOrderStrategy, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    [Fact]
    public void WindowSettings_DefaultReadingOrderStrategy_IsColumnAware()
    {
        new WindowSettings().ReadingOrderStrategy.Should().Be("ColumnAware");
    }
}
