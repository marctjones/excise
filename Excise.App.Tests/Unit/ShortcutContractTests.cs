using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Automation;
using Excise.App.Services.Input;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Excise.Core.Automation;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1640 keyboard-shortcut audit, headless. The registry (Excise.Core.Automation), the window's
/// resolver and the menu gestures are three statements of the same shortcut table; this holds them
/// to one another instead of to a human's memory of the Keyboard Shortcuts sheet.
/// </summary>
[Collection("AvaloniaTests")]
public class ShortcutContractTests
{
    private readonly ITestOutputHelper _out;
    public ShortcutContractTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// Chords the registry shares between commands on purpose, each in a context that tells them apart:
    /// Enter finds while the search box has focus and applies a redaction while redaction mode is on
    /// (<see cref="WindowShortcutResolver"/> guards the latter with the mode and not-in-a-text-box).
    /// </summary>
    private static readonly HashSet<string> ContextSharedChords = new(StringComparer.Ordinal) { "Enter" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoTwoCommandsShareAChord(bool mac)
    {
        var byChord = PdfCommandRegistry.All
            .Select(c => (Command: c, Chord: CommandShortcutPolicy.GetChord(c, mac)))
            .Where(x => !string.IsNullOrEmpty(x.Chord))
            .GroupBy(x => x.Chord!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 && !ContextSharedChords.Contains(g.Key))
            .Select(g => g.Key + " => " + string.Join(", ", g.Select(x => x.Command.Id)))
            .ToList();
        byChord.Should().BeEmpty("one chord must mean one command on this platform");
    }

    [FixedAvaloniaFact]
    public void EveryMenuGesture_ReachesTheCommandItIsAttachedTo()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var items = window.GetLogicalDescendants().OfType<MenuItem>()
                .Where(m => m.InputGesture is not null).ToList();
            items.Should().NotBeEmpty();

            var problems = new List<string>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var header = (item.Header?.ToString() ?? "?").Replace("_", "");
                var id = item.GetValue(CommandAccessibility.CommandIdProperty);

                // The text the user reads in the menu, not KeyGesture.ToString() (that prints raw
                // key names such as "Ctrl+OemComma" and "Ctrl+D0"; the template formats them).
                item.ApplyTemplate();
                var key = item.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Name == "PART_InputGestureText")?.Text;
                key.Should().NotBeNullOrEmpty($"'{header}' renders its gesture");

                // 1. No two menu items carry the same gesture.
                key = key!;
                if (seen.TryGetValue(key, out var other))
                    problems.Add($"{key} is on both '{other}' and '{header}'");
                else
                    seen[key] = header;

                // 2. A gesture the menu shows is one the registry (the documented table) agrees with.
                if (id is not null)
                {
                    PdfCommandRegistry.TryGet(id, out var meta).Should().BeTrue($"'{header}' names command '{id}'");
                    var documented = meta!.Shortcut;
                    if (documented is null)
                        problems.Add($"'{header}' shows {key} but the registry gives '{id}' no shortcut");
                    else if (!SameChord(documented, key))
                        problems.Add($"'{header}' shows {key} but the registry says {documented}");
                }
                else
                {
                    problems.Add($"'{header}' ({key}) has no CommandId, so the registry cannot vouch for it");
                }
            }

            _out.WriteLine(string.Join("\n", problems));
            problems.Should().BeEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A shortcut the menu advertises must be one the window actually handles: Alt+F4 belongs to the
    /// OS and Ctrl+X to the focused text box, every other gesture resolves to a window shortcut when
    /// the mode that enables it is on (Return only applies a redaction in redaction mode, Ctrl+C only
    /// copies in text-selection mode).
    /// </summary>
    [FixedAvaloniaFact]
    public void EveryMenuGesture_IsHandledByTheWindowShortcutResolver()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var context = new WindowShortcutContext(
                SearchVisible: true, RedactionMode: true, TextSelectionMode: true,
                TextBoxFocused: false, ComboBoxFocused: false);
            var unhandled = window.GetLogicalDescendants().OfType<MenuItem>()
                .Where(m => m.InputGesture is KeyGesture)
                .Select(m => (Header: (m.Header?.ToString() ?? "?").Replace("_", ""), Gesture: (KeyGesture)m.InputGesture!))
                .Where(x => x.Header is not ("Exit" or "Cut"))
                .Where(x => WindowShortcutResolver.Resolve(x.Gesture.Key, x.Gesture.KeyModifiers, context) == WindowShortcut.None)
                .Select(x => $"{x.Header} ({x.Gesture})")
                .ToList();
            unhandled.Should().BeEmpty("a gesture shown in the menu must do something when pressed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The other direction: a chord the registry documents for a command must reach it. Alt+F4 is the
    /// OS's and Ctrl+X belongs to the focused text box.
    /// </summary>
    [Fact]
    public void EveryRegistryChord_IsHandledByTheWindowShortcutResolver()
    {
        var context = new WindowShortcutContext(
            SearchVisible: true, RedactionMode: true, TextSelectionMode: true,
            TextBoxFocused: false, ComboBoxFocused: false);
        var unhandled = new List<string>();
        foreach (var command in PdfCommandRegistry.All.Where(c => !string.IsNullOrEmpty(c.Shortcut)))
        {
            if (command.Shortcut is "Alt+F4" or "Ctrl+X") continue;   // the OS and the focused text box own these
            var chord = command.Shortcut!.Replace(" ", "").Replace("Esc", "Escape").Replace("Enter", "Return");
            // Avalonia's parser wants key names: "," and "+" are OemComma/OemPlus, digits D0..D9.
            chord = chord.Replace("Ctrl+,", "Ctrl+OemComma").Replace("Ctrl++", "Ctrl+OemPlus")
                .Replace("Ctrl+-", "Ctrl+OemMinus");
            chord = System.Text.RegularExpressions.Regex.Replace(chord, @"\+(\d)$", "+D$1");
            var gesture = KeyGesture.Parse(chord);
            if (WindowShortcutResolver.Resolve(gesture.Key, gesture.KeyModifiers, context) == WindowShortcut.None)
                unhandled.Add($"{command.Id} ({command.Shortcut})");
        }
        unhandled.Should().BeEmpty("a chord the registry documents must do something when pressed");
    }

    private static bool SameChord(string registry, string gesture)
    {
        static string Norm(string s) => s.Replace("Esc", "Escape").Replace("OemComma", ",")
            .Replace("OemPlus", "+").Replace("OemMinus", "-").Replace("Return", "Enter").ToLowerInvariant();
        return Norm(registry) == Norm(gesture);
    }
}
