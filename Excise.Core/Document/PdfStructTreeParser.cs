using Excise.Core.Primitives;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Document;

/// <summary>
/// Parser for the PDF structure tree (tagged PDF).
/// The structure tree provides semantic information about document content
/// and accessibility. It's located at /Catalog/StructTreeRoot.
///
/// ISO 32000-2 §14.7 specifies structure tree semantics.
/// </summary>
internal static class PdfStructTreeParser
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Parse the structure tree from the document catalog.
    /// Returns the root element, or null if no /StructTreeRoot defined.
    /// </summary>
    public static PdfStructElement? ParseStructureTree(PdfDocument doc)
    {
        var structRootObj = doc.Catalog.GetOptional("StructTreeRoot");
        if (structRootObj == null) return null;

        var structRootDict = doc.Resolve(structRootObj) as PdfDictionary;
        if (structRootDict == null) return null;

        // The structure tree root has a /K entry (single element or array of top-level elements)
        // Per ISO 32000-2 §14.7.2, /K can be:
        // - A single structure element dict
        // - An array of structure element dicts
        var kObj = structRootDict.GetOptional("K");
        if (kObj == null) return null;

        // For now, treat the root as a synthetic container and return its children.
        // In practice, many PDFs have /K as an array of top-level sections, so we
        // create a synthetic root wrapping them.
        var children = ParseStructureElements(doc, kObj, depth: 0, parentPageNumber: null);
        if (children.Count == 0) return null;

        // If there's only one top-level element, return it directly.
        // Otherwise, wrap in a synthetic root.
        return children.Count == 1
            ? children[0]
            : new PdfStructElement("/Document", children: children, rawDictionary: structRootDict);
    }

    /// <summary>
    /// Parse a structure element or array of elements.
    /// </summary>
    private static IReadOnlyList<PdfStructElement> ParseStructureElements(
        PdfDocument doc,
        PdfObject elementObj,
        int depth,
        int? parentPageNumber)
    {
        if (depth > MaxDepth) return System.Array.Empty<PdfStructElement>();

        // If it's an array, parse each element
        if (elementObj is PdfArray arr)
        {
            var result = new List<PdfStructElement>();
            foreach (var item in arr)
            {
                var elem = ParseSingleStructureElement(doc, item, depth, parentPageNumber);
                if (elem != null)
                    result.Add(elem);
            }
            return result.AsReadOnly();
        }

        // Single element
        var single = ParseSingleStructureElement(doc, elementObj, depth, parentPageNumber);
        return single != null ? new[] { single }.AsReadOnly() : System.Array.Empty<PdfStructElement>();
    }

    /// <summary>
    /// Parse a single structure element dictionary.
    /// </summary>
    private static PdfStructElement? ParseSingleStructureElement(
        PdfDocument doc,
        PdfObject elementObj,
        int depth,
        int? parentPageNumber)
    {
        var elemDict = doc.Resolve(elementObj) as PdfDictionary;
        if (elemDict == null) return null;

        // Get element type (/S)
        var typeObj = elemDict.GetOptional("S");
        if (typeObj == null) return null;

        var type = typeObj switch
        {
            PdfName n => "/" + n.Value,
            _ => null
        };
        if (string.IsNullOrEmpty(type)) return null;

        // Get alternate text (/Alt)
        var altText = elemDict.GetStringOrNull("Alt");

        // Get actual text (/ActualText)
        var actualText = elemDict.GetStringOrNull("ActualText");

        // Get language (/Lang)
        var language = elemDict.GetStringOrNull("Lang");

        // Get associated page number (/P -> /Parent chain and /StructParents array)
        var pageNumber = ExtractPageNumber(doc, elemDict) ?? parentPageNumber;

        // Get marked content IDs from /K (children)
        var (children, mcids) = ParseStructureChildren(doc, elemDict, depth, pageNumber);

        return new PdfStructElement(
            type,
            altText: altText,
            actualText: actualText,
            language: language,
            pageNumber: pageNumber,
            children: children,
            markedContentIds: mcids,
            rawDictionary: elemDict);
    }

    /// <summary>
    /// Parse the /K entry, which contains child elements and/or marked content IDs.
    /// Returns (children elements, mcids).
    ///
    /// /K can be:
    /// - A single structure element
    /// - An array of mixed structure elements and MCIDs
    /// - A single MCID integer (rare, wrapped in array)
    /// </summary>
    private static (IReadOnlyList<PdfStructElement>, IReadOnlyList<int>) ParseStructureChildren(
        PdfDocument doc,
        PdfDictionary elemDict,
        int depth,
        int? pageNumber)
    {
        var kObj = elemDict.GetOptional("K");
        if (kObj == null) return (System.Array.Empty<PdfStructElement>(), System.Array.Empty<int>());

        var childElements = new List<PdfStructElement>();
        var mcids = new List<int>();

        // Resolve references
        var resolvedK = doc.Resolve(kObj);

        if (resolvedK is PdfArray arr)
        {
            // Array of mixed children
            foreach (var item in arr)
            {
                var resolved = doc.Resolve(item);

                // Try to parse as MCID (integer)
                if (resolved is PdfInteger mcidInt)
                {
                    mcids.Add((int)mcidInt.Value);
                    continue;
                }

                // Try to parse as structure element (dict)
                var childElem = ParseSingleStructureElement(doc, item, depth + 1, pageNumber);
                if (childElem != null)
                    childElements.Add(childElem);
            }
        }
        else if (resolvedK is PdfInteger mcidInt2)
        {
            // Single MCID
            mcids.Add((int)mcidInt2.Value);
        }
        else if (resolvedK is PdfDictionary childDict)
        {
            // Single structure element (not wrapped in array)
            var childElem = ParseSingleStructureElement(doc, kObj, depth + 1, pageNumber);
            if (childElem != null)
                childElements.Add(childElem);
        }

        return (childElements.AsReadOnly(), mcids.AsReadOnly());
    }

    /// <summary>
    /// Extract the page number associated with this structure element.
    /// Per ISO 32000-2 §14.7.4, elements can have a /P entry (parent) and ultimately
    /// reference a page via the parent chain. For now, we use a simpler heuristic:
    /// check if there's a direct /P → /Parent → ... chain that reaches a page dict.
    ///
    /// More robust: use /StructParents on the page to reverse-lookup, but that requires
    /// scanning all pages. For read-only, we'll skip this for now.
    /// </summary>
    private static int? ExtractPageNumber(PdfDocument doc, PdfDictionary elemDict)
    {
        // Check if the element itself has a /P (parent) entry
        // In tagged PDFs, structure elements form a tree with /P pointing to parent.
        // The /Parent typically points to the structure root, not directly to a page.
        // To find the actual page, we'd need to walk the page tree and check /StructParents,
        // which is complex. For now, return null — callers can use other methods to
        // determine which page an element affects.
        return null;
    }
}
