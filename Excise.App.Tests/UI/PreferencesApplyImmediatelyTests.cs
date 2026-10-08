using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2000 — Preferences has no Save button: changes apply as they are made and
/// whatever the window shows when it closes is what applies and persists. The
/// redaction profile is the case that matters: a switch to Maximum that silently
/// failed to apply would leave every later redaction on the weaker Standard
/// profile while the user believes otherwise.
/// </summary>
[Collection("AvaloniaTests")]
public class PreferencesApplyImmediatelyTests
{
    private sealed class Harness
    {
        public required MainWindowViewModel Main { get; init; }
        public required PreferencesViewModel Prefs { get; init; }
        public required PreferencesWindow Window { get; init; }
        public int Applies { get; set; }
    }

    private static async Task<Harness> OpenAsync()
    {
        var main = MainWindowViewModelTestFactory.Create();
        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        Harness? h = null;
        prefs.ApplyRequested = () =>
        {
            h!.Applies++;
            main.ApplySavedPreferences(prefs);
        };
        var window = new PreferencesWindow { DataContext = prefs };
        h = new Harness { Main = main, Prefs = prefs, Window = window };
        window.Show();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        return h;
    }

    private static async Task ChooseNextProfileByKeyboard(PreferencesWindow window)
    {
        var combo = window.FindControl<ComboBox>("RedactionProfileComboBox")!;
        combo.Focus();
        combo.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout)
        {
            await Task.Delay(50);
            await KeyboardTestHelpers.FlushDispatcherAsync();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingTheWindow_AppliesTheLastChange_EvenBeforeItSettles()
    {
        var h = await OpenAsync();
        h.Main.RedactionPreferences.Profile.Should().Be(RedactionProfile.Standard);

        await ChooseNextProfileByKeyboard(h.Window);
        h.Prefs.RedactionProfile.Should().Be(RedactionProfile.Maximum, "the keypress must reach the bound control");
        h.Window.HasPendingApply.Should().BeTrue("the change is still settling");

        h.Window.Close();   // no button: the window's own close
        await KeyboardTestHelpers.FlushDispatcherAsync();

        h.Main.RedactionPreferences.Profile.Should().Be(RedactionProfile.Maximum,
            "what the window showed when it closed is what applies");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ARedactionChange_AppliesWhileTheWindowIsStillOpen()
    {
        var h = await OpenAsync();
        try
        {
            await ChooseNextProfileByKeyboard(h.Window);
            await WaitUntil(() => h.Main.RedactionPreferences.Profile == RedactionProfile.Maximum,
                PreferencesWindow.ApplyDelay * 10);

            h.Main.RedactionPreferences.Profile.Should().Be(RedactionProfile.Maximum,
                "the profile applies once the change settles, without closing the window");
            h.Applies.Should().Be(1, "one change, one apply");
        }
        finally
        {
            h.Window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task NothingApplies_AfterTheWindowCloses_OrFromTheMemoryReadout()
    {
        var h = await OpenAsync();
        h.Prefs.RefreshMemoryReadout();
        await Task.Delay(PreferencesWindow.ApplyDelay * 2);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        h.Applies.Should().Be(0, "the live memory readout reports state; it is not a preference");

        h.Window.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        var afterClose = h.Applies;

        h.Prefs.RedactionWholeWord = true;
        await Task.Delay(PreferencesWindow.ApplyDelay * 2);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        h.Applies.Should().Be(afterClose, "no timer outlives the window");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ResetToDefaults_AsksFirst_AndADeclineChangesNothing()
    {
        var h = await OpenAsync();
        try
        {
            h.Prefs.RedactionProfile = RedactionProfile.Maximum;
            var answer = false;
            h.Prefs.ConfirmReset = () => Task.FromResult(answer);

            await h.Prefs.ResetToDefaultsCommand.Execute();
            h.Prefs.RedactionProfile.Should().Be(RedactionProfile.Maximum, "declining the confirmation resets nothing");

            answer = true;
            await h.Prefs.ResetToDefaultsCommand.Execute();
            h.Prefs.RedactionProfile.Should().Be(RedactionProfile.Standard);
        }
        finally
        {
            h.Window.Close();
        }
    }
}
