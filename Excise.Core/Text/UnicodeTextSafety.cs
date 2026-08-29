using System;
using System.Globalization;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Display-only handling for Unicode controls in untrusted document text.
///
/// PDF text is prose, not an identifier namespace: callers must preserve it
/// when extracting, selecting, copying, or redacting. This helper is for
/// security-sensitive *presentations* of that text, where invisible controls
/// must not be able to make a preview say something different from its stored
/// character sequence.
/// </summary>
public static class UnicodeTextSafety
{
    /// <summary>
    /// True when text contains a Unicode bidi control. Such a character can
    /// reorder the visual display of nearby text without changing its logical
    /// order (UAX #9), so it deserves a separate user-visible warning.
    /// </summary>
    public static bool ContainsBidiControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
        {
            if (IsBidiControl(c)) return true;
        }

        return false;
    }

    /// <summary>
    /// True when text contains a control or format character which would be
    /// invisible or potentially misleading when displayed literally.
    /// </summary>
    public static bool ContainsPotentiallyMisleadingControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (ShouldMakeVisible(rune)) return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a safe inspection representation. Printable document text is
    /// unchanged; invisible format controls become unambiguous <c>[U+XXXX]
    /// tokens. Line endings and tabs remain readable layout characters.
    /// This is intentionally not a normalization or sanitization routine.
    /// </summary>
    public static string EscapeForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        StringBuilder? escaped = null;
        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            if (!ShouldMakeVisible(rune))
            {
                escaped?.Append(rune.ToString());
                i += rune.Utf16SequenceLength;
                continue;
            }

            escaped ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
            escaped.Append("[U+");
            escaped.Append(rune.Value.ToString(rune.Value <= 0xFFFF ? "X4" : "X6", CultureInfo.InvariantCulture));
            escaped.Append(']');
            i += rune.Utf16SequenceLength;
        }

        return escaped?.ToString() ?? text;
    }

    /// <summary>Whether <paramref name="c"/> has Unicode's Bidi_Control property.</summary>
    public static bool IsBidiControl(char c) => c is
        '\u061C' or // ARABIC LETTER MARK
        '\u200E' or // LEFT-TO-RIGHT MARK
        '\u200F' or // RIGHT-TO-LEFT MARK
        >= '\u202A' and <= '\u202E' or // embeddings and overrides
        >= '\u2066' and <= '\u2069';   // isolates

    private static bool ShouldMakeVisible(Rune rune)
    {
        // Preserve the useful whitespace users expect in copied paragraphs.
        if (rune.Value is '\t' or '\n' or '\r') return false;

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format;
    }
}
