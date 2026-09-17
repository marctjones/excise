using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// RC18 CERTAIN-channel recovery from unscrubbed CARRIERS. A redaction that
/// rewrote the visible page content but missed a document-level carrier leaves
/// the term physically present and trivially readable — <c>/ActualText</c>,
/// <c>/Alt</c> or <c>/E</c> in the structure tree (#636), or an annotation's
/// <c>/Contents</c> (#608). Unlike the width-residue channel this asserts
/// nothing probabilistic: the text is right there in the bytes.
///
/// This is the READ mirror of the scrubbers (<see cref="StructureTreeRedactionScrubber"/>,
/// <c>PdfDocumentSanitizer</c>): what recovery reports here is exactly what
/// scrubbing is supposed to remove, so the unredact tool sees the leak the
/// scrub side is meant to close. Deliberately limited to the hidden-readable
/// carriers, NOT bulk /Info + XMP metadata — those are usually benign document
/// metadata, and dumping them would bury a real leak in noise. Indirect string
/// objects are resolved first (the #1155 shape a plain GetStringOrNull misses).
/// </summary>
public static class CarrierTextRecovery
{
    // The structure-tree carriers that spell real content (§14.9.4): the actual
    // text a span represents, a figure's alternate description, an abbreviation
    // expansion. Same set StructureTreeRedactionScrubber removes.
    private static readonly string[] StructCarriers = { "ActualText", "Alt", "E" };

    /// <summary>A recoverable string found in a carrier the visible page does not show.</summary>
    /// <param name="Carrier">Human-readable carrier name, e.g. "structure-tree /ActualText".</param>
    /// <param name="Text">The recovered text, verbatim.</param>
    /// <param name="PageNumber">
    /// 1-based page. 0 means the carrier could not be placed on a page — a
    /// structure element with no /Pg, or one whose /Pg resolves to no page in
    /// this document. The finding is still real; the report files it
    /// document-level rather than inventing a position for it.
    /// </param>
    /// <param name="Rect">
    /// #1587: where the carrier's text belongs on the page. An annotation
    /// carries its own /Rect. A structure element carries none, so the box is
    /// the union of the glyphs its /MCID marked content painted — the closest
    /// thing to a location the carrier has, and the box a restored copy must
    /// draw into. Null when neither is available.
    /// </param>
    public readonly record struct CarrierText(
        string Carrier, string Text, int PageNumber, PdfRectangle? Rect = null);

    public static IReadOnlyList<CarrierText> Scan(PdfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var found = new List<CarrierText>();
        ScanStructureTree(doc, found);
        ScanAnnotations(doc, found);
        return found;
    }

    private static void ScanStructureTree(PdfDocument doc, List<CarrierText> found)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("StructTreeRoot") ?? PdfNull.Instance) is not PdfDictionary root)
            return;

        var stack = new Stack<PdfObject>();
        if (root.GetOptional("K") is { } k) stack.Push(k);
        var visited = new HashSet<PdfDictionary>();
        // #1587: glyph boxes per (page, MCID), built lazily. A document with no
        // located carrier never pays for it, and one with many pays once per
        // page rather than once per carrier.
        var mcidBoxes = new Dictionary<int, IReadOnlyDictionary<int, PdfRectangle>>();
        var guard = 0;
        while (stack.Count > 0 && guard++ < 200_000)
        {
            var node = doc.Resolve(stack.Pop());
            if (node is PdfArray arr)
            {
                foreach (var e in arr) stack.Push(e);
                continue;
            }
            if (node is not PdfDictionary elem || !visited.Add(elem)) continue;

            var hasCarrier = StructCarriers.Any(elem.ContainsKey);
            var (pageNumber, rect) = hasCarrier
                ? LocateElement(doc, elem, mcidBoxes)
                : (0, (PdfRectangle?)null);

            foreach (var carrier in StructCarriers)
            {
                var v = (doc.Resolve(elem.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;
                if (!string.IsNullOrWhiteSpace(v))
                    found.Add(new CarrierText($"structure-tree /{carrier}", v!, pageNumber, rect));
            }
            if (elem.GetOptional("K") is { } kids) stack.Push(kids);
        }
    }

    /// <summary>
    /// #1587 — where a structure element's text sits on the page: the page its
    /// <c>/Pg</c> names, and the union of the glyph boxes its <c>/MCID</c>
    /// marked content painted (§14.7.4.3).
    ///
    /// <para>A carrier value has no geometry of its own — it is a property on a
    /// tree node, not a drawing operation — so the MCID bridge is the only
    /// evidence available for where the text it restates belonged. Returns page
    /// 0 and no rectangle rather than guessing when <c>/Pg</c> is absent,
    /// resolves to no page here, or names MCIDs this page never painted (the
    /// normal case AFTER a redaction removed those glyphs, which is exactly
    /// when this channel fires).</para>
    /// </summary>
    private static (int PageNumber, PdfRectangle? Rect) LocateElement(
        PdfDocument doc,
        PdfDictionary elem,
        Dictionary<int, IReadOnlyDictionary<int, PdfRectangle>> cache)
    {
        if (elem.GetOptional("Pg") is not PdfReference pageRef) return (0, null);

        var pageNumber = 0;
        for (var i = 1; i <= doc.PageCount; i++)
        {
            var candidate = doc.GetPageReference(i);
            if (candidate != null && candidate.ObjectNumber == pageRef.ObjectNumber)
            {
                pageNumber = i;
                break;
            }
        }
        if (pageNumber == 0) return (0, null);

        var mcids = CollectMcids(doc, elem.GetOptional("K")).ToList();
        if (mcids.Count == 0) return (pageNumber, null);

        if (!cache.TryGetValue(pageNumber, out var boxes))
        {
            boxes = McidBoxesOf(doc.GetPage(pageNumber));
            cache[pageNumber] = boxes;
        }

        PdfRectangle? union = null;
        foreach (var mcid in mcids)
        {
            if (!boxes.TryGetValue(mcid, out var box)) continue;
            union = union is { } u ? Union(u, box) : box;
        }
        return (pageNumber, union);
    }

    /// <summary>Union of each MCID's glyph boxes on one page.</summary>
    private static IReadOnlyDictionary<int, PdfRectangle> McidBoxesOf(PdfPage page)
    {
        var boxes = new Dictionary<int, PdfRectangle>();
        IReadOnlyList<Text.Letter> letters;
        try { letters = page.Letters; }
        catch { return boxes; }

        foreach (var letter in letters)
        {
            if (letter.MarkedContentId is not { } id) continue;
            var box = letter.GlyphRectangle.Normalize();
            boxes[id] = boxes.TryGetValue(id, out var existing) ? Union(existing, box) : box;
        }
        return boxes;
    }

    /// <summary>
    /// The MCIDs a structure element owns, including through nested
    /// <c>StructElem</c> children — a <c>/Span</c> carrying the
    /// <c>/ActualText</c> usually holds its glyphs one level down.
    /// </summary>
    private static IEnumerable<int> CollectMcids(PdfDocument doc, PdfObject? k, int depth = 0)
    {
        if (k == null || depth > 16) yield break;
        switch (doc.Resolve(k))
        {
            case PdfInteger direct:
                yield return (int)direct.Value;
                break;

            case PdfArray array:
                foreach (var item in array)
                    foreach (var id in CollectMcids(doc, item, depth + 1))
                        yield return id;
                break;

            case PdfDictionary dict:
                // A marked-content reference (§14.7.4.3), or a child StructElem
                // whose own /K holds the ids.
                if (dict.GetOptional("Type") is PdfName { Value: "MCR" } &&
                    dict.GetOptional("MCID") is PdfInteger id2)
                {
                    yield return (int)id2.Value;
                }
                else if (dict.GetOptional("K") is { } nested)
                {
                    foreach (var id in CollectMcids(doc, nested, depth + 1))
                        yield return id;
                }
                break;
        }
    }

    private static PdfRectangle Union(PdfRectangle a, PdfRectangle b) => new(
        Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom),
        Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));

    private static void ScanAnnotations(PdfDocument doc, List<CarrierText> found)
    {
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            foreach (var annotObj in annots)
            {
                if (doc.Resolve(annotObj) is not PdfDictionary annot) continue;
                // /Contents is the note/markup text (§12.5.6). /T is skipped on
                // purpose: on a widget it is the FIELD NAME (e.g. "btn1"), noise
                // rather than hidden content.
                // /Contents is the comment text; /RC its XHTML rich-text restatement
                // (§12.5.6.2) — a separate carrier the scrub handles (#1185), so the
                // audit must see it too.
                // #1587: an annotation states its own geometry, so unlike a
                // structure element it needs no MCID bridge -- /Rect IS the
                // location, and a report can link it to the mark it sits on.
                var rect = doc.Resolve(annot.GetOptional("Rect") ?? PdfNull.Instance) is PdfArray r && r.Count == 4
                    ? PdfRectangle.FromArray(r).Normalize()
                    : (PdfRectangle?)null;
                foreach (var key in new[] { "Contents", "RC" })
                {
                    var v = (doc.Resolve(annot.GetOptional(key) ?? PdfNull.Instance) as PdfString)?.Value;
                    if (!string.IsNullOrWhiteSpace(v))
                        found.Add(new CarrierText($"annotation /{key}", v!, i, rect));
                }
            }
        }
    }
}
