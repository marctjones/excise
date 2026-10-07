using Excise.Core.Automation;

namespace Excise.App.Services.Input;

// One platform adaptation for native menus, help and shortcut dialogs (#1977).
internal static class CommandShortcutPolicy
{
    internal static string? GetChord(PdfCommandMetadata command, bool mac)
    {
        if (!mac) return command.Shortcut;
        return command.Id switch
        {
            PdfCommandIds.Redo => "Cmd+Shift+Z",
            PdfCommandIds.SearchNext => "Cmd+G",
            PdfCommandIds.SearchPrevious => "Cmd+Shift+G",
            _ => command.Shortcut?.Replace("Ctrl", "Cmd", System.StringComparison.Ordinal)
        };
    }

    internal static string? GetDisplayText(PdfCommandMetadata command, bool mac) =>
        GetChord(command, mac) is { } chord
            ? ShortcutNotation.Format(chord.Replace("Cmd+", "Primary+", System.StringComparison.Ordinal), mac)
            : null;
}
