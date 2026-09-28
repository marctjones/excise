namespace Excise.App.Services;

/// <summary>
/// Writes a shortcut chord the way the platform's users read it (#1806): macOS
/// shows modifier glyphs run together (<c>⌘⇧S</c>), Windows and Linux show
/// <c>Ctrl+Shift+S</c>. The one place that knows the glyphs; the platform is a
/// parameter so both spellings are testable on any host.
/// </summary>
/// <remarks>
/// Chords are written in a neutral form: <c>Primary</c> is the
/// platform's main command modifier (Cmd on macOS, Ctrl elsewhere, the way the
/// menu accelerators are bound); <c>Ctrl</c> is the literal Control key, which
/// stays Control on macOS (the tab-switching shortcuts); <c>Shift</c> and
/// <c>Alt</c> (Option on macOS) are themselves.
/// </remarks>
internal static class ShortcutNotation
{
    private static readonly (string Name, string Mac, string Other)[] Modifiers =
    [
        ("Primary", "⌘", "Ctrl+"),
        ("Ctrl", "⌃", "Ctrl+"),
        ("Alt", "⌥", "Alt+"),
        ("Shift", "⇧", "Shift+"),
    ];

    /// <summary>Formats one chord, e.g. <c>Primary+Shift+S</c> or <c>Primary++</c> (Cmd and the plus key).</summary>
    internal static string Format(string chord, bool mac)
    {
        var rest = chord;
        var text = new System.Text.StringBuilder();
        while (TryStripModifier(ref rest, out var modifier))
            text.Append(mac ? modifier.Mac : modifier.Other);
        return text.Append(rest).ToString();
    }

    private static bool TryStripModifier(ref string chord, out (string Name, string Mac, string Other) modifier)
    {
        foreach (var candidate in Modifiers)
        {
            if (chord.StartsWith(candidate.Name + "+", System.StringComparison.Ordinal))
            {
                chord = chord[(candidate.Name.Length + 1)..];
                modifier = candidate;
                return true;
            }
        }
        modifier = default;
        return false;
    }
}
