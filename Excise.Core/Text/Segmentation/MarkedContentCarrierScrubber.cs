using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Scrubs text carriers (/ActualText, /Alt, /E) carried INLINE in the content
/// stream as a marked-content property list — <c>/Span &lt;&lt;/ActualText (SECRET)&gt;&gt; BDC …
/// EMC</c> (§14.9.4) — when the span covers redacted content or the carrier still
/// spells a removed word.
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
/// <para>Runs on the FINAL operator list, after glyph/image removal and after
/// Form XObject flattening — so inline carriers inlined from a flattened form are
/// covered too. Mutates the inline property dictionaries in place; they are not
/// shared objects (an inline <c>BDC</c> dict belongs to the one operator).</para>
///
/// <para><b>Not covered:</b> the NAMED property-list form
/// (<c>/Span /P1 BDC</c> resolving through <c>/Resources /Properties</c>). That
/// dictionary can be shared across spans, so scrubbing it positionally could
/// over-remove; left as a follow-up on #1182. Inline is the form <c>/ActualText</c>
/// almost always takes.</para>
/// </summary>
internal static class MarkedContentCarrierScrubber
{
    /// <summary>
    /// Scrub inline marked-content carriers in <paramref name="ops"/> for a single
    /// redaction <paramref name="area"/>. <paramref name="page"/> must still describe
    /// the pre-removal content (words + marked-content structure) — call BEFORE the
    /// letters are gone, i.e. pass the same page state the glyph pass read.
    /// </summary>
    /// <returns>True if any carrier entry was removed.</returns>
    public static bool Scrub(IReadOnlyList<ContentOperator> ops, PdfPage page, PdfRectangle area)
    {
        var removedText = StructureTreeRedactionScrubber.CollectRemovedText(page, area);
        var affectedMcids = StructureTreeRedactionScrubber.CollectAffectedMcids(page, area);
        return Scrub(ops, removedText, affectedMcids, page.Document);
    }

    internal static bool Scrub(
        IReadOnlyList<ContentOperator> ops,
        IReadOnlyCollection<string> removedText,
        HashSet<int> affectedMcids,
        PdfDocument doc)
    {
        var removedAny = false;

        foreach (var op in ops)
        {
            if (op.Name is not ("BDC" or "DP")) continue;

            // The inline property list is a dictionary operand: /Tag << … >> BDC.
            var props = op.Operands.OfType<PdfDictionary>().FirstOrDefault();
            if (props == null) continue;

            // Which marked-content span is this? An MCID in the affected set means
            // the span covers content the redaction is removing (§14.7.4.3), so its
            // alternate text describes what is being deleted even if it does not
            // spell it verbatim.
            var mcid = StructureTreeRedactionScrubber.ExtractMcid(op);
            var structural = mcid is { } id && affectedMcids.Contains(id);

            foreach (var carrier in StructureTreeRedactionScrubber.TextCarriers)
            {
                if (!props.ContainsKey(carrier)) continue;

                // /ActualText/Alt/E may be stored as an indirect string (#1155).
                var value = (doc.Resolve(props.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;

                var restatesRemovedText = value != null && removedText.Any(t =>
                    t.Length >= StructureTreeRedactionScrubber.MinMatchLength &&
                    value.Contains(t, System.StringComparison.Ordinal));

                if (structural || restatesRemovedText)
                {
                    props.Remove(carrier);
                    removedAny = true;
                }
            }
        }

        return removedAny;
    }
}
