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
/// <para><b>Both property-list forms are read, deliberately including the one
/// the scrubber does not remove.</b> <c>BDC</c> takes either an inline
/// dictionary or a NAME resolving through <c>/Resources /Properties</c>
/// (§14.6.2). The scrubber handles only the inline form — the named dictionary
/// can be shared between spans, so positional scrubbing could over-remove.
/// Recovery has no such constraint: reading a carrier is never destructive, and
/// a channel that skipped the named form would be blind to a leak that is
/// physically present. Where this reports a named-form carrier, the scrub side
/// cannot currently close it; see #1599.</para>
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
        for (var p = 1; p <= document.PageCount; p++)
            ScanPage(document, document.GetPage(p), p, found);
        return found;
    }

    private static void ScanPage(
        PdfDocument document, PdfPage page, int pageNumber, List<MarkedContentText> found)
    {
        IReadOnlyList<ContentOperator> ops;
        try { ops = page.GetContentStream().Operators; }
        catch { return; }

        var properties = page.Resources?.ResolveDictionary(document, "Properties");

        // One entry per open marked-content span, innermost last. Carrier spans
        // accumulate the geometry of everything drawn inside them, including
        // inside nested spans, so an /ActualText on an outer span still gets the
        // box of the glyphs a nested span painted.
        var open = new List<Span>();

        // #1672: collected per page, then reduced. A NAMED list referenced by
        // several spans is ONE carrier and must be reported once.
        var onThisPage = new List<(MarkedContentText Text, string? Name)>();

        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "BMC":
                    open.Add(new Span(null, false));
                    break;

                case "BDC":
                {
                    var (props, named, name) = ResolveProperties(document, op, properties);
                    open.Add(new Span(props, named, name));
                    break;
                }

                case "EMC":
                {
                    if (open.Count == 0) break;   // unbalanced EMC: §14.6 violation, not fatal here
                    var span = open[^1];
                    open.RemoveAt(open.Count - 1);
                    Emit(document, span, pageNumber, onThisPage);
                    // The closed span's geometry belongs to its parent too.
                    if (open.Count > 0 && span.Box is { } box) open[^1].Add(box);
                    break;
                }

                default:
                {
                    if (op.BoundingBox is not { } box || open.Count == 0) break;
                    foreach (var span in open) span.Add(box);
                    break;
                }
            }
        }

        // §14.6: a BDC left unclosed at end-of-stream is malformed, but its
        // carrier is still in the file and still readable, so it is reported.
        foreach (var span in open) Emit(document, span, pageNumber, onThisPage);

        found.AddRange(Reduce(page, onThisPage));
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
            var under = group.FirstOrDefault(g => g.Enclosed is { } b && marks!.Any(m => Overlaps(m, b)));
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

    private static bool Overlaps(PdfRectangle mark, PdfRectangle box)
    {
        var m = mark.Normalize();
        var b = box.Normalize();
        return m.Left < b.Right && b.Left < m.Right && m.Bottom < b.Top && b.Bottom < m.Top;
    }

    private static void Emit(
        PdfDocument document, Span span, int pageNumber,
        List<(MarkedContentText Text, string? Name)> found)
    {
        if (span.Properties is not { } props) return;
        foreach (var carrier in Carriers)
        {
            if (!props.ContainsKey(carrier)) continue;
            // #1155: the value may be an indirect string; a plain read misses it.
            var value = (document.Resolve(props.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            found.Add((new MarkedContentText(
                pageNumber, $"marked-content /{carrier}", value!, span.Box, span.Named),
                span.ResourceName));
        }
    }

    private sealed class Span(PdfDictionary? properties, bool named, string? resourceName = null)
    {
        public PdfDictionary? Properties { get; } = properties;
        public bool Named { get; } = named;

        /// <summary>
        /// The <c>/Properties</c> key for the NAMED form, null for inline.
        /// #1672 identifies a shared dictionary by (page, this), NOT by
        /// <see cref="PdfDictionary"/> reference — §14.6.2 resolves a name
        /// through the page's own /Properties, so two spans naming /P1 on one
        /// page ARE the same carrier by definition, whether or not
        /// <c>Resolve</c> happens to hand back the same instance.
        /// </summary>
        public string? ResourceName { get; } = resourceName;
        public PdfRectangle? Box { get; private set; }

        public void Add(PdfRectangle box)
        {
            var b = box.Normalize();
            Box = Box is { } existing
                ? new PdfRectangle(
                    Math.Min(existing.Left, b.Left), Math.Min(existing.Bottom, b.Bottom),
                    Math.Max(existing.Right, b.Right), Math.Max(existing.Top, b.Top))
                : b;
        }
    }
}
