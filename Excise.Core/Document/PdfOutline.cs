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

        // Build a page-ref → page-number map once so destination lookups
        // are O(1) per outline node instead of O(N) per node.
        var pageRefToNumber = BuildPageRefMap(doc);

        // Build named-destinations map — PDF spec §12.3.2.3. Some outlines
        // reference destinations by name rather than direct page reference.
        var namedDests = BuildNamedDestinations(doc);

        // Outline root has /First pointing to the first child.
        var firstObj = root.GetOptional("First");
        if (firstObj == null) return System.Array.Empty<PdfOutlineItem>();
        return ParseSiblingChain(doc, firstObj, pageRefToNumber, namedDests, depth: 0);
    }

    private const int MaxDepth = 32;

    private static List<PdfOutlineItem> ParseSiblingChain(PdfDocument doc, PdfObject firstObj,
        Dictionary<(int, int), int> pageRefToNumber,
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
            var page = ResolveDestinationPage(doc, current, pageRefToNumber, namedDests);

            var childFirst = current.GetOptional("First");
            var children = childFirst != null
                ? ParseSiblingChain(doc, childFirst, pageRefToNumber, namedDests, depth + 1)
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
        Dictionary<(int, int), int> pageRefToNumber,
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
        if (dest is PdfArray arr && arr.Count > 0)
        {
            var pageObj = arr[0];
            if (pageObj is PdfReference pageRef)
            {
                if (pageRefToNumber.TryGetValue((pageRef.ObjectNum, pageRef.Generation), out var n))
                    return n;
            }
        }
        return null;
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
    /// Map page object references → 1-based page numbers. Walks the
    /// /Catalog/Pages tree once and records each leaf's indirect-ref
    /// identity. Shared with <see cref="PdfLinkParser"/> so callers can
    /// build it once and reuse for both outlines and link annotations.
    /// </summary>
    public static Dictionary<(int, int), int> BuildPageRefMap(PdfDocument doc)
    {
        var map = new Dictionary<(int, int), int>();
        var pagesRoot = doc.Catalog.GetOptional("Pages");
        if (pagesRoot != null && doc.Resolve(pagesRoot) is PdfDictionary rootDict)
        {
            var visited = new HashSet<(int, int)>();
            if (pagesRoot is PdfReference rootRef)
            {
                visited.Add((rootRef.ObjectNum, rootRef.Generation));
            }

            int counter = 0;
            WalkPages(doc, rootDict, map, ref counter, visited, depth: 0);
        }
        return map;
    }

    private static void WalkPages(PdfDocument doc, PdfDictionary node,
        Dictionary<(int, int), int> map, ref int counter,
        HashSet<(int, int)> visited, int depth)
    {
        if (depth > MaxDepth) return;

        var kids = node.GetOptional("Kids");
        if (kids != null && doc.Resolve(kids) is PdfArray kidsArr)
        {
            foreach (var kidObj in kidsArr)
            {
                if (kidObj is PdfReference kidRef &&
                    doc.Resolve(kidRef) is PdfDictionary kidDict)
                {
                    var kidType = kidDict.GetNameOrNull("Type");
                    if (kidType == "Page")
                    {
                        counter++;
                        map[(kidRef.ObjectNum, kidRef.Generation)] = counter;
                    }
                    else if (kidType == "Pages")
                    {
                        if (!visited.Add((kidRef.ObjectNum, kidRef.Generation)))
                            continue;

                        WalkPages(doc, kidDict, map, ref counter, visited, depth + 1);
                    }
                }
            }
        }
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
        var destsObj = doc.Catalog.GetOptional("Dests");
        if (destsObj != null && doc.Resolve(destsObj) is PdfDictionary destsDict)
        {
            foreach (var kvp in destsDict)
            {
                var v = doc.Resolve(kvp.Value);
                // Each entry can be a destination array directly, or a dict with /D.
                if (v is PdfDictionary d)
                {
                    var dArr = d.GetOptional("D");
                    if (dArr != null) v = doc.Resolve(dArr);
                }
                map[kvp.Key.Value] = v;
            }
        }

        // /Catalog/Names/Dests — PDF 1.2+ name tree.
        var namesObj = doc.Catalog.GetOptional("Names");
        if (namesObj != null && doc.Resolve(namesObj) is PdfDictionary namesDict)
        {
            var dests = namesDict.GetOptional("Dests");
            if (dests != null && doc.Resolve(dests) is PdfDictionary destsRoot)
            {
                WalkNameTree(doc, destsRoot, map);
            }
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>Walk a PDF name tree (§7.9.6). Tree leaves have /Names; branches have /Kids.</summary>
    private static void WalkNameTree(PdfDocument doc, PdfDictionary node, Dictionary<string, PdfObject> map)
    {
        var names = node.GetOptional("Names");
        if (names != null && doc.Resolve(names) is PdfArray namesArr)
        {
            for (int i = 0; i + 1 < namesArr.Count; i += 2)
            {
                if (namesArr[i] is PdfString key)
                {
                    var v = doc.Resolve(namesArr[i + 1]);
                    if (v is PdfDictionary d)
                    {
                        var dArr = d.GetOptional("D");
                        if (dArr != null) v = doc.Resolve(dArr);
                    }
                    map[key.Value] = v;
                }
            }
        }
        var kids = node.GetOptional("Kids");
        if (kids != null && doc.Resolve(kids) is PdfArray kidsArr)
        {
            foreach (var kidObj in kidsArr)
            {
                if (doc.Resolve(kidObj) is PdfDictionary kidDict)
                    WalkNameTree(doc, kidDict, map);
            }
        }
    }
}
