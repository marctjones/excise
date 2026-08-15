using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Given the decoded text of a content-stream text operation, finds which
/// page-level extracted <see cref="Letter"/> objects correspond to each
/// character. This is the bridge between content-stream-level redaction
/// (which works with operator text) and pixel-space redaction (which needs
/// per-glyph bounding boxes).
/// </summary>
/// <remarks>
/// <para>
/// Matching is done by text-content search rather than spatial position.
/// Content-stream-parser positions drift from extracted letter positions by
/// a few points, so position-only matching misidentifies words. Text matching
/// is rotation-independent; the letter order <see cref="TextExtractor"/>
/// produces already corresponds to reading order even when the page is
/// rotated 90/180/270 degrees. When the same text appears multiple times
/// on a page we disambiguate by preferring the match whose letters form
/// the tightest spatial cluster.
/// </para>
/// <para>
/// Ported from Excise.App.Redaction.GlyphLevel.LetterFinder. The CJK /
/// per-glyph-byte tracking branches are deferred until
/// <see href="https://github.com/marctjones/excise/issues/281"/> lands a
/// richer text-operation representation; simple fonts with one byte per
/// character work with the current shape.
/// </para>
/// </remarks>
public class LetterFinder
{
    /// <summary>
    /// Locate the letters that correspond to <paramref name="operationText"/>.
    /// </summary>
    /// <param name="operationText">The decoded text of a Tj/TJ/'/&quot; operation.</param>
    /// <param name="allLetters">All letters extracted from the page in reading order.</param>
    /// <returns>
    /// One <see cref="LetterMatch"/> per character in <paramref name="operationText"/>
    /// that could be matched to a letter. Empty when the text isn't present on the page.
    /// </returns>
    public List<LetterMatch> FindOperationLetters(
        string operationText,
        IReadOnlyList<Letter> allLetters)
        => FindOperationLetters(operationText, allLetters, preferredBounds: null);

    /// <summary>
    /// Locate an operation's letters, using the parser's bounds to distinguish
    /// repeated text runs on the same page.
    /// </summary>
    internal List<LetterMatch> FindOperationLetters(
        string operationText,
        IReadOnlyList<Letter> allLetters,
        PdfRectangle? preferredBounds)
    {
        var matches = new List<LetterMatch>();
        if (string.IsNullOrEmpty(operationText) || allLetters.Count == 0)
            return matches;

        // Reading-order letters from the extractor preserve the rotation-correct
        // sequence, so concatenating their .Value gives us a searchable string.
        var characterToLetter = new List<int>(allLetters.Count);
        var textBuilder = new System.Text.StringBuilder(allLetters.Count);
        for (var letterIndex = 0; letterIndex < allLetters.Count; letterIndex++)
        {
            var value = allLetters[letterIndex].Value;
            textBuilder.Append(value);
            for (var charIndex = 0; charIndex < value.Length; charIndex++)
                characterToLetter.Add(letterIndex);
        }
        var fullPageText = textBuilder.ToString();

        var foundIndices = FindAllTextOccurrences(fullPageText, operationText);

        // Fallback for form-field rows: the content stream often contains more or
        // fewer trailing fill chars (underscores, dashes) than the extractor sees
        // because TJ kerning collapses them differently. Strip to the meaningful
        // prefix and match on that.
        if (foundIndices.Count == 0 &&
            (operationText.Contains('_') || operationText.Contains('-')))
        {
            var meaningful = ExtractMeaningfulText(operationText);
            if (!string.IsNullOrEmpty(meaningful) && meaningful.Length >= 3)
            {
                foundIndices = FindAllTextOccurrences(fullPageText, meaningful);
                if (foundIndices.Count > 0)
                    operationText = meaningful;
            }
        }

        // RTL bridge (#632): the extractor reorders visual-order Arabic/Hebrew
        // runs into LOGICAL order, while the operator's decoded text stays in
        // raw content-stream (visual) order. When the raw text contains
        // strong-RTL characters and the direct search failed, search for the
        // logically-reordered form and pair each operation character with the
        // letter it moved to under the same permutation.
        int[]? rtlPermutation = null;
        if (foundIndices.Count == 0 && BidiReorderer.ContainsStrongRtl(operationText))
        {
            var permutation = BidiReorderer.BuildRtlRunPermutation(operationText);
            var logicalText = BidiReorderer.ReverseRtlRunsInString(operationText);
            foundIndices = FindAllTextOccurrences(fullPageText, logicalText);
            if (foundIndices.Count > 0)
            {
                rtlPermutation = permutation;
            }
            else
            {
                // The primary permutation joins digit islands to RTL runs
                // (#632); the letter-side reorder decides that from the WHOLE
                // line, which this operator's text alone cannot see (strong-
                // LTR in another operator on the same line, split operators).
                // Fall back to the digits-stay-put permutation.
                var conservative =
                    BidiReorderer.ReverseRtlRunsInStringWithoutDigitJoining(operationText);
                if (conservative != logicalText)
                {
                    foundIndices = FindAllTextOccurrences(fullPageText, conservative);
                    if (foundIndices.Count > 0)
                        rtlPermutation = BidiReorderer
                            .BuildRtlRunPermutationWithoutDigitJoining(operationText);
                }
            }
        }

        if (foundIndices.Count == 0)
            return matches;

        // Repeated short runs are common in professionally generated PDFs.
        // Text-only coherence picks the same occurrence for every identical
        // operator (a one-character run is always an exact tie), so redacting
        // one occurrence can remove matching operators across the page (#942).
        // The content parser already knows this operator's bounds; use them to
        // select the occurrence nearest the operator that actually drew it.
        int matchIndex = foundIndices.Count == 1
            ? foundIndices[0]
            : preferredBounds is { } bounds
                ? FindBestMatchByBounds(
                    foundIndices, allLetters, characterToLetter, operationText.Length, bounds)
                : FindBestMatchByCoherence(
                    foundIndices, allLetters, characterToLetter, operationText.Length);

        int count = Math.Min(operationText.Length, fullPageText.Length - matchIndex);
        for (int i = 0; i < count; i++)
        {
            // Under an RTL permutation, operation character i landed at page-
            // letter offset permutation[i]; identity otherwise.
            int offset = rtlPermutation != null && rtlPermutation[i] < count
                ? rtlPermutation[i]
                : i;
            var pageCharacterIndex = matchIndex + offset;
            if (pageCharacterIndex < 0 || pageCharacterIndex >= characterToLetter.Count)
                continue;
            var letter = allLetters[characterToLetter[pageCharacterIndex]];
            matches.Add(new LetterMatch
            {
                CharacterIndex = i,
                Letter = letter,
                // Carry the glyph's original source bytes so a Type0/CID font's
                // kept text can be re-encoded with its real codes rather than
                // Unicode (which the font can't render). (Issue #353)
                RawBytes = EncodeCharacterCode(letter),
            });
        }
        return matches;
    }

    private static int FindBestMatchByBounds(
        List<int> candidates,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<int> characterToLetter,
        int textLength,
        PdfRectangle preferredBounds)
    {
        int best = candidates[0];
        double bestScore = double.MaxValue;
        var target = preferredBounds.Normalize();

        foreach (var idx in candidates)
        {
            if (idx + textLength > characterToLetter.Count) continue;

            var firstLetter = characterToLetter[idx];
            var lastLetter = characterToLetter[idx + textLength - 1];
            var candidate = BoundsOf(letters, firstLetter, lastLetter - firstLetter + 1);
            var dx = CenterX(candidate) - CenterX(target);
            var dy = CenterY(candidate) - CenterY(target);
            var dw = candidate.Width - target.Width;
            var dh = candidate.Height - target.Height;
            var score = dx * dx + dy * dy + 0.25 * (dw * dw + dh * dh);

            if (score < bestScore)
            {
                bestScore = score;
                best = idx;
            }
        }

        return best;
    }

    private static PdfRectangle BoundsOf(
        IReadOnlyList<Letter> letters,
        int start,
        int count)
    {
        double left = double.MaxValue, bottom = double.MaxValue;
        double right = double.MinValue, top = double.MinValue;
        for (int i = start; i < start + count; i++)
        {
            var r = letters[i].GlyphRectangle.Normalize();
            left = Math.Min(left, r.Left);
            bottom = Math.Min(bottom, r.Bottom);
            right = Math.Max(right, r.Right);
            top = Math.Max(top, r.Top);
        }
        return new PdfRectangle(left, bottom, right, top);
    }

    private static double CenterX(PdfRectangle r) => (r.Left + r.Right) * 0.5;
    private static double CenterY(PdfRectangle r) => (r.Bottom + r.Top) * 0.5;

    /// <summary>
    /// Re-encode a letter's <see cref="Letter.CharacterCode"/> to its original
    /// source bytes (big-endian, <see cref="Letter.CodeByteLength"/> wide). For
    /// Identity-H/V CID fonts this is a 2-byte code; for simple fonts a single
    /// byte. Reconstruction preserves these bytes instead of guessing how to
    /// encode the decoded Unicode through a custom font.
    /// </summary>
    private static byte[] EncodeCharacterCode(Letter letter)
    {
        int len = letter.CodeByteLength < 1 ? 1 : letter.CodeByteLength;
        int code = letter.CharacterCode;
        var bytes = new byte[len];
        for (int b = len - 1; b >= 0; b--)
        {
            bytes[b] = (byte)(code & 0xFF);
            code >>= 8;
        }
        return bytes;
    }

    private static List<int> FindAllTextOccurrences(string haystack, string needle)
    {
        var indices = new List<int>();
        int start = 0;
        while (true)
        {
            int idx = haystack.IndexOf(needle, start, StringComparison.Ordinal);
            if (idx < 0)
                idx = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            indices.Add(idx);
            start = idx + 1;
        }
        return indices;
    }

    /// <summary>
    /// Of several candidate start indices, returns the one whose letters form
    /// the tightest bounding box — a proxy for "these glyphs were drawn as a
    /// unit." The match with the smallest total spread wins, with Y-spread
    /// weighted ~10x to prefer same-baseline runs.
    /// </summary>
    private static int FindBestMatchByCoherence(
        List<int> candidates,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<int> characterToLetter,
        int textLength)
    {
        int best = candidates[0];
        double bestScore = double.MaxValue;

        foreach (var idx in candidates)
        {
            if (idx + textLength > characterToLetter.Count) continue;

            var firstLetter = characterToLetter[idx];
            var lastLetter = characterToLetter[idx + textLength - 1];

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = firstLetter; i <= lastLetter; i++)
            {
                var r = letters[i].GlyphRectangle;
                // Normalize: rotated text can arrive with swapped Left/Right or Bottom/Top.
                var lx = Math.Min(r.Left, r.Right);
                var rx = Math.Max(r.Left, r.Right);
                var by = Math.Min(r.Bottom, r.Top);
                var ty = Math.Max(r.Bottom, r.Top);
                if (lx < minX) minX = lx;
                if (rx > maxX) maxX = rx;
                if (by < minY) minY = by;
                if (ty > maxY) maxY = ty;
            }

            var xSpread = maxX - minX;
            var ySpread = maxY - minY;
            var score = xSpread + ySpread * 11; // +1 so Y strictly dominates when equal
            if (score < bestScore)
            {
                bestScore = score;
                best = idx;
            }
        }
        return best;
    }

    /// <summary>
    /// For a form-field row like <c>FULL NAME AT BIRTH: ________________</c>,
    /// returns just <c>FULL NAME AT BIRTH:</c> — the part before the fill run.
    /// The extractor and parser frequently disagree on the underscore count
    /// because of TJ-kerning rounding, so the "meaningful prefix" gives us a
    /// robust secondary anchor.
    /// </summary>
    private static string ExtractMeaningfulText(string operationText)
    {
        if (string.IsNullOrEmpty(operationText))
            return operationText;

        // Find the first run of 3+ identical fill characters.
        for (int i = 0; i + 2 < operationText.Length; i++)
        {
            char c = operationText[i];
            if ((c == '_' || c == '-') &&
                operationText[i + 1] == c &&
                operationText[i + 2] == c)
            {
                return i == 0 ? operationText : operationText.Substring(0, i).TrimEnd();
            }
        }
        return operationText;
    }
}
