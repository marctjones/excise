using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Excise.App.Tests.UI.InteractionCoverage;

/// <summary>
/// One interactive affordance in a real window, with an identity stable enough
/// to be written to a checked-in expectations file (#1021 follow-up).
///
/// <para><b>Why an identity scheme at all.</b> Only 29 of roughly 180 declared
/// interactive elements in <c>MainWindow.axaml</c> carry an <c>x:Name</c>, and
/// a positional logical path renames every sibling the moment someone inserts a
/// menu separator. So the key is, in order of preference: the ViewModel command
/// PROPERTY name the control is bound to, then <c>x:Name</c>, then header/content
/// text. The first is what a reader recognises and what survives moving a button
/// from a toolbar to a menu.</para>
///
/// <para>This type is shared by the ENUMERATOR (which builds the denominator
/// from a real window) and the RECORDER (which names an element that just
/// received a synthetic input event). They must agree character for character or
/// the coverage ratio is silently zero — so there is one implementation, not
/// two. That is the same rule the content-stream walker is under: one walk,
/// many sinks.</para>
/// </summary>
public sealed record GuiInteractiveElement(string Surface, string Kind, string Name)
{
    /// <summary>The line written to, and read from, the coverage artifacts.</summary>
    public string Id => $"{Surface}/{Kind}:{Name}";

    public override string ToString() => Id;
}

/// <summary>
/// Builds the DENOMINATOR: every interactive affordance reachable in a
/// constructed window, and every declared keyboard gesture.
/// </summary>
public static class GuiInteractiveElementInventory
{
    /// <summary>
    /// Every interactive element under <paramref name="root"/>, keyed as
    /// described on <see cref="GuiInteractiveElement"/>.
    /// </summary>
    public static IReadOnlyList<GuiInteractiveElement> Enumerate(TopLevel root)
    {
        var surface = root.GetType().Name;
        var commandNames = GuiInteractionNaming.CommandNameMap(root.DataContext);
        var found = new List<GuiInteractiveElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Two unnamed ComboBoxes in one window collapsed into a single row and
        // silently shrank the denominator by one — an under-report, the bad
        // direction. Anything still unnamed after the command-name and text
        // fallbacks gets an ordinal so it at least counts. That ordinal IS
        // positional and will shift if a sibling is inserted; giving the control
        // an x:Name in the .axaml is how to make it stable, and the "<unnamed"
        // in the id is the signpost to do so.
        var unnamedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(GuiInteractiveElement e)
        {
            if (e.Name == GuiInteractionNaming.Unnamed)
            {
                unnamedCounts.TryGetValue(e.Kind, out var n);
                unnamedCounts[e.Kind] = n + 1;
                e = e with { Name = $"{GuiInteractionNaming.Unnamed}#{n + 1}" };
            }
            if (seen.Add(e.Id)) found.Add(e);
        }

        foreach (var node in Descendants(root))
        {
            if (node is not Control control) continue;

            var described = GuiInteractionNaming.Describe(control, surface, commandNames);
            if (described != null) Add(described);

            // Keyboard gestures are declared on menu items (28 of them) rather
            // than as window KeyBindings, so they are only discoverable here.
            if (node is MenuItem { InputGesture: { } gesture })
                Add(new GuiInteractiveElement(surface, "Gesture", GuiInteractionNaming.Format(gesture)));
        }

        return found;
    }

    private static IEnumerable<ILogical> Descendants(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.LogicalChildren) stack.Push(child);
        }
    }
}

/// <summary>
/// The single naming implementation shared by the enumerator and the recorder.
/// </summary>
public static class GuiInteractionNaming
{
    /// <summary>
    /// Classify and name <paramref name="control"/>, or null when it is not an
    /// interactive affordance (a panel, a label, a template part).
    /// </summary>
    public static GuiInteractiveElement? Describe(
        Control control, string surface, IReadOnlyDictionary<ICommand, string> commandNames)
    {
        var kind = Classify(control);
        if (kind == null) return null;

        // A control materialised by an ItemsControl's DataTemplate is ONE
        // affordance repeated per item, not N affordances. Enumerating a
        // 160-page document's thumbnail list otherwise produced 455 distinct
        // "unnamed CheckBox" rows and pushed the denominator from 161 to 617 —
        // an instrument reporting the size of the test fixture, not the size of
        // the GUI.
        //
        // The discriminator is ItemsSource, not Items: a static menu declares
        // its children, so MenuItems keep their own identities, while the
        // recent-files list, thumbnail strip, search results and clipboard
        // history are all bound and collapse correctly.
        var owner = DataDrivenItemsOwner(control);
        if (owner != null)
            return new GuiInteractiveElement(
                surface, "ItemTemplate",
                $"{(string.IsNullOrEmpty(owner.Name) ? owner.GetType().Name : owner.Name)}/{kind}");

        var name = CommandName(control, commandNames)
                   ?? (string.IsNullOrEmpty(control.Name) ? null : control.Name)
                   ?? ContentText(control)
                   ?? Unnamed;

        return new GuiInteractiveElement(surface, kind, name);
    }

    /// <summary>
    /// The interactive KIND, or null for a non-interactive control.
    ///
    /// <para>Order matters: <see cref="CheckBox"/>, <see cref="RadioButton"/>
    /// and <see cref="ToggleSwitch"/> all derive from <see cref="ToggleButton"/>
    /// which derives from <see cref="Button"/>, and <see cref="MenuItem"/>
    /// derives from <see cref="HeaderedSelectingItemsControl"/>. The most
    /// specific test has to come first or every toggle reads as a Button.</para>
    /// </summary>
    private static string? Classify(Control control) => control switch
    {
        // A submenu header is a container, not an affordance you can complete an
        // action with; its children are enumerated separately.
        MenuItem mi when mi.Items.Count > 0 || mi.ItemsSource != null => null,
        MenuItem mi when mi.ToggleType != MenuItemToggleType.None => "ToggleMenuItem",
        MenuItem => "MenuItem",
        CheckBox => "CheckBox",
        RadioButton => "RadioButton",
        ToggleSwitch => "ToggleSwitch",
        ToggleButton => "ToggleButton",
        Button b when b.Flyout != null => "FlyoutButton",
        Button => "Button",
        ComboBox => "ComboBox",
        Slider => "Slider",
        NumericUpDown => "NumericUpDown",
        TextBox => "TextBox",
        TreeView => "TreeView",
        ListBox => "ListBox",
        TabItem => "TabItem",
        _ => null,
    };

    /// <summary>
    /// The nearest ancestor <see cref="ItemsControl"/> that is DATA-BOUND, or
    /// null when this control is declared in XAML rather than generated.
    /// </summary>
    private static ItemsControl? DataDrivenItemsOwner(Control control)
    {
        for (var node = control.GetLogicalParent() as Control; node != null;
             node = node.GetLogicalParent() as Control)
        {
            if (node is ItemsControl { ItemsSource: not null } ic) return ic;
        }
        return null;
    }

    /// <summary>Placeholder for a control with no command, no x:Name and no text.</summary>
    public const string Unnamed = "<unnamed>";

    /// <summary>
    /// Format a gesture from its parts rather than via
    /// <see cref="KeyGesture.ToString"/>, which renders Ctrl+0, Ctrl+1 and
    /// Ctrl+2 as "Ctrl", "Ctrl+Back" and "Ctrl+Cancel" — unreadable, and the
    /// first is not even distinguishable from a bare modifier.
    /// </summary>
    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(gesture.Key.ToString());
        return string.Join("+", parts);
    }

    private static string? CommandName(Control control, IReadOnlyDictionary<ICommand, string> names)
    {
        var command = control switch
        {
            MenuItem mi => mi.Command,
            Button b => b.Command,
            _ => null,
        };
        return command != null && names.TryGetValue(command, out var n) ? n : null;
    }

    private static string? ContentText(Control control) => control switch
    {
        MenuItem mi => Flatten(mi.Header),
        ContentControl cc => Flatten(cc.Content),
        _ => null,
    };

    private static string? Flatten(object? content) => content switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Replace('\t', ' ').Trim(),
        TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text) => tb.Text!.Trim(),
        _ => null,
    };

    /// <summary>
    /// ViewModel command property name for every <see cref="ICommand"/> the
    /// DataContext exposes. Cached per DataContext instance: the recorder needs
    /// it on every recorded event and reflection over the VM is not free.
    /// </summary>
    public static IReadOnlyDictionary<ICommand, string> CommandNameMap(object? dataContext)
    {
        if (dataContext == null) return EmptyMap;

        lock (MapCacheLock)
        {
            if (MapCache.TryGetValue(dataContext, out var cached)) return cached;

            var map = new Dictionary<ICommand, string>();
            foreach (var p in dataContext.GetType().GetProperties())
            {
                if (!typeof(ICommand).IsAssignableFrom(p.PropertyType)) continue;
                object? value;
                try { value = p.GetValue(dataContext); }
                catch { continue; }
                if (value is ICommand cmd) map[cmd] = p.Name;
            }

            MapCache.Add(dataContext, map);
            return map;
        }
    }

    private static readonly IReadOnlyDictionary<ICommand, string> EmptyMap =
        new Dictionary<ICommand, string>();
    private static readonly object MapCacheLock = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<ICommand, string>>
        MapCache = new();
}
