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
/// The base rule: find each maximal same-line run of strong-RTL letters
/// (neutrals such as spaces and punctuation join a run only between two
/// members), and reverse the run when its X positions ascend — i.e. when
/// stream order is visual order. Descending-X runs are already logical and
/// pass through unchanged.
/// </para>
/// <para>
/// <b>Digit islands (UAX #9 weak types, #632 second slice).</b> Digits are
/// bidi-WEAK: inside an RTL line a number renders left-to-right while the
/// words around it run right-to-left, so a visual-order stream carries the
/// line's segments in reverse logical order but each number's digits in
/// logical order. On lines whose strong characters are all RTL (the UAX #9
/// P2/P3 first-strong rule collapses to "no strong-LTR present"), digit
/// islands — including single common/European separators between digits,
/// rule W4 — therefore JOIN the reversible run as order-preserving blocks:
/// the run's segments reverse, the number's digits do not. Without this,
/// logical "عمر 30 سنة" extracted as "سنة 30 عمر" and a phrase needle
/// spanning the number (ID lines, dates, phone numbers) silently evaded
/// <c>RedactText</c>. When a strong-LTR character shares the line, digits
/// bind to the LTR context (rule W7) and the pre-existing conservative rule
/// (digits terminate runs) applies unchanged. Expected orderings are
/// spec-derived and verified against the python-bidi UAX #9 reference
/// implementation; NOTE mutool 1.27 is NOT an oracle for digit placement —
/// its bidi is a per-run heuristic that leaves digit islands in stream
/// position and reverses Arabic-Indic digit pairs.
/// </para>
/// <para>
/// This is still deliberately NOT a full Unicode Bidirectional Algorithm:
/// lines mixing strong-LTR and strong-RTL words fall back to per-run
/// reversal, and explicit bidi controls (LRE/RLE/LRI/RLI/PDF) are not
/// honoured. Full UBA remains scoped under #632.
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
    /// UAX #9 bidi character classes, collapsed to what the reorderer needs.
    /// </summary>
    private enum BidiClass : byte
    {
        /// <summary>Strong RTL (bidi classes R and AL).</summary>
        StrongRtl,
        /// <summary>Digits (bidi classes EN and AN).</summary>
        Digit,
        /// <summary>Whitespace, punctuation, symbols (neutral/weak).</summary>
        Neutral,
        /// <summary>Everything else — strong-LTR letters, CJK, unclassified.</summary>
        Other,
    }

    /// <summary>
    /// Reorder, in place, every maximal same-line strong-RTL run whose X
    /// positions ascend (stream order = visual order), producing logical
    /// order. Runs whose X positions descend are already logical and are
    /// left untouched. On lines with no strong-LTR content, digit islands
    /// join the run as order-preserving blocks (see class remarks).
    /// </summary>
    internal static void ReorderVisualRtlRuns(List<Letter> letters)
    {
        int n = letters.Count;
        if (n == 0) return;

        var classes = new BidiClass[n];
        for (int i = 0; i < n; i++) classes[i] = Classify(letters[i]);

        // Process one line segment at a time: pairwise same-line chaining,
        // identical to the run-extension rule this replaces.
        int segStart = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i == n || !IsSameLine(letters[i - 1], letters[i]))
            {
                ReorderLineSegment(letters, classes, segStart, i - 1);
                segStart = i;
            }
        }
    }

    private static void ReorderLineSegment(
        List<Letter> letters, BidiClass[] classes, int segStart, int segEnd)
    {
        bool anyRtl = false;
        bool joinDigits = true;
        for (int k = segStart; k <= segEnd; k++)
        {
            if (classes[k] == BidiClass.StrongRtl) anyRtl = true;
            else if (classes[k] == BidiClass.Other) joinDigits = false;
        }
        if (!anyRtl) return;

        int i = segStart;
        while (i <= segEnd)
        {
            var c = classes[i];
            bool starter = c == BidiClass.StrongRtl ||
                           (joinDigits && c == BidiClass.Digit);
            if (!starter)
            {
                i++;
                continue;
            }

            // Extend the run: strong-RTL letters (and digits, when joining),
            // plus neutrals strictly BETWEEN two members (end stays on the
            // last member, so trailing neutrals never join). Any other
            // character terminates the run.
            int start = i, end = i;
            bool hasRtl = c == BidiClass.StrongRtl;
            int j = i + 1;
            while (j <= segEnd)
            {
                var cj = classes[j];
                if (cj == BidiClass.StrongRtl) { end = j; hasRtl = true; j++; }
                else if (joinDigits && cj == BidiClass.Digit) { end = j; j++; }
                else if (cj == BidiClass.Neutral) { j++; }
                else break;
            }

            // Ascending X ⇒ the stream painted the run left-to-right, i.e.
            // visual order; reordering yields logical order. Descending X ⇒
            // already logical (glyphs positioned right-to-left explicitly).
            // Digit-only stretches (hasRtl false) are never touched.
            if (hasRtl && end > start && letters[end].StartX > letters[start].StartX)
            {
                var map = BuildRunReversalMap(
                    start, end,
                    k => classes[k],
                    k => IsNumberSeparatorLetter(letters[k]));
                var copy = letters.GetRange(start, end - start + 1);
                for (int k = start; k <= end; k++)
                    letters[map[k - start]] = copy[k - start];
            }

            i = end + 1;
        }
    }

    /// <summary>
    /// Apply the same run reordering to a plain string (no geometry
    /// available, so ascending X — the visual-order case — is assumed, and
    /// the whole string is treated as one line). Used to bridge between raw
    /// content-stream operator text (visual order) and the logically-ordered
    /// page letter sequence: applying this to an operator's decoded text
    /// reproduces what <see cref="ReorderVisualRtlRuns"/> did to its letters.
    /// </summary>
    internal static string ReverseRtlRunsInString(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ApplyPermutation(text, BuildRtlRunPermutation(text));
    }

    /// <summary>
    /// Like <see cref="ReverseRtlRunsInString(string)"/> but with the
    /// pre-digit-island rule: digits always terminate runs. The letter-side
    /// reorder decides digit joining from the WHOLE line's content, which a
    /// single operator's text cannot see (strong-LTR text elsewhere on the
    /// line, split operators), so callers matching operator text should try
    /// the primary form first and fall back to this one.
    /// </summary>
    internal static string ReverseRtlRunsInStringWithoutDigitJoining(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ApplyPermutation(text, BuildRtlRunPermutationWithoutDigitJoining(text));
    }

    /// <summary>
    /// Permutation for <see cref="ReverseRtlRunsInString(string)"/>: element
    /// <c>i</c> is the index the character at <c>i</c> moves to. Characters
    /// outside RTL runs map to themselves. Digit islands join the runs as
    /// order-preserving blocks unless the text contains strong-LTR content
    /// (mirroring the line-level rule in <see cref="ReorderVisualRtlRuns"/>).
    /// </summary>
    internal static int[] BuildRtlRunPermutation(string text) =>
        BuildStringPermutation(text, joinDigits: !ContainsOther(text));

    /// <summary>
    /// Permutation with the pre-digit-island rule (digits terminate runs);
    /// see <see cref="ReverseRtlRunsInStringWithoutDigitJoining"/>.
    /// </summary>
    internal static int[] BuildRtlRunPermutationWithoutDigitJoining(string text) =>
        BuildStringPermutation(text, joinDigits: false);

    private static int[] BuildStringPermutation(string text, bool joinDigits)
    {
        var map = new int[text.Length];
        for (int k = 0; k < text.Length; k++) map[k] = k;

        int i = 0;
        while (i < text.Length)
        {
            var c = Classify(text[i]);
            bool starter = c == BidiClass.StrongRtl ||
                           (joinDigits && c == BidiClass.Digit);
            if (!starter)
            {
                i++;
                continue;
            }

            int start = i, end = i;
            bool hasRtl = c == BidiClass.StrongRtl;
            int j = i + 1;
            while (j < text.Length)
            {
                var cj = Classify(text[j]);
                if (cj == BidiClass.StrongRtl) { end = j; hasRtl = true; j++; }
                else if (joinDigits && cj == BidiClass.Digit) { end = j; j++; }
                else if (cj == BidiClass.Neutral) { j++; }
                else break;
            }

            if (hasRtl && end > start)
            {
                var runMap = BuildRunReversalMap(
                    start, end,
                    k => Classify(text[k]),
                    k => IsNumberSeparatorChar(text[k]));
                for (int k = start; k <= end; k++)
                    map[k] = runMap[k - start];
            }

            i = end + 1;
        }

        return map;
    }

    /// <summary>
    /// Destination map for one visual-order run [<paramref name="start"/>..
    /// <paramref name="end"/>]: the run is mirrored, except that each digit
    /// island — a maximal stretch of digits, absorbing a single number
    /// separator flanked by digits (UAX #9 rule W4) — moves as a block with
    /// its internal order preserved. Element <c>k - start</c> is the absolute
    /// index the element at <c>k</c> moves to. With no digits present this is
    /// a pure mirror, i.e. exactly the old whole-run reversal.
    /// </summary>
    private static int[] BuildRunReversalMap(
        int start, int end, Func<int, BidiClass> classAt, Func<int, bool> isSeparatorAt)
    {
        var map = new int[end - start + 1];
        int k = start;
        while (k <= end)
        {
            if (classAt(k) == BidiClass.Digit)
            {
                int islandStart = k, islandEnd = k;
                int j = k + 1;
                while (j <= end)
                {
                    if (classAt(j) == BidiClass.Digit) { islandEnd = j; j++; }
                    else if (isSeparatorAt(j) && j + 1 <= end && classAt(j + 1) == BidiClass.Digit)
                    {
                        islandEnd = j + 1;
                        j += 2;
                    }
                    else break;
                }

                int destStart = start + end - islandEnd;
                for (int m = islandStart; m <= islandEnd; m++)
                    map[m - start] = destStart + (m - islandStart);
                k = islandEnd + 1;
            }
            else
            {
                map[k - start] = start + end - k;
                k++;
            }
        }

        return map;
    }

    private static string ApplyPermutation(string text, int[] map)
    {
        var result = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
            result[map[i]] = text[i];
        return new string(result);
    }

    /// <summary>True when any character of <paramref name="text"/> is a strong-RTL character.</summary>
    internal static bool ContainsStrongRtl(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsStrongRtlChar(c)) return true;
        return false;
    }

    private static bool ContainsOther(string text)
    {
        foreach (var c in text)
            if (Classify(c) == BidiClass.Other) return true;
        return false;
    }

    private static BidiClass Classify(char c)
    {
        if (IsDigitChar(c)) return BidiClass.Digit;
        if (IsStrongRtlChar(c)) return BidiClass.StrongRtl;
        if (IsNeutralChar(c)) return BidiClass.Neutral;
        return BidiClass.Other;
    }

    private static BidiClass Classify(Letter letter)
    {
        var value = letter.Value;
        if (string.IsNullOrEmpty(value)) return BidiClass.Other;

        // Any strong-RTL scalar makes the letter strong-RTL (multi-char
        // values come from ligature ToUnicode expansions).
        foreach (var c in value)
            if (IsStrongRtlChar(c)) return BidiClass.StrongRtl;

        bool allDigits = true;
        bool allNeutral = true;
        foreach (var c in value)
        {
            if (!IsDigitChar(c)) allDigits = false;
            if (!IsNeutralChar(c)) allNeutral = false;
        }
        if (allDigits) return BidiClass.Digit;
        if (allNeutral) return BidiClass.Neutral;
        return BidiClass.Other;
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

    /// <summary>Digits: European (bidi EN) and Arabic-Indic (bidi AN).</summary>
    private static bool IsDigitChar(char c) =>
        (c >= '0' && c <= '9') ||
        (c >= '\u0660' && c <= '\u0669') ||
        (c >= '\u06F0' && c <= '\u06F9');

    /// <summary>
    /// UAX #9 number separators (classes CS and ES): a single one between
    /// two digits is part of the number (rule W4) and travels with it.
    /// </summary>
    private static bool IsNumberSeparatorChar(char c) =>
        c == '.' || c == ',' || c == ':' || c == '/' || c == '+' || c == '-';

    private static bool IsNumberSeparatorLetter(Letter letter) =>
        letter.Value is { Length: 1 } v && IsNumberSeparatorChar(v[0]);

    private static bool IsNeutralChar(char c) =>
        char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool IsSameLine(Letter a, Letter b) =>
        Math.Abs(a.StartY - b.StartY) <= SameLineToleranceY;
}
