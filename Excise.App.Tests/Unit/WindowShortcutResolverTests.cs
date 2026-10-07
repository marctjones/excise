using Avalonia.Input;
using Excise.App.Services.Input;

namespace Excise.App.Tests.Unit;

public class WindowShortcutResolverTests
{
    [Fact]
    public void AllKeysModifiersAndEditorStatesMatchLegacyExceptRemovedModeShortcut()
    {
        Assert.Equal(15, Enum.GetValues<KeyModifiers>().Aggregate(0, (bits, flag) => bits | (int)flag));
        var checkedCases = 0;
        foreach (var key in Enum.GetValues<Key>().Distinct())
        for (var modifiers = 0; modifiers < 16; modifiers++)
        for (var flags = 0; flags < 32; flags++)
        {
            var context = new WindowShortcutContext((flags & 1) != 0, (flags & 2) != 0,
                (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);
            var expected = Legacy(key, (KeyModifiers)modifiers, context);
            // Approved behavior change #1976; keep the frozen legacy oracle intact.
            if (expected == WindowShortcut.ToggleRedactionMode)
                expected = WindowShortcut.None;
            var actual = WindowShortcutResolver.Resolve(key, (KeyModifiers)modifiers, context);
            Assert.True(actual == expected,
                $"{key}, modifiers={modifiers}, context={flags}: expected {expected}, got {actual}");
            checkedCases++;
        }
        Assert.True(checkedCases > 10000);
    }

    // Frozen predicates from pre-refactor MainWindow_KeyDown (8ab3b4e2), not the new binding table.
    // This checks matching; existing headless keyboard-effect tests check dispatch/focus/Handled.
    private static WindowShortcut Legacy(Key key, KeyModifiers modifiers, WindowShortcutContext context)
    {
        if (key == Key.O && modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.ToggleOutline;
        }
        if (key == Key.T && modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.ToggleThumbnails;
        }
        if (key == Key.O && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.OpenFile;
        }
        if (key == Key.S && modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.SaveFile;
        }
        if (key == Key.S && modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.SaveAs;
        }
        if (key == Key.W && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.CloseDocument;
        }
        if (key == Key.P && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.Print;
        }
        if (key == Key.E && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ExportCurrentPage;
        }
        if (key == Key.OemComma && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ShowPreferences;
        }
        if (key == Key.F1)
        {
            return WindowShortcut.ShowShortcuts;
        }
        if (key == Key.F && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ToggleSearch;
        }
        if (key == Key.F3 && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.FindNext;
        }
        if (key == Key.F3 && modifiers.HasFlag(KeyModifiers.Shift))
        {
            return WindowShortcut.FindPrevious;
        }
        if (key == Key.Escape && context.SearchVisible)
        {
            return WindowShortcut.CloseSearch;
        }
        if ((key == Key.Enter || key == Key.Return) &&
            !modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift) &&
            !modifiers.HasFlag(KeyModifiers.Alt) &&
            context.RedactionMode)
        {
            if (context.TextBoxFocused || context.ComboBoxFocused) return WindowShortcut.None;
            return WindowShortcut.ApplyRedaction;
        }
        if (key == Key.L && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.RotatePageLeft;
        }
        if (key == Key.R && !modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Alt))
        {
            return WindowShortcut.ToggleRedactionMode;
        }
        if (key == Key.R && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.RotatePageRight;
        }
        if (key == Key.D0 && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ZoomActualSize;
        }
        if (key == Key.D1 && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ZoomFitWidth;
        }
        if (key == Key.D2 && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ZoomFitPage;
        }
        if ((key == Key.OemPlus || key == Key.Add) && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ZoomIn;
        }
        if ((key == Key.OemMinus || key == Key.Subtract) && modifiers.HasFlag(KeyModifiers.Control))
        {
            return WindowShortcut.ZoomOut;
        }
        if (key == Key.T && !modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Alt))
        {
            if (context.TextBoxFocused) return WindowShortcut.None;
            return WindowShortcut.ToggleTextSelectionMode;
        }
        if (key == Key.PageDown || (key == Key.Down && !modifiers.HasFlag(KeyModifiers.Control)))
        {
            return WindowShortcut.NextPage;
        }
        if (key == Key.PageUp || (key == Key.Up && !modifiers.HasFlag(KeyModifiers.Control)))
        {
            return WindowShortcut.PreviousPage;
        }
        if (key == Key.Home)
        {
            return WindowShortcut.FirstPage;
        }
        if (key == Key.End)
        {
            return WindowShortcut.LastPage;
        }
        if (key == Key.Z && modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (context.TextBoxFocused) return WindowShortcut.None;
            return WindowShortcut.Undo;
        }
        if (key == Key.Y && modifiers.HasFlag(KeyModifiers.Control))
        {
            if (context.TextBoxFocused) return WindowShortcut.None;
            return WindowShortcut.Redo;
        }
        if (key == Key.A && modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (context.TextBoxFocused) return WindowShortcut.None;
            return WindowShortcut.SelectAllText;
        }
        if (key == Key.C && modifiers.HasFlag(KeyModifiers.Control) &&
            modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (context.TextBoxFocused) return WindowShortcut.None;
            return WindowShortcut.ToggleContinuousView;
        }
        if (key == Key.C && modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (!context.TextSelectionMode) return WindowShortcut.None;
            return WindowShortcut.CopyText;
        }
        return WindowShortcut.None;
    }
}
