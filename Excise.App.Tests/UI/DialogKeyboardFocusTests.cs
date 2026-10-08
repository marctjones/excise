using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using FluentAvalonia.Styling;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2007 — keyboard focus in dialogs, under the app's real theme and styles
/// (the default TestApp loads neither; see #1996). Each control the dialog
/// declares is one Tab stop — a number box's spinner buttons are not extra
/// stops — and the stops run in reading order.
/// </summary>
[Collection("AvaloniaTests")]
public class DialogKeyboardFocusTests
{
    public static TheoryData<string> Dialogs() => new() { "Bates", "Security", "MakeSearchable", "ReduceFileSize", "LinuxPrint" };

    private static Window Create(string name) => name switch
    {
        "Bates" => new BatesNumberingDialog { DataContext = new BatesNumberingDialogViewModel() },
        "Security" => new SecurityDialog
        {
            DataContext = new SecurityDialogViewModel(
                true, _ => true, (_, _, _) => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null)),
        },
        "MakeSearchable" => new MakeSearchableDialog
        {
            DataContext = new MakeSearchableDialogViewModel(true, (_, _, _, _) => throw new NotSupportedException()),
        },
        "ReduceFileSize" => new ReduceFileSizeDialog { DataContext = new ReduceFileSizeDialogViewModel() },
        "LinuxPrint" => new LinuxPrintDialog
        {
            DataContext = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 4),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public async Task TabStops_AreDeclaredControls_InReadingOrder(string dialog)
    {
        var window = Create(dialog);
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.Styles.Add(new FluentAvaloniaTheme());
        var resources = new ResourceInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Brushes.axaml") };
        Application.Current!.Resources.MergedDictionaries.Add(resources);
        window.Styles.Add(new StyleInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Controls.axaml") });
        try
        {
            window.Show();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var stops = new List<Control>();
            for (var i = 0; i < 40; i++)
            {
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                await KeyboardTestHelpers.FlushDispatcherAsync();
                if (window.FocusManager?.GetFocusedElement() is not Control focused)
                    continue;
                if (stops.Count > 0 && ReferenceEquals(focused, stops[0]))
                    break; // wrapped round
                stops.Add(focused);
            }

            stops.Should().NotBeEmpty($"{dialog} must be reachable by keyboard");

            // A templated control (a number box) may focus its own editor part,
            // but each declared control is ONE stop: its spinner buttons are not
            // stops of their own.
            var owners = stops.Select(OwnerOf).ToList();
            owners.Should().OnlyHaveUniqueItems(
                $"{dialog}: a control took more than one Tab stop ({string.Join(" → ", stops.Select(Describe))})");

            // Reading order: each stop starts below the previous one, except
            // controls sharing a row (footer buttons), which go left to right.
            var tops = owners.Select(o => o.TranslatePoint(default, window)!.Value).ToList();
            for (var i = 1; i < tops.Count; i++)
            {
                var sameRow = Math.Abs(tops[i].Y - tops[i - 1].Y) < 4;
                (sameRow ? tops[i].X > tops[i - 1].X : tops[i].Y > tops[i - 1].Y).Should().BeTrue(
                    $"{dialog}: Tab went from '{Describe(owners[i - 1])}' to '{Describe(owners[i])}' against reading order");
            }
        }
        finally
        {
            window.Close();
            Application.Current!.Resources.MergedDictionaries.Remove(resources);
        }
    }

    private static Control OwnerOf(Control c)
    {
        var owner = c;
        while (owner.TemplatedParent is Control parent)
            owner = parent;
        return owner;
    }

    private static string Describe(Control c) =>
        c.Name ?? global::Avalonia.Automation.AutomationProperties.GetName(c) ?? c.GetType().Name;
}
