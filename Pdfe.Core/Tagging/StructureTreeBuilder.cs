using System.Linq;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Tagging;

/// <summary>
/// Builds a PDF logical structure tree (tagged PDF, ISO 32000-2 §14.7-14.8) for
/// accessibility / PDF/UA. Callers allocate a marked-content id (MCID) per tagged
/// run on a page and attach it to a structure element; <see cref="Write"/>
/// serializes the StructTreeRoot, the element tree, the ParentTree number tree,
/// and the catalog entries (/MarkInfo, /StructTreeRoot, /ViewerPreferences).
///
/// <para>Each element's kids are marked-content references (<c>/MCR</c> with an
/// explicit <c>/Pg</c>), so a single logical element can span pages.</para>
/// </summary>
internal sealed class StructureTreeBuilder
{
    private readonly PdfDocument _doc;
    private readonly List<StructElem> _elements = new();
    private readonly Dictionary<int, int> _nextMcid = new();   // pageNumber -> next MCID
    private bool _finalized;

    public StructureTreeBuilder(PdfDocument doc) => _doc = doc;

    /// <summary>A structure element (e.g. H1, P, Table) and its marked-content runs.</summary>
    internal sealed class StructElem
    {
        public string Type = "P";
        public readonly List<(int Page, int Mcid)> Content = new();
    }

    /// <summary>Register a new structure element of the given type (child of Document).</summary>
    public StructElem AddElement(string structType)
    {
        var e = new StructElem { Type = structType };
        _elements.Add(e);
        return e;
    }

    /// <summary>Allocate the next MCID on a page and record it on the element.</summary>
    public int AllocateMcid(StructElem element, int pageNumber)
    {
        int mcid = _nextMcid.TryGetValue(pageNumber, out var n) ? n : 0;
        _nextMcid[pageNumber] = mcid + 1;
        element.Content.Add((pageNumber, mcid));
        return mcid;
    }

    public bool HasContent => _elements.Any(e => e.Content.Count > 0);

    /// <summary>Serialize the structure tree into the document. Idempotent.</summary>
    public void Write()
    {
        if (_finalized || !HasContent) return;
        _finalized = true;

        var root = new PdfDictionary();
        root.SetName("Type", "StructTreeRoot");
        var rootRef = _doc.AddIndirectObject(root);

        var docElem = new PdfDictionary();
        docElem.SetName("Type", "StructElem");
        docElem.SetName("S", "Document");
        docElem["P"] = rootRef;
        var docRef = _doc.AddIndirectObject(docElem);
        var rootKids = new PdfArray();
        rootKids.Add((PdfObject)docRef);
        root["K"] = rootKids;

        // pageNumber -> (mcid -> owning struct elem ref), for the ParentTree.
        var perPage = new Dictionary<int, Dictionary<int, PdfReference>>();
        var docKids = new PdfArray();

        foreach (var e in _elements.Where(e => e.Content.Count > 0))
        {
            var se = new PdfDictionary();
            se.SetName("Type", "StructElem");
            se.SetName("S", e.Type);
            se["P"] = docRef;
            var seRef = _doc.AddIndirectObject(se);

            var kids = new PdfArray();
            foreach (var (page, mcid) in e.Content)
            {
                var pgRef = _doc.GetPageReference(page);
                var mcr = new PdfDictionary();
                mcr.SetName("Type", "MCR");
                if (pgRef != null) mcr["Pg"] = pgRef;
                mcr.SetInt("MCID", mcid);
                kids.Add((PdfObject)mcr);

                if (!perPage.TryGetValue(page, out var map)) perPage[page] = map = new();
                map[mcid] = seRef;
            }
            se["K"] = kids.Count == 1 ? kids[0] : kids;
            docKids.Add((PdfObject)seRef);
        }
        docElem["K"] = docKids;

        // ParentTree: number tree keyed by each page's /StructParents index;
        // value is an array indexed by MCID -> owning struct element.
        var nums = new PdfArray();
        int nextKey = 0;
        foreach (var page in perPage.Keys.OrderBy(p => p))
        {
            int key = nextKey++;
            _doc.GetPage(page).Dictionary.SetInt("StructParents", key);
            var map = perPage[page];
            int maxMcid = map.Keys.Max();
            var arr = new PdfArray();
            for (int i = 0; i <= maxMcid; i++)
                arr.Add(map.TryGetValue(i, out var r) ? (PdfObject)r : PdfNull.Instance);
            nums.Add(key);
            nums.Add((PdfObject)arr);
        }
        var parentTree = new PdfDictionary();
        parentTree["Nums"] = nums;
        root["ParentTree"] = _doc.AddIndirectObject(parentTree);
        root.SetInt("ParentTreeNextKey", nextKey);

        // Catalog: mark as tagged, link the tree, and (PDF/UA) show the doc title.
        _doc.Catalog["StructTreeRoot"] = rootRef;
        var markInfo = new PdfDictionary();
        markInfo.SetBool("Marked", true);
        _doc.Catalog["MarkInfo"] = markInfo;
        if (!_doc.Catalog.ContainsKey("ViewerPreferences"))
        {
            var vp = new PdfDictionary();
            vp.SetBool("DisplayDocTitle", true);
            _doc.Catalog["ViewerPreferences"] = vp;
        }
    }
}
