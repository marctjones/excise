using Avalonia.Input;

namespace Excise.App.Services.Input;

// Matching only: commands, focus changes and event consumption stay in the view. See #1962.
internal readonly record struct WindowShortcutContext(
    bool SearchVisible, bool RedactionMode, bool TextSelectionMode, bool TextBoxFocused, bool ComboBoxFocused);

internal enum WindowShortcut
{
    None,
    ToggleOutline,
    ToggleThumbnails,
    OpenFile,
    SaveFile,
    SaveAs,
    CloseDocument,
    Print,
    ExportCurrentPage,
    ShowPreferences,
    ShowShortcuts,
    ToggleSearch,
    FindNext,
    FindPrevious,
    CloseSearch,
    ApplyRedaction,
    RotatePageLeft,
    ToggleRedactionMode,
    RotatePageRight,
    ZoomActualSize,
    ZoomFitWidth,
    ZoomFitPage,
    ZoomIn,
    ZoomOut,
    ToggleTextSelectionMode,
    NextPage,
    PreviousPage,
    FirstPage,
    LastPage,
    Undo,
    Redo,
    SelectAllText,
    ToggleContinuousView,
    CopyText
}

internal static class WindowShortcutResolver
{
    private const KeyModifiers Control = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Unmodified = Control | Shift | KeyModifiers.Alt;

    // Ordered legacy precedence. Required/forbidden masks intentionally allow extra modifiers:
    // e.g. Ctrl+Alt+O opens, Meta+R toggles, but Ctrl+Shift+O toggles the outline (#369).
    // This is not a replacement for menu InputGesture/interaction-registry authority.
    private static readonly Binding[] Bindings =
    [
        new(Key.O, Control | Shift, 0, WindowShortcut.ToggleOutline),
        new(Key.T, Control | Shift, 0, WindowShortcut.ToggleThumbnails),
        new(Key.O, Control, 0, WindowShortcut.OpenFile),
        new(Key.S, Control, Shift, WindowShortcut.SaveFile),
        new(Key.S, Control | Shift, 0, WindowShortcut.SaveAs),
        new(Key.W, Control, 0, WindowShortcut.CloseDocument),
        new(Key.P, Control, 0, WindowShortcut.Print),
        new(Key.E, Control, 0, WindowShortcut.ExportCurrentPage),
        new(Key.OemComma, Control, 0, WindowShortcut.ShowPreferences),
        new(Key.F1, 0, 0, WindowShortcut.ShowShortcuts),
        new(Key.F, Control, 0, WindowShortcut.ToggleSearch),
        new(Key.F3, 0, Shift, WindowShortcut.FindNext),
        new(Key.F3, Shift, 0, WindowShortcut.FindPrevious),
        new(Key.Escape, 0, 0, WindowShortcut.CloseSearch, Guard.SearchVisible),
        new(Key.Enter, 0, Unmodified, WindowShortcut.ApplyRedaction, Guard.ApplyRedaction),
        new(Key.Return, 0, Unmodified, WindowShortcut.ApplyRedaction, Guard.ApplyRedaction),
        new(Key.L, Control, 0, WindowShortcut.RotatePageLeft),
        new(Key.R, 0, Unmodified, WindowShortcut.ToggleRedactionMode),
        new(Key.R, Control, 0, WindowShortcut.RotatePageRight),
        new(Key.D0, Control, 0, WindowShortcut.ZoomActualSize),
        new(Key.D1, Control, 0, WindowShortcut.ZoomFitWidth),
        new(Key.D2, Control, 0, WindowShortcut.ZoomFitPage),
        new(Key.OemPlus, Control, 0, WindowShortcut.ZoomIn),
        new(Key.Add, Control, 0, WindowShortcut.ZoomIn),
        new(Key.OemMinus, Control, 0, WindowShortcut.ZoomOut),
        new(Key.Subtract, Control, 0, WindowShortcut.ZoomOut),
        new(Key.T, 0, Unmodified, WindowShortcut.ToggleTextSelectionMode, Guard.NotTextBox),
        new(Key.PageDown, 0, 0, WindowShortcut.NextPage),
        new(Key.Down, 0, Control, WindowShortcut.NextPage),
        new(Key.PageUp, 0, 0, WindowShortcut.PreviousPage),
        new(Key.Up, 0, Control, WindowShortcut.PreviousPage),
        new(Key.Home, 0, 0, WindowShortcut.FirstPage),
        new(Key.End, 0, 0, WindowShortcut.LastPage),
        new(Key.Z, Control, Shift, WindowShortcut.Undo, Guard.NotTextBox),
        new(Key.Y, Control, 0, WindowShortcut.Redo, Guard.NotTextBox),
        new(Key.A, Control, Shift, WindowShortcut.SelectAllText, Guard.NotTextBox),
        new(Key.C, Control | Shift, 0, WindowShortcut.ToggleContinuousView, Guard.NotTextBox),
        new(Key.C, Control, Shift, WindowShortcut.CopyText, Guard.TextSelection)
    ];

    internal static WindowShortcut Resolve(Key key, KeyModifiers modifiers, WindowShortcutContext context)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Key == key && (modifiers & binding.Required) == binding.Required &&
                (modifiers & binding.Forbidden) == 0)
                return Allows(binding.Condition, context) ? binding.Action : WindowShortcut.None;
        }
        return WindowShortcut.None;
    }

    private static bool Allows(Guard guard, WindowShortcutContext context) => guard switch
    {
        Guard.SearchVisible => context.SearchVisible,
        Guard.ApplyRedaction => context.RedactionMode && !context.TextBoxFocused && !context.ComboBoxFocused,
        Guard.NotTextBox => !context.TextBoxFocused,
        Guard.TextSelection => context.TextSelectionMode,
        _ => true
    };

    private enum Guard { Always, SearchVisible, ApplyRedaction, NotTextBox, TextSelection }
    private readonly record struct Binding(Key Key, KeyModifiers Required, KeyModifiers Forbidden,
        WindowShortcut Action, Guard Condition = Guard.Always);
}
