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
/// <para><b>The NAMED property-list form</b> (<c>/Span /P1 BDC</c> resolving
/// through <c>/Resources /Properties</c>, #1599) IS covered, with the sharing
/// hazard the inline form does not have handled explicitly: the dictionary
/// can be referenced by several spans, so it is scrubbed only when EVERY
/// referencing <c>BDC</c> in the content encloses redacted glyphs
/// (<see cref="EveryReferenceIsAffected"/>). When some but not all references
/// are affected, the value is left in place — over-removal into a surviving
/// span's accessibility text would be worse than the leak — and that refusal
/// is reported as a carrier the caller can act on (see
/// <c>PdfDocumentRedactionExtensions.RedactTextCore</c>'s
/// <c>UnscrubbedSharedMarkedContentCarriers</c> handling), never silently
/// dropped.</para>
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
        => Scrub(ops, page, area, out _);

    /// <summary>
    /// As <see cref="Scrub(IReadOnlyList{ContentOperator}, PdfPage, PdfRectangle)"/>,
    /// also naming every NAMED property list (#1599) this call could not scrub
    /// because a span that SURVIVES this redaction still references it — the
    /// over-removal hazard the shared-dictionary check exists to avoid. CLAUDE.md
    /// rule 6: a carrier the engine refuses to touch must be reported, not
    /// silently left behind with a clean-looking result.
    /// </summary>
    public static bool Scrub(
        IReadOnlyList<ContentOperator> ops, PdfPage page, PdfRectangle area,
        out IReadOnlyList<string> unscrubbedSharedCarriers)
    {
        // Analysis and mutation run on the SAME list: `ops` is the pre-removal
        // content, so it still carries the glyph bounding boxes that tell us which
        // marked-content spans covered the area, and it is the list whose BDC
        // property dicts we mutate in place before the glyph pass consumes it.
        var affectedSpans = CollectAffectedCarrierSpans(ops, area, page);
        var removedText = StructureTreeRedactionScrubber.CollectRemovedText(page, area);
        return Scrub(
            ops,
            affectedSpans,
            removedText,
            page.Document,
            out unscrubbedSharedCarriers,
            page.Document.Resolve(page.Resources?.GetOptional("Properties") ?? PdfNull.Instance) as PdfDictionary);
    }

    internal static bool Scrub(
        IReadOnlyList<ContentOperator> ops,
        HashSet<ContentOperator> affectedSpans,
        IReadOnlyCollection<string> removedText,
        PdfDocument doc,
        out IReadOnlyList<string> unscrubbedSharedCarriers,
        PdfDictionary? properties = null)
    {
        var removedAny = false;
        HashSet<string>? shared = null;

        foreach (var op in ops)
        {
            if (op.Name is not ("BDC" or "DP")) continue;

            var props = op.Operands.OfType<PdfDictionary>().FirstOrDefault();
            PdfName? propertyName = null;
            if (props == null)
            {
                // #1599: the NAMED form. The dictionary lives in
                // /Resources /Properties and may be shared, so it is only safe
                // to scrub when no span that survives this redaction still
                // points at it.
                propertyName = NamedPropertyList(op);
                if (propertyName == null) continue;
                props = doc.Resolve(properties?.GetOptional(propertyName.Value) ?? PdfNull.Instance) as PdfDictionary;
                if (props == null) continue;
                if (!EveryReferenceIsAffected(ops, propertyName.Value, affectedSpans))
                {
                    // This span's glyphs are being removed, but the property
                    // list it names is shared with a span that survives — the
                    // over-removal #1182 deferred this over. Report it rather
                    // than let the redaction look clean over a carrier that
                    // still restates the removed text (#1599 acceptance / rule 6).
                    if (affectedSpans.Contains(op))
                        (shared ??= new HashSet<string>(System.StringComparer.Ordinal))
                            .Add(propertyName.Value);
                    continue;
                }
            }

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

        unscrubbedSharedCarriers = shared is { Count: > 0 }
            ? shared.ToList()
            : System.Array.Empty<string>();
        return removedAny;
    }

    /// <summary>
    /// The set of carrier-bearing BDC operators (by reference) whose enclosed
    /// glyphs intersect <paramref name="area"/>. Walks the marked-content nesting
    /// so a carrier span with no MCID of its own is still caught when an enclosing
    /// or nested glyph falls in the redaction region.
    /// </summary>
    private static HashSet<ContentOperator> CollectAffectedCarrierSpans(
        IReadOnlyList<ContentOperator> ops, PdfRectangle area, PdfPage? page = null)
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
                    stack.Push(HasTextCarrier(op, page) ? op : null);
                    break;

                case "EMC":
                    if (stack.Count > 0) stack.Pop();
                    break;

                default:
                    if (op.BoundingBox is not { } box || !box.IntersectsWith(area)) continue;
                    // Every enclosing carrier span covers glyphs being removed here.
                    foreach (var span in stack)
                        if (span != null) affected.Add(span);
                    break;
            }
        }

        return affected;
    }

    /// <summary>
    /// The <c>/Properties</c> key a <c>BDC</c> names, or null when it carries an
    /// inline dictionary (or nothing). §14.6.2: the operands are
    /// <c>tag properties BDC</c>, so the NAME is the second one — the first is
    /// the tag (<c>/Span</c>) and must not be mistaken for it.
    /// </summary>
    private static PdfName? NamedPropertyList(ContentOperator bdc) =>
        bdc.Operands.Count >= 2 ? bdc.Operands[1] as PdfName : null;

    /// <summary>
    /// True when every <c>BDC</c> in this content that names
    /// <paramref name="key"/> encloses removed glyphs (#1599).
    /// </summary>
    /// <remarks>
    /// The shared-dictionary hazard in one predicate. If a span that SURVIVES
    /// this redaction still points at the dictionary, scrubbing it erases that
    /// span's accessibility text too — the over-removal #1182 deferred this
    /// over. When no such span exists, the value belongs solely to content
    /// being removed and can go.
    /// </remarks>
    private static bool EveryReferenceIsAffected(
        IReadOnlyList<ContentOperator> ops,
        string key,
        HashSet<ContentOperator> affectedSpans)
    {
        foreach (var op in ops)
        {
            if (op.Name is not ("BDC" or "DP")) continue;
            if (NamedPropertyList(op)?.Value != key) continue;
            if (!affectedSpans.Contains(op)) return false;
        }
        return true;
    }

    private static bool HasTextCarrier(ContentOperator bdc, PdfPage? page)
    {
        var props = bdc.Operands.OfType<PdfDictionary>().FirstOrDefault();
        if (props == null && page != null && NamedPropertyList(bdc) is { } name)
        {
            // #1599: a named span carries the same text carriers as an inline
            // one, so enclosure tracking has to see it too — otherwise the span
            // is never marked affected and the dictionary is never reachable.
            var properties = page.Document.Resolve(
                page.Resources?.GetOptional("Properties") ?? PdfNull.Instance) as PdfDictionary;
            props = page.Document.Resolve(
                properties?.GetOptional(name.Value) ?? PdfNull.Instance) as PdfDictionary;
        }

        return props != null &&
               StructureTreeRedactionScrubber.TextCarriers.Any(props.ContainsKey);
    }
}
