using Excise.Core.Primitives;
using System.Collections.Generic;

namespace Excise.Core.Document;

/// <summary>
/// One node in a PDF outline (Table of Contents) tree. PDF spec §12.3.3.
/// Title is the visible label; PageNumber (1-based) is the destination if
/// the outline item carries a /Dest or /A → /D entry that resolves to a
/// page in this document. PageNumber is null when the destination can't
/// be resolved (named destinations not yet looked up, external action,
/// malformed PDF, etc.).
/// </summary>
public sealed class PdfOutlineItem
{
    public string Title { get; }
    public int? PageNumber { get; }
    public IReadOnlyList<PdfOutlineItem> Children { get; }

    public PdfOutlineItem(string title, int? pageNumber, IReadOnlyList<PdfOutlineItem> children)
    {
        Title = title;
        PageNumber = pageNumber;
        Children = children;
    }
}

/// <summary>
/// Lazy parser for the document's /Outlines tree.
/// </summary>
public static class PdfOutlineParser
{
    /// <summary>
    /// Build the outline tree for <paramref name="doc"/>. Returns an empty
    /// list when the document has no outline.
    /// </summary>
    public static IReadOnlyList<PdfOutlineItem> Parse(PdfDocument doc)
    {
        var rootObj = doc.Catalog.GetOptional("Outlines");
        if (rootObj == null) return System.Array.Empty<PdfOutlineItem>();
        var root = doc.Resolve(rootObj) as PdfDictionary;
        if (root == null) return System.Array.Empty<PdfOutlineItem>();

        // Build named-destinations map — PDF spec §12.3.2.3. Some outlines
        // reference destinations by name rather than direct page reference.
        var namedDests = BuildNamedDestinations(doc);

        // Outline root has /First pointing to the first child.
        var firstObj = root.GetOptional("First");
        if (firstObj == null) return System.Array.Empty<PdfOutlineItem>();
        return ParseSiblingChain(doc, firstObj, namedDests, depth: 0);
    }

    private const int MaxDepth = 32;

    private static List<PdfOutlineItem> ParseSiblingChain(PdfDocument doc, PdfObject firstObj,
        Dictionary<string, PdfObject>? namedDests,
        int depth)
    {
        var siblings = new List<PdfOutlineItem>();
        if (depth > MaxDepth) return siblings; // guard against malformed cycles

        var visited = new HashSet<(int, int)>();
        var current = doc.Resolve(firstObj) as PdfDictionary;
        while (current != null)
        {
            // Detect cycles in /Next links — malformed PDFs can loop here.
            if (firstObj is PdfReference r)
            {
                if (!visited.Add((r.ObjectNum, r.Generation))) break;
            }

            var title = current.GetStringOrNull("Title") ?? string.Empty;
            var page = ResolveDestinationPage(doc, current, namedDests);

            var childFirst = current.GetOptional("First");
            var children = childFirst != null
                ? ParseSiblingChain(doc, childFirst, namedDests, depth + 1)
                : (IReadOnlyList<PdfOutlineItem>)System.Array.Empty<PdfOutlineItem>();

            siblings.Add(new PdfOutlineItem(title, page, children));

            firstObj = current.GetOptional("Next") ?? (PdfObject)PdfNull.Instance;
            if (firstObj is PdfNull) break;
            current = doc.Resolve(firstObj) as PdfDictionary;
        }
        return siblings;
    }

    /// <summary>
    /// Outline items use either /Dest (a destination array or name) or
    /// /A (an action — for GoTo actions /A.D is the destination). The
    /// destination array's first element is the page reference.
    /// </summary>
    private static int? ResolveDestinationPage(PdfDocument doc, PdfDictionary item,
        Dictionary<string, PdfObject>? namedDests)
    {
        // /Dest can be a name, byte string, or array.
        var dest = item.GetOptional("Dest");
        if (dest == null)
        {
            // /A is a regular action; only GoTo (/S /GoTo) carries a /D destination.
            var action = doc.Resolve(item.GetOptional("A") ?? (PdfObject)PdfNull.Instance) as PdfDictionary;
            if (action != null)
            {
                var subtype = action.GetNameOrNull("S");
                if (subtype != "GoTo") return null;
                dest = action.GetOptional("D");
            }
            if (dest == null) return null;
        }

        // Resolve named destinations to their array form.
        dest = ResolveNamedDestination(doc, dest, namedDests);
        return dest is PdfArray arr && arr.Count > 0 && doc.TryGetPageNumber(arr[0], out var n) ? n : null;
    }

    private static PdfObject? ResolveNamedDestination(PdfDocument doc, PdfObject dest,
        Dictionary<string, PdfObject>? namedDests)
    {
        if (dest is PdfName name)
        {
            return namedDests != null && namedDests.TryGetValue(name.Value, out var arr) ? arr : null;
        }
        if (dest is PdfString s)
        {
            return namedDests != null && namedDests.TryGetValue(s.Value, out var arr) ? arr : null;
        }
        return doc.Resolve(dest);
    }

    /// <summary>
    /// Read /Catalog/Names/Dests (PDF 1.2+) and /Catalog/Dests (older) into
    /// a flat name → destination-array map. Returns null when the document
    /// has no named destinations (most don't). Public so link-parser code
    /// can share one resolved map across outline and per-page link parsing.
    /// </summary>
    public static Dictionary<string, PdfObject>? BuildNamedDestinations(PdfDocument doc)
    {
        var map = new Dictionary<string, PdfObject>();

        // /Catalog/Dests — older form, dictionary of name → destination.
        if (doc.Resolve(doc.Catalog.GetOptional("Dests") ?? PdfNull.Instance) is PdfDictionary destsDict)
        {
            foreach (var kvp in destsDict)
                map[kvp.Key.Value] = Destination(doc, kvp.Value);
        }

        // /Catalog/Names/Dests — PDF 1.2+ name tree.
        var names = doc.Resolve(doc.Catalog.GetOptional("Names") ?? PdfNull.Instance) as PdfDictionary;
        foreach (var (key, value) in PdfNameTree.Enumerate(doc, names?.GetOptional("Dests")))
        {
            if (key is PdfString name)
                map[name.Value] = Destination(doc, value);
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>A named destination is a destination array directly, or a dictionary whose /D holds it.</summary>
    private static PdfObject Destination(PdfDocument doc, PdfObject value)
    {
        var v = doc.Resolve(value);
        if (v is PdfDictionary d && d.GetOptional("D") is { } dArr) v = doc.Resolve(dArr);
        return v;
    }
}
