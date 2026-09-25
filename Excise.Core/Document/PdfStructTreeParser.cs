using Excise.Core.Primitives;
using System.Collections.Generic;

namespace Excise.Core.Document;

/// <summary>
/// Parser for the PDF structure tree (tagged PDF) at /Catalog/StructTreeRoot:
/// the one structure model accessibility, text and PDF/UA validation read
/// (ISO 32000-2 §14.7).
/// </summary>
internal sealed class PdfStructTreeParser
{
    private const int MaxDepth = 64;

    /// <summary>The standard structure types (ISO 32000-1 §14.8.4), where role mapping stops.</summary>
    internal static readonly HashSet<string> StandardStructureTypes = new()
    {
        // Grouping
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC",
        "TOCI", "Index", "NonStruct", "Private",
        // Paragraph-like / heading
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6",
        // List
        "L", "LI", "Lbl", "LBody",
        // Table
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        // Inline
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot",
        "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        // Illustration
        "Figure", "Formula", "Form",
    };

    private readonly PdfDocument _doc;
    private readonly Dictionary<string, string> _roleMap = new();
    // A /K that names an ancestor, or one element under two parents, is parsed once.
    private readonly HashSet<PdfDictionary> _visited = new(ReferenceEqualityComparer.Instance);

    private PdfStructTreeParser(PdfDocument doc) => _doc = doc;

    /// <summary>
    /// The structure tree's single top-level element, or a synthetic /Document
    /// wrapping several; null when there is no /StructTreeRoot or no element.
    /// </summary>
    public static PdfStructElement? ParseStructureTree(PdfDocument doc)
    {
        if (doc.Resolve(doc.Catalog.GetOptional("StructTreeRoot") ?? PdfNull.Instance) is not PdfDictionary root)
            return null;

        var parser = new PdfStructTreeParser(doc);
        if (doc.Resolve(root.GetOptional("RoleMap") ?? PdfNull.Instance) is PdfDictionary roleMap)
        {
            foreach (var key in roleMap.Keys)
            {
                if (doc.Resolve(roleMap.GetOptional(key.Value)!) is PdfName target)
                    parser._roleMap[key.Value] = target.Value;
            }
        }

        var children = parser.ParseElements(parser.Kids(root.GetOptional("K")), parentPage: null, depth: 0);
        return children.Count switch
        {
            0 => null,
            1 => children[0],
            _ => new PdfStructElement("/Document", children: children, rawDictionary: root),
        };
    }

    private List<PdfStructElement> ParseElements(IEnumerable<PdfObject> kids, int? parentPage, int depth)
    {
        var elements = new List<PdfStructElement>();
        foreach (var kid in kids)
        {
            if (ParseElement(kid, parentPage, depth) is { } element)
                elements.Add(element);
        }
        return elements;
    }

    private PdfStructElement? ParseElement(PdfObject obj, int? parentPage, int depth)
    {
        if (depth > MaxDepth
            || _doc.Resolve(obj) is not PdfDictionary dict
            || _doc.Resolve(dict.GetOptional("S") ?? PdfNull.Instance) is not PdfName { Value: var type }
            || !_visited.Add(dict))
            return null;

        // /K holds MCIDs (on the element's /Pg), /MCR dictionaries (on their own
        // /Pg, else the element's), /OBJR dictionaries and child elements
        // (ISO 32000-2 Tables 355, 357 and 358).
        int? ownPage = PageOf(dict);
        int? referencePage = null;
        var content = new List<PdfMarkedContentReference>();
        var childKids = new List<PdfObject>();
        foreach (var kid in Kids(dict.GetOptional("K")))
        {
            switch (_doc.Resolve(kid))
            {
                case PdfInteger mcid:
                    content.Add(new PdfMarkedContentReference((int)mcid.Value, ownPage));
                    break;
                case PdfDictionary child when child.ContainsKey("S"):
                    childKids.Add(child);
                    break;
                case PdfDictionary reference:
                    int? page = PageOf(reference);
                    referencePage ??= page;
                    if (_doc.Resolve(reference.GetOptional("MCID") ?? PdfNull.Instance) is PdfInteger mcrMcid)
                        content.Add(new PdfMarkedContentReference((int)mcrMcid.Value, page ?? ownPage));
                    break;
            }
        }

        int? pageNumber = ownPage ?? referencePage ?? parentPage;
        return new PdfStructElement(
            "/" + type,
            altText: dict.GetStringOrNull("Alt"),
            actualText: dict.GetStringOrNull("ActualText"),
            language: dict.GetStringOrNull("Lang"),
            pageNumber: pageNumber,
            children: ParseElements(childKids, pageNumber, depth + 1),
            markedContent: content,
            rawDictionary: dict,
            roleMappedType: "/" + RoleMapped(type));
    }

    /// <summary>
    /// Follow the role map from <paramref name="type"/> until a standard type or
    /// a type already met (ISO 32000-2 §14.7.3). The first step is taken even
    /// from a standard type: a role map entry always applies.
    /// </summary>
    private string RoleMapped(string type)
    {
        if (!_roleMap.ContainsKey(type))
            return type;

        var seen = new HashSet<string>();
        var current = type;
        while (_roleMap.TryGetValue(current, out var next) && seen.Add(current))
        {
            current = next;
            if (StandardStructureTypes.Contains(current))
                break;
        }
        return current;
    }

    private IEnumerable<PdfObject> Kids(PdfObject? k) => _doc.Resolve(k ?? PdfNull.Instance) switch
    {
        PdfArray array => array,
        PdfNull => [],
        var single => [single],
    };

    private int? PageOf(PdfDictionary dict)
        => _doc.TryGetPageNumber(dict.GetOptional("Pg"), out var page) ? page : null;
}
