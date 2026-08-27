using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Scrubs text carriers (/ActualText, /Alt, /E) carried INLINE in the content
/// stream as a marked-content property list — <c>/Span &lt;&lt;/ActualText (SECRET)&gt;&gt; BDC …
/// EMC</c> (§14.9.4) — when the span encloses redacted glyphs or its value
/// restates a removed word.
///
/// <para><b>Why this is separate from <see cref="StructureTreeRedactionScrubber"/>
/// (#1182).</b> That one handles <c>/ActualText</c> on <c>StructElem</c> objects in
/// the structure tree. The IDENTICAL text can also sit in a <c>BDC</c>/<c>DP</c>
/// property dictionary in the content stream itself — a different carrier the
/// structure-tree walk never reaches. Glyph removal rewrites the text-show
/// operators but passes <c>BDC</c> through verbatim, so the inline <c>/ActualText</c>
/// survived, and excise reported <c>IsCleanSuccess = true</c> over a file whose
/// "redacted" name an accessibility-aware reader (mutool <c>-A</c>, a screen
/// reader) recovers straight out of the marked content.</para>
///
/// <para><b>The signal is enclosure, not the carrier's own MCID or a text match
/// (#1185).</b> /ActualText's whole purpose is to substitute text that DIFFERS
/// from the painted glyphs (a ligature glyph → "fi"), so content-matching the
/// removed VISIBLE word against the carrier value misses it. And the carrier BDC
/// frequently has no MCID of its own — the MCID sits on an enclosing span. So a
/// carrier is scrubbed when the glyphs it ENCLOSES fall in the redaction area:
/// precise (a sibling span whose glyphs were not touched is left alone) and
/// nesting-aware. Content-matching stays as a second signal for a carrier that
/// does restate a removed word without enclosing it.</para>
///
/// <para>Runs on the FINAL operator list, after glyph/image removal and after
/// Form XObject flattening — so inline carriers inlined from a flattened form are
/// covered too. Mutates the inline property dictionaries in place; they are not
/// shared objects (an inline <c>BDC</c> dict belongs to the one operator).</para>
///
/// <para><b>Not covered:</b> the NAMED property-list form
/// (<c>/Span /P1 BDC</c> resolving through <c>/Resources /Properties</c>). That
/// dictionary can be shared across spans, so scrubbing it positionally could
/// over-remove; left as a follow-up on #1182.</para>
/// </summary>
internal static class MarkedContentCarrierScrubber
{
    /// <summary>
    /// Scrub inline marked-content carriers in <paramref name="ops"/> — the
    /// PRE-removal content operators — for a single redaction <paramref name="area"/>.
    /// Must run BEFORE the glyph pass: it needs the glyph bounding boxes to see
    /// which spans covered the area, and it mutates the BDC/DP property dicts in
    /// place so the mutation flows through the glyph pass into the written stream.
    /// </summary>
    /// <returns>True if any carrier entry was removed.</returns>
    public static bool Scrub(IReadOnlyList<ContentOperator> ops, PdfPage page, PdfRectangle area)
    {
        // Analysis and mutation run on the SAME list: `ops` is the pre-removal
        // content, so it still carries the glyph bounding boxes that tell us which
        // marked-content spans covered the area, and it is the list whose BDC
        // property dicts we mutate in place before the glyph pass consumes it.
        var affectedSpans = CollectAffectedCarrierSpans(ops, area);
        var removedText = StructureTreeRedactionScrubber.CollectRemovedText(page, area);
        return Scrub(ops, affectedSpans, removedText, page.Document);
    }

    internal static bool Scrub(
        IReadOnlyList<ContentOperator> ops,
        HashSet<ContentOperator> affectedSpans,
        IReadOnlyCollection<string> removedText,
        PdfDocument doc)
    {
        var removedAny = false;

        foreach (var op in ops)
        {
            if (op.Name is not ("BDC" or "DP")) continue;

            var props = op.Operands.OfType<PdfDictionary>().FirstOrDefault();
            if (props == null) continue;

            var enclosesRemovedGlyphs = affectedSpans.Contains(op);

            foreach (var carrier in StructureTreeRedactionScrubber.TextCarriers)
            {
                if (!props.ContainsKey(carrier)) continue;

                // /ActualText/Alt/E may be an indirect string (#1155).
                var value = (doc.Resolve(props.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;

                var restatesRemovedText = value != null && removedText.Any(t =>
                    t.Length >= StructureTreeRedactionScrubber.MinMatchLength &&
                    value.Contains(t, System.StringComparison.Ordinal));

                if (enclosesRemovedGlyphs || restatesRemovedText)
                {
                    props.Remove(carrier);
                    removedAny = true;
                }
            }
        }

        return removedAny;
    }

    /// <summary>
    /// The set of carrier-bearing BDC operators (by reference) whose enclosed
    /// glyphs intersect <paramref name="area"/>. Walks the marked-content nesting
    /// so a carrier span with no MCID of its own is still caught when an enclosing
    /// or nested glyph falls in the redaction region.
    /// </summary>
    private static HashSet<ContentOperator> CollectAffectedCarrierSpans(
        IReadOnlyList<ContentOperator> ops, PdfRectangle area)
    {
        var affected = new HashSet<ContentOperator>();
        var stack = new Stack<ContentOperator?>();   // the BDC op opening each span (null for BMC)

        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "BMC":
                    stack.Push(null);
                    break;

                case "BDC":
                    stack.Push(HasTextCarrier(op) ? op : null);
                    break;

                case "EMC":
                    if (stack.Count > 0) stack.Pop();
                    break;

                default:
                    if (op.BoundingBox is not { } box || !Intersects(box, area)) continue;
                    // Every enclosing carrier span covers glyphs being removed here.
                    foreach (var span in stack)
                        if (span != null) affected.Add(span);
                    break;
            }
        }

        return affected;
    }

    private static bool HasTextCarrier(ContentOperator bdc)
    {
        var props = bdc.Operands.OfType<PdfDictionary>().FirstOrDefault();
        return props != null &&
               StructureTreeRedactionScrubber.TextCarriers.Any(props.ContainsKey);
    }

    private static bool Intersects(PdfRectangle a, PdfRectangle b) =>
        a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;
}
