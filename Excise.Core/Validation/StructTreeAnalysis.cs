using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Validation;

/// <summary>
/// A single node in a structure tree, as seen by the validator: the raw <c>/S</c>
/// type, the type after <c>/RoleMap</c> resolution, accessibility text, and the
/// marked-content this element owns. Deliberately independent of
/// <see cref="Document.PdfStructTreeParser"/> — that parser drops <c>/MCR</c>
/// marked-content references and does not apply the role map, both of which the
/// PDF/UA rules depend on.
/// </summary>
internal sealed class StructNode
{
    public string RawType = "";
    public string ResolvedType = "";
    public string? Alt;
    public string? ActualText;
    public string? Lang;
    public readonly List<StructNode> Children = new();
    /// <summary>(page number 1-based or -1 unknown, MCID) pairs this element owns.</summary>
    public readonly List<(int Page, int Mcid)> Content = new();
    public PdfDictionary Dict = new();

    /// <summary>Depth-first enumeration of this node and all descendants.</summary>
    public IEnumerable<StructNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var n in c.DescendantsAndSelf())
                yield return n;
    }
}

/// <summary>
/// Parses a document's structure tree into <see cref="StructNode"/>s with role-map
/// resolution and marked-content collection, and answers the questions the PDF/UA
/// rules ask. Read-only over <see cref="PdfDocument"/>.
/// </summary>
internal sealed class StructTreeAnalysis
{
    private const int MaxDepth = 64;

    private readonly PdfDocument _doc;
    private readonly Dictionary<string, string> _roleMap = new();

    /// <summary>The synthetic roots (top-level /K of the StructTreeRoot).</summary>
    public IReadOnlyList<StructNode> Roots { get; }

    /// <summary>The StructTreeRoot dictionary, or null when absent.</summary>
    public PdfDictionary? StructTreeRoot { get; }

    /// <summary>Distinct raw <c>/S</c> types that are neither standard nor in the role map.</summary>
    public IReadOnlyCollection<string> UnmappedCustomTypes { get; }

    private StructTreeAnalysis(
        PdfDocument doc,
        PdfDictionary? root,
        IReadOnlyList<StructNode> roots,
        IReadOnlyCollection<string> unmapped)
    {
        _doc = doc;
        StructTreeRoot = root;
        Roots = roots;
        UnmappedCustomTypes = unmapped;
    }

    /// <summary>Parse the structure tree. <see cref="StructTreeRoot"/> is null if none.</summary>
    public static StructTreeAnalysis Build(PdfDocument doc)
    {
        var rootDict = doc.Resolve(doc.Catalog.GetOptional("StructTreeRoot") ?? PdfNull.Instance) as PdfDictionary;
        var analysis = new StructTreeAnalysis(doc, rootDict, System.Array.Empty<StructNode>(), System.Array.Empty<string>());
        if (rootDict == null)
            return analysis;

        analysis.LoadRoleMap(rootDict);

        var roots = new List<StructNode>();
        var kObj = rootDict.GetOptional("K");
        if (kObj != null)
            analysis.AppendChildren(doc.Resolve(kObj), roots, depth: 0);

        var unmapped = new HashSet<string>();
        foreach (var node in roots.SelectMany(r => r.DescendantsAndSelf()))
        {
            if (!StandardStructureTypes.Contains(node.RawType) &&
                !analysis._roleMap.ContainsKey(node.RawType))
            {
                unmapped.Add(node.RawType);
            }
        }

        return new StructTreeAnalysis(doc, rootDict, roots, unmapped);
    }

    private void LoadRoleMap(PdfDictionary rootDict)
    {
        if (_doc.Resolve(rootDict.GetOptional("RoleMap") ?? PdfNull.Instance) is not PdfDictionary rm)
            return;
        foreach (var key in rm.Keys)
        {
            if (_doc.Resolve(rm.GetOptional(key.Value)!) is PdfName target)
                _roleMap[key.Value] = target.Value;
        }
    }

    /// <summary>Resolve a raw type through the role map to a standard type (transitively, cycle-guarded).</summary>
    private string Resolve(string rawType)
    {
        var seen = new HashSet<string>();
        var cur = rawType;
        while (_roleMap.TryGetValue(cur, out var next) && seen.Add(cur))
            cur = next;
        return cur;
    }

    private void AppendChildren(PdfObject kResolved, List<StructNode> into, int depth)
    {
        if (depth > MaxDepth) return;
        if (kResolved is PdfArray arr)
        {
            foreach (var item in arr)
            {
                var node = ParseElement(_doc.Resolve(item), depth);
                if (node != null) into.Add(node);
            }
        }
        else
        {
            var node = ParseElement(kResolved, depth);
            if (node != null) into.Add(node);
        }
    }

    /// <summary>
    /// Parse a single object under a /K. Returns a node only for structure
    /// element dictionaries (those with an /S). MCR / OBJR / bare-integer kids
    /// are consumed by the parent (see <see cref="ParseKids"/>), not here.
    /// </summary>
    private StructNode? ParseElement(PdfObject obj, int depth)
    {
        if (obj is not PdfDictionary dict) return null;
        if (_doc.Resolve(dict.GetOptional("S") ?? PdfNull.Instance) is not PdfName sName) return null;

        var node = new StructNode
        {
            RawType = sName.Value,
            ResolvedType = Resolve(sName.Value),
            Alt = dict.GetStringOrNull("Alt"),
            ActualText = dict.GetStringOrNull("ActualText"),
            Lang = dict.GetStringOrNull("Lang"),
            Dict = dict,
        };

        int elementPage = ResolvePageNumber(dict.GetOptional("Pg"));
        ParseKids(dict.GetOptional("K"), node, elementPage, depth);
        return node;
    }

    /// <summary>
    /// Walk an element's /K: nested elements recurse; integers are MCIDs on the
    /// element's page; /MCR dicts carry their own /MCID (+ optional /Pg); /OBJR
    /// dicts reference annotations (ignored for these rules).
    /// </summary>
    private void ParseKids(PdfObject? kObj, StructNode node, int elementPage, int depth)
    {
        if (kObj == null) return;
        var resolved = _doc.Resolve(kObj);
        if (resolved is PdfArray arr)
        {
            foreach (var item in arr)
                ParseKid(_doc.Resolve(item), node, elementPage, depth);
        }
        else
        {
            ParseKid(resolved, node, elementPage, depth);
        }
    }

    private void ParseKid(PdfObject kid, StructNode node, int elementPage, int depth)
    {
        switch (kid)
        {
            case PdfInteger i:
                node.Content.Add((elementPage, (int)i.Value));
                break;
            case PdfDictionary d when GetName(d, "Type") == "MCR":
            {
                int page = d.ContainsKey("Pg") ? ResolvePageNumber(d.GetOptional("Pg")) : elementPage;
                if (_doc.Resolve(d.GetOptional("MCID") ?? PdfNull.Instance) is PdfInteger mcid)
                    node.Content.Add((page, (int)mcid.Value));
                break;
            }
            case PdfDictionary d when GetName(d, "Type") == "OBJR":
                break; // annotation reference — outside the text-content rules
            case PdfDictionary d when d.ContainsKey("S"):
            {
                var child = ParseElement(d, depth + 1);
                if (child != null) node.Children.Add(child);
                break;
            }
        }
    }

    private string? GetName(PdfDictionary d, string key) =>
        _doc.Resolve(d.GetOptional(key) ?? PdfNull.Instance) is PdfName n ? n.Value : null;

    /// <summary>Map a /Pg page reference to a 1-based page number, or -1 if unknown.</summary>
    private int ResolvePageNumber(PdfObject? pgObj)
    {
        if (pgObj is not PdfReference pgRef) return -1;
        for (int p = 1; p <= _doc.PageCount; p++)
        {
            if (_doc.GetPageReference(p) is { } r && r.Equals(pgRef))
                return p;
        }
        return -1;
    }

    /// <summary>
    /// The set of (page, MCID) pairs referenced anywhere in the structure tree —
    /// i.e. content that IS tagged. A pair with page -1 (unknown page) is treated
    /// as page-agnostic by <see cref="IsTagged"/> so single-page documents, where
    /// page qualification is unambiguous, still match.
    /// </summary>
    public (HashSet<(int, int)> Qualified, HashSet<int> PageAgnostic) TaggedContent()
    {
        var qualified = new HashSet<(int, int)>();
        var agnostic = new HashSet<int>();
        foreach (var node in Roots.SelectMany(r => r.DescendantsAndSelf()))
        {
            foreach (var (page, mcid) in node.Content)
            {
                if (page < 0) agnostic.Add(mcid);
                else qualified.Add((page, mcid));
            }
        }
        return (qualified, agnostic);
    }

    /// <summary>The standard PDF structure types (ISO 32000-1 §14.8.4).</summary>
    public static readonly HashSet<string> StandardStructureTypes = new()
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
}
