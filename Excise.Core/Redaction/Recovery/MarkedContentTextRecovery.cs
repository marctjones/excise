using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — the READ mirror of <c>MarkedContentCarrierScrubber</c>: text carried
/// INLINE in the content stream as a marked-content property list
/// (<c>/Span &lt;&lt;/ActualText (SECRET)&gt;&gt; BDC … EMC</c>, §14.9.4), and the
/// location of the glyphs that span encloses.
///
/// <para><b>Why this is not covered by <see cref="CarrierTextRecovery"/>.</b>
/// That one walks the STRUCTURE TREE, where <c>/ActualText</c> sits on a
/// <c>StructElem</c> object. The identical text can sit in a <c>BDC</c>
/// property dictionary in the content stream, which the structure-tree walk
/// never reaches — a different carrier with the same payload. Glyph removal
/// rewrites the text-show operators and passes <c>BDC</c> through verbatim, so
/// the inline value survives a redaction that reports success (#1182/#1185).
/// An accessibility-aware reader (<c>mutool -A</c>, a screen reader) reads it
/// straight out.</para>
///
/// <para><b>Both property-list forms are read.</b> <c>BDC</c> takes either an
/// inline dictionary or a NAME resolving through <c>/Resources /Properties</c>
/// (§14.6.2). Reading a carrier is never destructive, and a channel that
/// skipped either form would be blind to a leak that is physically present.</para>
///
/// <para><b>Every content stream a page draws is read (#1854, #1849).</b> Form
/// XObjects, tiling patterns and Type 3 glyph procedures through the resources,
/// and annotation appearances, each once, under the first page that draws it
/// and with no location: its operators are not in page space. This walks what
/// the page DRAWS, not the object graph the term scrub walks, so the audit
/// does not share the scrub's blind spots.</para>
///
/// <para><b>Location comes from the enclosed glyphs, not the carrier.</b> A
/// carrier has no geometry of its own. The span's own <c>/MCID</c> is not used
/// either: carrier spans frequently have none, the MCID living on an enclosing
/// span. So the box is the union of the operator bounding boxes drawn between
/// this <c>BDC</c> and its matching <c>EMC</c>, nesting-aware — the same
/// enclosure signal the scrubber uses to decide what to remove.</para>
/// </summary>
public static class MarkedContentTextRecovery
{
    /// <summary>One inline carrier value and where its span painted.</summary>
    /// <param name="Enclosed">
    /// Union of the bounding boxes the span drew, or null when the span
    /// enclosed nothing with geometry — an empty span still carries the text,
    /// so it is reported with no location rather than dropped.
    /// </param>
    public readonly record struct MarkedContentText(
        int PageNumber, string Carrier, string Text, PdfRectangle? Enclosed, bool NamedPropertyList);

    private static readonly string[] Carriers = { "ActualText", "Alt", "E" };

    public static IReadOnlyList<MarkedContentText> Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var found = new List<MarkedContentText>();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        for (var p = 1; p <= document.PageCount; p++)
        {
            var page = document.GetPage(p);
            ScanPage(document, page, p, found);
            var pending = new Stack<(PdfStream Stream, PdfDictionary? Resources)>(
                Drawn(document, page.Resources, page.Dictionary));
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next.Stream)) continue;
                IReadOnlyList<ContentOperator> ops;
                try { ops = new ContentStreamParser(next.Stream.DecodedData) { ComputeOperatorMetadata = false }.Parse().Operators; }
                catch { continue; }
                Collect(document, page, p, ops, next.Resources?.ResolveDictionary(document, "Properties"), null, found);
                foreach (var child in Drawn(document, next.Resources, null)) pending.Push(child);
            }
        }
        return found;
    }

    /// <summary>
    /// The content streams <paramref name="resources"/> can draw, each with the
    /// resources it runs under (its own, else the caller's), plus the appearance
    /// streams of <paramref name="page"/>'s annotations.
    /// </summary>
    private static IEnumerable<(PdfStream, PdfDictionary?)> Drawn(
        PdfDocument document, PdfDictionary? resources, PdfDictionary? page)
    {
        foreach (var category in new[] { "XObject", "Pattern", "Font" })
        {
            foreach (var (_, value) in resources?.ResolveDictionary(document, category) ?? new PdfDictionary())
            {
                if (document.Resolve(value) is not PdfDictionary item) continue;
                var own = item.ResolveDictionary(document, "Resources") ?? resources;
                if (item is PdfStream s
                    && (s.GetNameOrNull("Subtype") == "Form" || s.GetOptional("PatternType") is PdfInteger { Value: 1 }))
                    yield return (s, own);
                foreach (var (_, proc) in item.ResolveDictionary(document, "CharProcs") ?? new PdfDictionary())
                    if (document.Resolve(proc) is PdfStream glyph) yield return (glyph, own);
            }
        }

        // Each appearance (/N, /R, /D) is a stream, or a dictionary of state streams.
        foreach (var annot in page?.ResolveArray(document, "Annots") ?? new PdfArray())
            foreach (var (_, state) in (document.Resolve(annot) as PdfDictionary)?.ResolveDictionary(document, "AP") ?? new PdfDictionary())
                foreach (var ap in document.Resolve(state) is PdfStream one
                             ? [one]
                             : ((document.Resolve(state) as PdfDictionary) ?? new PdfDictionary())
                                 .Select(kv => document.Resolve(kv.Value)).OfType<PdfStream>())
                    yield return (ap, ap.ResolveDictionary(document, "Resources"));
    }

    private static void ScanPage(
        PdfDocument document, PdfPage page, int pageNumber, List<MarkedContentText> found)
    {
        IReadOnlyList<ContentOperator> ops;
        try { ops = page.GetContentStream().Operators; }
        catch { return; }

        // A span's box accumulates everything drawn inside it, nested spans
        // included, so an /ActualText on an outer span still gets the box of
        // the glyphs a nested span painted.
        var drawn = new Dictionary<ContentOperator, PdfRectangle>();
        foreach (var op in ops)
        {
            if (op.BoundingBox is not { } box) continue;
            var b = box.Normalize();
            foreach (var span in op.EnclosingSpans)
                drawn[span] = drawn.TryGetValue(span, out var e)
                    ? new PdfRectangle(
                        Math.Min(e.Left, b.Left), Math.Min(e.Bottom, b.Bottom),
                        Math.Max(e.Right, b.Right), Math.Max(e.Top, b.Top))
                    : b;
        }

        Collect(document, page, pageNumber, ops, page.Resources?.ResolveDictionary(document, "Properties"), drawn, found);
    }

    private static void Collect(
        PdfDocument document, PdfPage page, int pageNumber, IReadOnlyList<ContentOperator> ops,
        PdfDictionary? properties, Dictionary<ContentOperator, PdfRectangle>? drawn, List<MarkedContentText> found)
    {
        // #1672: collected per stream, then reduced. A NAMED list referenced by
        // several spans is ONE carrier and must be reported once. §14.6: a BDC
        // left unclosed at end-of-stream is malformed, but its carrier is still
        // in the file and still readable, so every BDC is read, and every DP.
        var collected = new List<(MarkedContentText Text, string? Name)>();
        foreach (var op in ops)
        {
            if (op.Name is not ("BDC" or "DP")) continue;
            var (props, named, name) = ResolveProperties(document, op, properties);
            if (props != null)
                Emit(document, props, named, name, drawn != null && drawn.TryGetValue(op, out var box) ? box : null,
                    pageNumber, collected);
        }

        found.AddRange(Reduce(page, collected));
    }

    private static (PdfDictionary? Props, bool Named, string? Name) ResolveProperties(
        PdfDocument document, ContentOperator bdc, PdfDictionary? properties)
    {
        var inline = bdc.Operands.OfType<PdfDictionary>().FirstOrDefault();
        if (inline != null) return (inline, false, null);

        // The named form: /Span /P1 BDC, where /P1 is a key in
        // /Resources /Properties. The tag is operand 0, the name operand 1.
        if (properties == null || bdc.Operands.Count < 2) return (null, false, null);
        if (bdc.Operands[1] is not PdfName name) return (null, false, null);
        var resolved = document.Resolve(properties.GetOptional(name.Value) ?? PdfNull.Instance);
        return resolved is PdfDictionary dict ? (dict, true, name.Value) : (null, false, null);
    }

    /// <summary>
    /// #1672 — collapse a NAMED property list referenced by several spans to a
    /// single finding, and choose the location that matters.
    ///
    /// <para><b>Why only the named form.</b> An INLINE dictionary is written
    /// out per span, so two inline spans are two carriers in the file and two
    /// findings is correct. A named list is ONE object the spans point at
    /// (§14.6.2). Reporting it per referencing span inflated every count
    /// downstream — the "N finding(s) indicate a failed redaction" line, and
    /// on a real tagged document the multiplier is however many spans the
    /// producer emitted, not two. That is #1625's inflation in another
    /// carrier.</para>
    ///
    /// <para>It also matches the SCRUB side: #1599 scrubs a named list once,
    /// gated on every referencing span being affected. One thing to remove
    /// should be one thing to report, or the audit and the scrub disagree
    /// about what a carrier is.</para>
    ///
    /// <para><b>Which location survives, and why it is not simply the first.</b>
    /// A finding carries ONE <c>Location</c>, so collapsing has to pick. For a
    /// redaction audit the useful span is the one a redaction was drawn over:
    /// if span A sits under a black box and span B does not, the carrier leaks
    /// A's text and reporting at B would both read wrong and fail to link to
    /// A's mark in <see cref="RecoveryReportBuilder"/>. So the box overlapping
    /// a dark filled rectangle wins; absent one, the first span.</para>
    ///
    /// <para>⚠️ The mark scan is a second content walk, so it runs ONLY when a
    /// group actually has more than one member — the common page pays nothing.
    /// </para>
    /// </summary>
    private static IEnumerable<MarkedContentText> Reduce(
        PdfPage page, List<(MarkedContentText Text, string? Name)> collected)
    {
        var result = new List<MarkedContentText>();
        var groups = new Dictionary<(string Name, string Carrier, string Text), List<MarkedContentText>>();

        foreach (var (text, name) in collected)
        {
            if (name == null) { result.Add(text); continue; }       // inline: its own carrier
            var key = (name, text.Carrier, text.Text);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<MarkedContentText>();
            list.Add(text);
        }

        IReadOnlyList<PdfRectangle>? marks = null;
        foreach (var group in groups.Values)
        {
            if (group.Count == 1) { result.Add(group[0]); continue; }

            marks ??= SafeMarks(page);
            var under = group.FirstOrDefault(g => g.Enclosed is { } b && marks!.Any(m => m.IntersectsWith(b)));
            result.Add(under.Text != null ? under : group[0]);
        }

        return result;
    }

    private static IReadOnlyList<PdfRectangle> SafeMarks(PdfPage page)
    {
        // A page whose marks cannot be read is not a reason to lose the
        // finding; it only costs the location preference.
        try { return Text.Segmentation.HiddenTextDetector.DarkFilledBoxes(page); }
        catch { return Array.Empty<PdfRectangle>(); }
    }

    // `name` is the /Properties key of the NAMED form, null for inline. #1672
    // groups a shared dictionary by (page, name), not by instance: §14.6.2
    // resolves a name through the page's own /Properties, so two spans naming
    // /P1 on one page ARE one carrier, whatever instance Resolve hands back.
    private static void Emit(
        PdfDocument document, PdfDictionary props, bool named, string? name, PdfRectangle? box,
        int pageNumber, List<(MarkedContentText Text, string? Name)> found)
    {
        foreach (var carrier in Carriers)
        {
            if (!props.ContainsKey(carrier)) continue;
            // #1155: the value may be an indirect string; a plain read misses it.
            var value = (document.Resolve(props.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            found.Add((new MarkedContentText(
                pageNumber, $"marked-content /{carrier}", value!, box, named),
                name));
        }
    }
}
