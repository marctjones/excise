using System.Threading.Tasks;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1888 — Cut has a real ViewModel command now, but reaching a TextBox is still
/// view mechanics no ViewModel should hold a reference for. These pin the two
/// halves that make that split actually work: MainWindow's window-level GotFocus
/// handler keeps CanCutFocusedText in sync with whatever last gained focus, and
/// the command still performs the cut through the existing static helper.
/// </summary>
[Collection("AvaloniaTests")]
public class CutFocusedTextWiringTests
{
    [FixedAvaloniaFact]
    public async Task FocusingAnEditableTextBox_EnablesCut_AndFocusingAwayDisablesIt()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.CanCutFocusedText.Should().BeFalse("nothing editable has focus yet");

            vm.IsSearchVisible = true;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var searchBox = window.FindControl<TextBox>("SearchTextBox")!;
            searchBox.Focus();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.CanCutFocusedText.Should().BeTrue("the focused control is an editable TextBox");

            searchBox.Text = "abcdef";
            searchBox.SelectionStart = 1;
            searchBox.SelectionEnd = 3;
            vm.CutFocusedTextCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            searchBox.Text.Should().Be("adef", "the command asked the view to cut the focused box's selection");

            window.FindControl<Button>("ToolbarOutlineButton")!.Focus();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.CanCutFocusedText.Should().BeFalse("focus moved to a non-editable control");
        }
        finally
        {
            window.Close();
        }
    }
}
