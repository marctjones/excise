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

        foreach (var op in ops)
        {
            switch (op.Name)
            {
                case "BMC":
                    open.Add(new Span(null, false));
                    break;

                case "BDC":
                {
                    var (props, named) = ResolveProperties(document, op, properties);
                    open.Add(new Span(props, named));
                    break;
                }

                case "EMC":
                {
                    if (open.Count == 0) break;   // unbalanced EMC: §14.6 violation, not fatal here
                    var span = open[^1];
                    open.RemoveAt(open.Count - 1);
                    Emit(document, span, pageNumber, found);
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
        foreach (var span in open) Emit(document, span, pageNumber, found);
    }

    private static (PdfDictionary? Props, bool Named) ResolveProperties(
        PdfDocument document, ContentOperator bdc, PdfDictionary? properties)
    {
        var inline = bdc.Operands.OfType<PdfDictionary>().FirstOrDefault();
        if (inline != null) return (inline, false);

        // The named form: /Span /P1 BDC, where /P1 is a key in
        // /Resources /Properties. The tag is operand 0, the name operand 1.
        if (properties == null || bdc.Operands.Count < 2) return (null, false);
        if (bdc.Operands[1] is not PdfName name) return (null, false);
        var resolved = document.Resolve(properties.GetOptional(name.Value) ?? PdfNull.Instance);
        return resolved is PdfDictionary dict ? (dict, true) : (null, false);
    }

    private static void Emit(
        PdfDocument document, Span span, int pageNumber, List<MarkedContentText> found)
    {
        if (span.Properties is not { } props) return;
        foreach (var carrier in Carriers)
        {
            if (!props.ContainsKey(carrier)) continue;
            // #1155: the value may be an indirect string; a plain read misses it.
            var value = (document.Resolve(props.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            found.Add(new MarkedContentText(
                pageNumber, $"marked-content /{carrier}", value!, span.Box, span.Named));
        }
    }

    private sealed class Span(PdfDictionary? properties, bool named)
    {
        public PdfDictionary? Properties { get; } = properties;
        public bool Named { get; } = named;
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
