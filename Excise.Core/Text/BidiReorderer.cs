using System;
using System.Collections.Generic;

namespace Excise.Core.Text;

/// <summary>
/// Restores LOGICAL character order for right-to-left (Arabic/Hebrew/Syriac)
/// runs in the extracted letter sequence. See issue #632.
/// </summary>
/// <remarks>
/// <para>
/// PDF content streams carry RTL text in one of two orders, and only geometry
/// says which:
/// </para>
/// <list type="bullet">
///   <item><b>Visual order</b> (the overwhelmingly common case — single
///   <c>Tj</c>/<c>TJ</c> per line): glyphs are emitted left-to-right with
///   positive advances, so the byte sequence is the REVERSE of the logical
///   character order. Raw stream-order extraction yields reversed text, which
///   a user's logical-order search string can never match — and
///   <c>RedactText</c> then silently removes nothing (the #637 failure mode:
///   reports success, leaves the name in the file).</item>
///   <item><b>Logical order</b> (producers that position every glyph
///   explicitly): codes appear in logical order and successive X positions
///   DECREASE. Stream order is already correct and must not be touched.</item>
/// </list>
/// <para>
/// The rule implemented here is the same geometric one mutool applies
/// (verified against mutool 1.27 on both stream orders): find each maximal
/// same-line run of strong-RTL letters (neutrals such as spaces and
/// punctuation join a run only between two strong-RTL letters), and reverse
/// the run when its X positions ascend — i.e. when stream order is visual
/// order. Descending-X runs are already logical and pass through unchanged.
/// </para>
/// <para>
/// This is deliberately NOT a full Unicode Bidirectional Algorithm: digits
/// (European and Arabic-Indic — both bidi-weak) and strong-LTR letters
/// terminate a run, so each RTL word still comes out logically ordered and
/// searchable, but the relative placement of numbers inside a mixed-direction
/// line is not re-derived. Full UBA remains scoped under #632.
/// </para>
/// </remarks>
internal static class BidiReorderer
{
    /// <summary>
    /// Same-line tolerance in points; matches the line-break heuristic in
    /// <see cref="TextExtractor.BuildWords"/>.
    /// </summary>
    private const double SameLineToleranceY = 5.0;

    /// <summary>
    /// Reverse, in place, every maximal same-line strong-RTL run whose X
    /// positions ascend (stream order = visual order), producing logical
    /// order. Runs whose X positions descend are already logical and are
    /// left untouched.
    /// </summary>
    internal static void ReorderVisualRtlRuns(List<Letter> letters)
    {
        int i = 0;
        while (i < letters.Count)
        {
            if (!IsStrongRtlLetter(letters[i]))
            {
                i++;
                continue;
            }

            // Extend the run: strong-RTL letters, plus neutrals strictly
            // BETWEEN two strong-RTL letters (end stays on the last strong
            // one, so trailing neutrals never join). Any other character —
            // Latin letters, digits — terminates the run.
            int end = i;
            int j = i + 1;
            while (j < letters.Count && IsSameLine(letters[j - 1], letters[j]))
            {
                if (IsStrongRtlLetter(letters[j])) { end = j; j++; }
                else if (IsNeutralLetter(letters[j])) { j++; }
                else break;
            }

            // Ascending X ⇒ the stream painted the run left-to-right, i.e.
            // visual order; reversing yields logical order. Descending X ⇒
            // already logical (glyphs positioned right-to-left explicitly).
            if (end > i && letters[end].StartX > letters[i].StartX)
                letters.Reverse(i, end - i + 1);

            i = end + 1;
        }
    }

    /// <summary>
    /// Apply the same run-reversal to a plain string (no geometry available,
    /// so ascending X — the visual-order case — is assumed). Used to bridge
    /// between raw content-stream operator text (visual order) and the
    /// logically-ordered page letter sequence: applying this to an operator's
    /// decoded text reproduces what <see cref="ReorderVisualRtlRuns"/> did to
    /// its letters.
    /// </summary>
    internal static string ReverseRtlRunsInString(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var map = BuildRtlRunPermutation(text);
        var result = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
            result[map[i]] = text[i];
        return new string(result);
    }

    /// <summary>
    /// Permutation for <see cref="ReverseRtlRunsInString"/>: element <c>i</c>
    /// is the index the character at <c>i</c> moves to. Characters outside
    /// RTL runs map to themselves.
    /// </summary>
    internal static int[] BuildRtlRunPermutation(string text)
    {
        var map = new int[text.Length];
        for (int k = 0; k < text.Length; k++) map[k] = k;

        int i = 0;
        while (i < text.Length)
        {
            if (!IsStrongRtlChar(text[i]))
            {
                i++;
                continue;
            }

            int end = i;
            int j = i + 1;
            while (j < text.Length)
            {
                if (IsStrongRtlChar(text[j])) { end = j; j++; }
                else if (IsNeutralChar(text[j])) { j++; }
                else break;
            }

            for (int k = i; k <= end; k++)
                map[k] = i + end - k;

            i = end + 1;
        }

        return map;
    }

    /// <summary>True when any character of <paramref name="text"/> is a strong-RTL character.</summary>
    internal static bool ContainsStrongRtl(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsStrongRtlChar(c)) return true;
        return false;
    }

    private static bool IsStrongRtlLetter(Letter letter)
    {
        if (string.IsNullOrEmpty(letter.Value)) return false;
        foreach (var c in letter.Value)
            if (IsStrongRtlChar(c)) return true;
        return false;
    }

    private static bool IsNeutralLetter(Letter letter)
    {
        if (string.IsNullOrEmpty(letter.Value)) return false;
        foreach (var c in letter.Value)
            if (!IsNeutralChar(c)) return false;
        return true;
    }

    /// <summary>
    /// Strong-RTL scalar: the U+0590–U+08FF stretch (Hebrew, Arabic, Syriac,
    /// Arabic Supplement, Thaana, NKo, Samaritan, Mandaic, Arabic Extended)
    /// minus the Arabic-Indic digit ranges (bidi class AN, not R/AL), plus
    /// the Hebrew and Arabic presentation-form blocks.
    /// </summary>
    internal static bool IsStrongRtlChar(char c)
    {
        if (c >= '\u0660' && c <= '\u0669') return false; // Arabic-Indic digits (bidi AN)
        if (c >= '\u06F0' && c <= '\u06F9') return false; // Extended Arabic-Indic digits (bidi AN)
        if (c >= '\u0590' && c <= '\u08FF') return true;  // Hebrew ... Arabic Extended
        if (c >= '\uFB1D' && c <= '\uFB4F') return true;  // Hebrew presentation forms
        if (c >= '\uFB50' && c <= '\uFDFF') return true;  // Arabic presentation forms A
        if (c >= '\uFE70' && c <= '\uFEFF') return true;  // Arabic presentation forms B
        return false;
    }

    private static bool IsNeutralChar(char c) =>
        char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool IsSameLine(Letter a, Letter b) =>
        Math.Abs(a.StartY - b.StartY) <= SameLineToleranceY;
}
