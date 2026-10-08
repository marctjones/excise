using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2005 — field labels are linked to their fields: clicking a label focuses
/// the field it names. Access keys (Alt+letter) are Windows-only, so on other
/// platforms no label text carries the underscore marker (on macOS Option+letter
/// types characters and must not be captured).
/// </summary>
[Collection("AvaloniaTests")]
public class LinkedLabelTests
{
    public static TheoryData<string> Windows() => new() { "Bates", "Security", "MakeSearchable", "LinuxPrint", "Preferences" };

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
        "LinuxPrint" => new LinuxPrintDialog
        {
            DataContext = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 4),
        },
        "Preferences" => new PreferencesWindow { DataContext = new PreferencesViewModel() },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(Windows))]
    public async Task ClickingALabel_FocusesItsField(string name)
    {
        // Under the app's real styles (#1996): Controls.axaml makes the whole label
        // clickable, which the bare TestApp theme does not.
        var window = Create(name);
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.Styles.Add(new FluentAvaloniaTheme());
        var resources = new ResourceInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Brushes.axaml") };
        global::Avalonia.Application.Current!.Resources.MergedDictionaries.Add(resources);
        window.Styles.Add(new StyleInclude(new Uri("avares://Excise.App/"))
            { Source = new Uri("avares://Excise.App/Styles/Controls.axaml") });
        window.Show();
        try
        {
            // Open every collapsed section so its labels can be clicked too.
            foreach (var expander in window.GetLogicalDescendants().OfType<Expander>())
                expander.IsExpanded = true;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var labels = window.GetLogicalDescendants().OfType<Label>().Where(l => l.Target != null).ToList();
            labels.Should().NotBeEmpty($"{name} links its field labels");

            // Independent oracle: the field a label names is the next input after it
            // in document order (each label sits above or beside its own field).
            // Reading the expectation off label.Target alone would accept a label
            // linked to the wrong field.
            var order = window.GetLogicalDescendants()
                .Where(c => c is Label { Target: not null } or TextBox or ComboBox or NumericUpDown or CheckBox)
                .Cast<Control>().ToList();
            foreach (var label in labels)
            {
                var target = (Control)label.Target!;
                var next = order.Skip(order.IndexOf(label) + 1).FirstOrDefault(c => c is not Label);
                target.Should().BeSameAs(next,
                    $"'{label.Content}' must name the field that follows it ({next?.Name}), not {target.Name}");
                target.IsEffectivelyVisible.Should().BeTrue($"{label.Content} targets a field the user can see");
                if (!label.IsEffectivelyVisible)
                    continue; // e.g. Current password only shows on an encrypted document
                label.BringIntoView();
                await KeyboardTestHelpers.FlushDispatcherAsync();
                window.Focus();
                var point = global::Avalonia.VisualExtensions.TranslatePoint(label, new global::Avalonia.Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await KeyboardTestHelpers.FlushDispatcherAsync();

                var focused = window.FocusManager?.GetFocusedElement() as Control;
                (focused == target || focused?.GetLogicalAncestors().Contains(target) == true || IsTemplatePartOf(focused, target))
                    .Should().BeTrue($"clicking '{label.Content}' must focus {target.Name}, focused {focused?.Name ?? focused?.GetType().Name}");

                if (!OperatingSystem.IsWindows())
                    (label.Content?.ToString() ?? "").Should().NotContain("_",
                        "access keys are Windows-only; the marker must not reach other platforms");
            }
        }
        finally
        {
            window.Close();
            global::Avalonia.Application.Current!.Resources.MergedDictionaries.Remove(resources);
        }
    }

    private static bool IsTemplatePartOf(Control? focused, Control target)
    {
        for (var c = focused; c != null; c = c.TemplatedParent as Control)
            if (c == target)
                return true;
        return false;
    }
}
