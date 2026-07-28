using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// Builds the macOS application menu — the bold app-name menu that hosts
/// "About Excise" and "Preferences…" (Avalonia appends the standard
/// Services / Hide / Quit items automatically).
///
/// This menu MUST be set on the Application during <see cref="App.Initialize"/>,
/// before Avalonia constructs its one-shot <c>MenuTarget.Application</c> exporter
/// — otherwise the exporter installs its built-in default ("About Avalonia" →
/// AboutAvaloniaDialog) and never re-reads (#834). Because that is earlier than
/// any window, the items cannot capture a view-model; they resolve the current
/// main window's view-model lazily at click time via <paramref name="currentViewModel"/>.
/// </summary>
internal static class MacApplicationMenu
{
    public static NativeMenu Build(Func<MainWindowViewModel?> currentViewModel)
    {
        ArgumentNullException.ThrowIfNull(currentViewModel);

        var menu = new NativeMenu();

        var about = new NativeMenuItem("About Excise");
        about.Click += (_, _) => Invoke(currentViewModel, vm => vm.AboutCommand);
        menu.Add(about);

        menu.Add(new NativeMenuItemSeparator());

        var preferences = new NativeMenuItem("Preferences…")
        {
            Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta)
        };
        preferences.Click += (_, _) => Invoke(currentViewModel, vm => vm.ShowPreferencesCommand);
        menu.Add(preferences);

        return menu;
    }

    private static void Invoke(
        Func<MainWindowViewModel?> currentViewModel,
        Func<MainWindowViewModel, ICommand?> pick)
    {
        if (currentViewModel() is { } vm
            && pick(vm) is { } command
            && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
