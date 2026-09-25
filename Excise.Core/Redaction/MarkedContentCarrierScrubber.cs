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
/// <para>Runs on the page re-parsed after Form XObject flattening and before
/// glyph removal — so inline carriers inlined from a flattened form are covered
/// too, and every operator carries the parser's span stamp. Mutates the inline
/// property dictionaries in place; they are not shared objects (an inline
/// <c>BDC</c> dict belongs to the one operator).</para>
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
///
/// <para><b>By term (#1854).</b> Both signals above start from removed glyphs,
/// so a span whose <c>/ActualText</c> holds the term over glyphs that do not
/// spell it kept it. <see cref="ScrubTerm"/> is the document-wide term stage.</para>
/// </summary>
internal static class MarkedContentCarrierScrubber
{
    /// <summary>
    /// <see cref="Mask"/> every property list in the document with the term
    /// policy: each NAMED list in a reachable <c>/Properties</c>, shared or not
    /// (the term leaves the document), and each INLINE list in every content
    /// stream (§7.8). <paramref name="unreadable"/> counts the streams that
    /// could not be read.
    /// </summary>
    internal static bool ScrubTerm(PdfDocument document, System.Func<string?, string?> apply, out int unreadable)
    {
        var changed = false;
        var streams = new HashSet<PdfStream>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var dict in RedactionFeatureStripper.ReachableDictionaries(document))
        {
            if (document.Resolve(dict.GetOptional("Properties") ?? PdfNull.Instance) is PdfDictionary lists)
                foreach (var (_, list) in lists)
                    if (document.Resolve(list) is PdfDictionary props)
                        changed |= Mask(document, props, apply);

            if (dict is PdfStream stream
                && (stream.GetNameOrNull("Subtype") == "Form" || stream.GetOptional("PatternType") is PdfInteger { Value: 1 }))
                streams.Add(stream);
            // Type 3 glyphs, and appearance streams whether or not they declare /Subtype /Form.
            foreach (var key in new[] { "CharProcs", "AP" })
                if (document.Resolve(dict.GetOptional(key) ?? PdfNull.Instance) is PdfDictionary procs)
                    foreach (var (_, value) in procs)
                        switch (document.Resolve(value))
                        {
                            case PdfStream s: streams.Add(s); break;
                            case PdfDictionary states:
                                foreach (var (_, state) in states)
                                    if (document.Resolve(state) is PdfStream a) streams.Add(a);
                                break;
                        }
        }

        unreadable = 0;
        for (var p = 1; p <= document.PageCount; p++)
        {
            var page = document.GetPage(p);
            try
            {
                if (!MayHoldPropertyList(page.GetContentStreamBytes())) continue;
                var content = page.GetContentStream(trackSourceSpans: true, computeOperatorMetadata: false);
                if (!MaskInline(document, content.Operators, apply)) continue;
                page.SetContentStream(content);
                changed = true;
            }
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException) { unreadable++; }
        }

        foreach (var stream in streams)
        {
            try
            {
                if (stream.IsFiltered && !stream.TryEnsureDecoded()) { unreadable++; continue; }
                var bytes = stream.DecodedData;
                if (!MayHoldPropertyList(bytes)) continue;
                var content = new ContentStreamParser(bytes)
                {
                    TrackSourceSpans = true,
                    ComputeOperatorMetadata = false,
                }.Parse();
                if (!MaskInline(document, content.Operators, apply)) continue;
                stream.DecodedData = new ContentStreamWriter().Write(content, bytes);
                changed = true;
            }
            catch (System.Exception ex) when (ex is not System.OutOfMemoryException) { unreadable++; }
        }
        return changed;
    }

    // Operator names cannot be escaped, so a stream without these bytes has no BDC or DP.
    private static bool MayHoldPropertyList(byte[] bytes) =>
        bytes.AsSpan().IndexOf("BDC"u8) >= 0 || bytes.AsSpan().IndexOf("DP"u8) >= 0;

    private static bool MaskInline(PdfDocument document, IReadOnlyList<ContentOperator> ops, System.Func<string?, string?> apply)
    {
        var changed = false;
        foreach (var op in ops)
            if (op.Name is "BDC" or "DP" && op.Operands.OfType<PdfDictionary>().FirstOrDefault() is { } props)
                changed |= Mask(document, props, apply);
        return changed;
    }

    /// <summary>
    /// Give each text carrier of <paramref name="props"/> what <paramref name="apply"/>
    /// returns for its value (null when it is not a string): empty drops the
    /// key, null leaves it.
    /// </summary>
    private static bool Mask(PdfDocument document, PdfDictionary props, System.Func<string?, string?> apply)
    {
        var changed = false;
        foreach (var carrier in StructureTreeRedactionScrubber.TextCarriers)
        {
            if (!props.ContainsKey(carrier)) continue;
            // /ActualText/Alt/E may be an indirect string (#1155).
            var value = (document.Resolve(props.GetOptional(carrier)!) as PdfString)?.Value;
            if (apply(value) is not { } replacement) continue;
            if (replacement.Length == 0) props.Remove(carrier);
            else props[carrier] = new PdfString(replacement);
            changed = true;
        }
        return changed;
    }

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
            removedAny |= Mask(doc, props, value =>
                enclosesRemovedGlyphs || (value != null && removedText.Any(t =>
                    t.Length >= StructureTreeRedactionScrubber.MinMatchLength &&
                    value.Contains(t, System.StringComparison.Ordinal)))
                    ? "" : null);
        }

        unscrubbedSharedCarriers = shared is { Count: > 0 }
            ? shared.ToList()
            : System.Array.Empty<string>();
        return removedAny;
    }

    /// <summary>
    /// The set of carrier-bearing BDC operators (by reference) whose enclosed
    /// glyphs intersect <paramref name="area"/>. Every span enclosing such a
    /// glyph counts, so a carrier span with no MCID of its own is still caught
    /// when a nested glyph falls in the redaction region.
    /// </summary>
    private static HashSet<ContentOperator> CollectAffectedCarrierSpans(
        IReadOnlyList<ContentOperator> ops, PdfRectangle area, PdfPage page)
    {
        var affected = new HashSet<ContentOperator>();
        foreach (var op in ops)
            if (op.BoundingBox is { } box && box.IntersectsWith(area))
                affected.UnionWith(op.EnclosingSpans);

        // Only a span that carries text is a carrier: one without would be
        // reported as a shared carrier the scrub refused (#1599).
        affected.RemoveWhere(span => !HasTextCarrier(span, page));
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

    private static bool HasTextCarrier(ContentOperator bdc, PdfPage page)
    {
        var props = bdc.Operands.OfType<PdfDictionary>().FirstOrDefault();
        if (props == null && NamedPropertyList(bdc) is { } name)
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
