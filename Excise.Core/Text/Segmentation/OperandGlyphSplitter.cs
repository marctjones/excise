using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1091 — the operand-level TJ-split, the target removal mechanism. It rewrites
/// the OPERAND of a text-showing operator, never the operator's place in the
/// stream:
///
/// <code>
/// (Louise Anne Farrar) Tj   ->   [(Louise Anne ) -2840 ()] TJ
/// </code>
///
/// <para>Same operator slot, same <c>BT</c>/<c>ET</c>, same <c>Tf</c>/<c>Tc</c>/
/// <c>Tw</c>, same positioning — everything Pitfall 2, #1038 and #1039 are about
/// is untouched, because the dangerous restructuring was never the operand, it
/// was removing operators and appending new blocks. Untouched bytes are spliced
/// through VERBATIM; only the matched glyphs' bytes are cut, located by the
/// #1092 per-glyph operand byte offset (so this is correct for Type0/CID, where
/// a decoded index is NOT a byte offset — exactly where <see cref="GlyphBlanker"/>
/// refuses).</para>
///
/// <para><b>#1045 width policy.</b> A removed contiguous run is replaced by ONE
/// advance adjustment equal to the run's TOTAL advance — layout does not shift,
/// but the PER-CHARACTER advances are gone, so the width side-channel a
/// de-redactor reconstructs a word from never leaks. Not per-glyph.</para>
///
/// <para>Returns null (the honest fallback) when it cannot safely split — a
/// match with no byte offset (a synthetic letter), an operator that is not a
/// text show, or an operand shape it does not model.</para>
/// </summary>
internal static class OperandGlyphSplitter
{
    // #1091 instrumentation (test-only): how often the split is ATTEMPTED and how
    // often it actually FIRES, so a measurement can confirm the primary path is
    // used rather than silently falling back to reconstruction on real input.
    internal static long Attempts;
    internal static long Splits;
    internal static void ResetCounters() { Attempts = 0; Splits = 0; }

    public static ContentOperator? TrySplit(ContentOperator op, IReadOnlyList<LetterMatch> toRemove)
    {
        if (toRemove.Count == 0) return null;
        System.Threading.Interlocked.Increment(ref Attempts);

        // Every match must know where its code lives (#1092). A synthetic letter
        // (AcroForm/annotation) has no backing operand byte and returns -1.
        foreach (var m in toRemove)
        {
            if (m.Letter.OperandByteOffset < 0) return null;
            if (m.Letter.CodeByteLength < 1) return null;
        }

        // The operator's string elements, in order. Tj is one element (index 0);
        // TJ is the array, where a string element keeps its own index and the
        // interleaved kerning numbers are passed through.
        List<PdfObject> sourceElements;
        switch (op.Name)
        {
            case "Tj" when op.Operands.Count == 1 && op.Operands[0] is PdfString:
                sourceElements = new List<PdfObject> { op.Operands[0] };
                break;
            case "TJ" when op.Operands.Count == 1 && op.Operands[0] is PdfArray arr:
                sourceElements = arr.ToList();
                break;
            default:
                return null;   // ', " and anything else — not modelled
        }

        // Removals grouped by the element they land in. For a Tj the letters
        // carry TjElementIndex -1; map that onto the single element 0.
        var removalsByElement = new Dictionary<int, List<(int Start, int Len, double Disp)>>();
        foreach (var m in toRemove)
        {
            var el = op.Name == "Tj" ? 0 : m.Letter.TjElementIndex;
            if (el < 0 || el >= sourceElements.Count) return null;   // offset we can't place
            if (sourceElements[el] is not PdfString) return null;    // a number element can't hold glyphs
            if (!removalsByElement.TryGetValue(el, out var list))
                removalsByElement[el] = list = new List<(int, int, double)>();
            list.Add((m.Letter.OperandByteOffset, m.Letter.CodeByteLength, m.Letter.DisplacementThousandths));
        }

        var outElements = new List<PdfObject>(sourceElements.Count + removalsByElement.Count);
        var anyRemoved = false;

        for (var el = 0; el < sourceElements.Count; el++)
        {
            if (sourceElements[el] is not PdfString str || !removalsByElement.TryGetValue(el, out var removals))
            {
                outElements.Add(sourceElements[el]);   // pass through unchanged (kerning number or untouched string)
                continue;
            }

            var bytes = str.Bytes;
            var sorted = removals.OrderBy(r => r.Start).ToList();

            // Validate the ranges sit inside the operand and don't overlap.
            var cursor = 0;
            var newParts = new List<PdfObject>();
            var i = 0;
            var elementRemoved = false;
            while (i < sorted.Count)
            {
                var start = sorted[i].Start;
                if (start < cursor || start + sorted[i].Len > bytes.Length) return null;   // corrupt offset — bail safe

                // Keep the untouched bytes before this removed run, verbatim.
                if (start > cursor)
                    newParts.Add(new PdfString(bytes.AsSpan(cursor, start - cursor).ToArray(), str.IsHex));

                // Gather the maximal CONTIGUOUS removed run (no kept byte between),
                // and sum its advance — #1045: one adjustment, not per glyph.
                double runDisp = 0;
                var runEnd = start;
                while (i < sorted.Count && sorted[i].Start == runEnd)
                {
                    runDisp += sorted[i].Disp;
                    runEnd = sorted[i].Start + sorted[i].Len;
                    i++;
                }

                // A positive TJ number moves the pen LEFT; to RESTORE the removed
                // run's rightward advance the number is negative (§9.4.3).
                newParts.Add(new PdfInteger(-(int)Math.Round(runDisp)));
                cursor = runEnd;
                elementRemoved = true;
            }

            if (!elementRemoved) { outElements.Add(str); continue; }

            // Trailing untouched bytes.
            if (cursor < bytes.Length)
                newParts.Add(new PdfString(bytes.AsSpan(cursor).ToArray(), str.IsHex));
            else
                newParts.Add(new PdfString(Array.Empty<byte>(), str.IsHex));   // the () the split leaves

            outElements.AddRange(newParts);
            anyRemoved = true;
        }

        if (!anyRemoved) return null;

        System.Threading.Interlocked.Increment(ref Splits);
        return new ContentOperator("TJ", new PdfObject[] { new PdfArray(outElements) })
        {
            BoundingBox = op.BoundingBox,
            TextContent = null,   // stale once the codes changed
        };
    }
}
