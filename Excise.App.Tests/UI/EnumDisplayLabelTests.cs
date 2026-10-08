using Avalonia.Controls;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Converters;
using Excise.App.Models;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Editing;
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1998 — option lists show plain-language labels, never enum identifiers.
/// </summary>
[Collection("AvaloniaTests")]
public class EnumDisplayLabelTests
{
    public static TheoryData<Enum> EveryDisplayedEnumValue()
    {
        var data = new TheoryData<Enum>();
        void Add<T>() where T : struct, Enum { foreach (var v in Enum.GetValues<T>()) data.Add(v); }
        Add<DocumentOpenMode>();
        Add<PerformancePreset>();
        Add<ReadingOrderStrategy>();
        Add<WhitespaceMode>();
        Add<CarrierScrubMode>();
        Add<RedactionProfile>();
        Add<WidthPolicy>();
        Add<PrintScalingMode>();
        Add<BatesPosition>();
        Add<PdfEncryptionAlgorithm>();
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryDisplayedEnumValue))]
    public void EveryValue_HasAnExplicitLabel(Enum value)
    {
        EnumDisplayConverter.TryLabel(value).Should().NotBeNullOrWhiteSpace(
            $"{value.GetType().Name}.{value} needs a plain-language label in EnumDisplayConverter, " +
            "or the list falls back to showing the identifier");
    }

    [FixedAvaloniaFact]
    public async Task EveryEnumBoundComboBox_OptsIntoTheLabelTemplate()
    {
        var windows = new Window[]
        {
            new PreferencesWindow { DataContext = new PreferencesViewModel() },
            new SecurityDialog
            {
                DataContext = new SecurityDialogViewModel(
                    true, _ => true, (_, _, _) => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null)),
            },
            new BatesNumberingDialog { DataContext = new BatesNumberingDialogViewModel() },
        };

        var checkedCount = 0;
        foreach (var window in windows)
        {
            window.Show();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            foreach (var combo in window.GetLogicalDescendants().OfType<ComboBox>())
            {
                var first = combo.ItemsSource?.Cast<object>().FirstOrDefault();
                if (first is not Enum)
                    continue;
                checkedCount++;
                combo.Classes.Should().Contain("enum-labels",
                    $"{combo.Name} lists {first.GetType().Name} values and must show readable labels");
            }
            window.Close();
        }

        checkedCount.Should().Be(11, "nine Preferences lists, the encryption algorithm and the Bates position");
    }
}
