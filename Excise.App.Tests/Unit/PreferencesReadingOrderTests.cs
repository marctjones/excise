using System;
using System.Linq;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
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

    /// <summary>
    /// The real round trip: the view model's own writer produces the persisted
    /// string, and the view model's own preference setter consumes it back — not
    /// an ad-hoc string fed to <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>,
    /// which would pass even if the writer and the reader disagreed on the format.
    /// </summary>
    [Theory]
    [InlineData(ReadingOrderStrategy.ColumnAware)]
    [InlineData(ReadingOrderStrategy.Simple)]
    [InlineData(ReadingOrderStrategy.RawStream)]
    public void ReadingOrderStrategy_RoundTripsThroughWindowSettings(ReadingOrderStrategy strategy)
    {
        var vm = MainWindowViewModelTestFactory.Create();
        vm.ApplyReadingOrderStrategyPreference(strategy);
        var settings = new WindowSettings();

        vm.WritePreferencesTo(settings);

        Enum.TryParse<ReadingOrderStrategy>(settings.ReadingOrderStrategy, out var parsed)
            .Should().BeTrue("the writer must produce a name WritePreferencesTo's own reader can parse");
        var reloaded = MainWindowViewModelTestFactory.Create();
        reloaded.ApplyReadingOrderStrategyPreference(parsed);
        reloaded.ReadingOrderStrategy.Should().Be(strategy);
    }

    [Fact]
    public void WindowSettings_DefaultReadingOrderStrategy_IsColumnAware()
    {
        new WindowSettings().ReadingOrderStrategy.Should().Be("ColumnAware");
    }
}
