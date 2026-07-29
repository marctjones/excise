using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Helper for asserting what an INDEPENDENT extractor (mutool) recovered from an
/// RTL fixture, tolerant of the one thing that legitimately varies between mupdf
/// builds: bidi presentation DIRECTION.
///
/// mupdf's RTL text output is build-dependent, and the two builds excise's CI
/// runs on disagree:
///   • macOS / homebrew mutool emits LOGICAL order — سلام (U+0633 0644 0627 0645).
///   • Linux / apt mupdf-tools emits VISUAL order — for a plain CID run that is
///     the exact reverse, مالس (U+0645 0627 0644 0633); where an Arabic lam-alef
///     ligature is involved the ligature decomposes (NFKC) to logical-order
///     lam+alef embedded in the otherwise-reversed run, ملاس
///     (U+0645 0644 0627 0633) — neither the word nor its clean reverse.
/// All three are CONFORMANT readings that recover the same word.
///
/// For an oracle-SANITY / anti-vacuity pre-check ("the extractor could read the
/// word from the UNREDACTED fixture, so its later absence means something") the
/// invariant that survives every build is the base-letter MULTISET: the oracle
/// saw the word's letters, in some order. This helper checks exactly that.
///
/// It is deliberately NOT used for post-redaction LEAK assertions — those stay
/// strict (they check the exact word AND its reverse as substrings), because a
/// leak check must not accept a reordering as "gone".
/// </summary>
internal static class RtlOracleText
{
    /// <summary>
    /// Sorted multiset of <paramref name="s"/>'s base LETTERS after NFKC folding
    /// (which reduces Arabic shaped/presentation forms and ligatures to base
    /// letters). Joiners, combining marks, whitespace and punctuation are
    /// dropped, so only letter identity — not order or shaping — remains.
    /// </summary>
    private static List<char> LetterKey(string s)
        => s.Normalize(NormalizationForm.FormKC)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) is
                        UnicodeCategory.OtherLetter
                        or UnicodeCategory.LowercaseLetter
                        or UnicodeCategory.UppercaseLetter
                        or UnicodeCategory.ModifierLetter)
            .OrderBy(c => c)
            .ToList();

    /// <summary>
    /// True if <paramref name="oracleReading"/> recovered every base letter of
    /// <paramref name="word"/> (multiset containment) — order- and
    /// shaping-independent, so it holds whether the mupdf build reads RTL in
    /// logical or visual order.
    /// </summary>
    internal static bool Recovered(string? oracleReading, string word)
    {
        if (oracleReading is null) return false;
        var pool = LetterKey(oracleReading);
        foreach (var ch in LetterKey(word))
            if (!pool.Remove(ch))
                return false;
        return true;
    }
}
