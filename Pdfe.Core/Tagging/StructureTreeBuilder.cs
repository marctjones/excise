using System.Linq;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Tagging;

/// <summary>
/// Builds a PDF logical structure tree (tagged PDF, ISO 32000-2 §14.7-14.8) for
/// accessibility / PDF/UA. Callers either allocate a marked-content id (MCID) per
/// tagged run on a page (text content) or attach an object reference (a widget
/// annotation), and <see cref="Write"/> serializes the StructTreeRoot, the element
/// tree, the ParentTree number tree, and the catalog entries (/MarkInfo,
/// /StructTreeRoot, /ViewerPreferences).
///
/// <para>Element kids are marked-content references (<c>/MCR</c> with explicit
/// <c>/Pg</c>, so an element can span pages) and/or object references
/// (<c>/OBJR</c> for form-field widgets).</para>
/// </summary>
internal sealed class StructureTreeBuilder
{
    private readonly PdfDocument _doc;
    private readonly List<StructElem> _elements = new();
    private readonly Dictionary<int, int> _nextMcid = new();   // pageNumber -> next MCID
    private bool _finalized;

    public StructureTreeBuilder(PdfDocument doc) => _doc = doc;

    /// <summary>A structure element (e.g. H1, P, Table, Form) and its content.</summary>
    internal sealed class StructElem
    {
        public string Type = "P";
        public readonly List<(int Page, int Mcid)> Content = new();                    // /MCR runs
        public readonly List<(int Page, PdfReference Obj, PdfDictionary Dict)> Objects = new(); // /OBJR widgets
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

    /// <summary>
    /// Register a structure element that references a widget annotation (e.g. a
    /// form field) via /OBJR. The widget gets a /StructParent at <see cref="Write"/>.
    /// </summary>
    public void AddObjectElement(string structType, int pageNumber, PdfReference widgetRef, PdfDictionary widgetDict)
    {
        var e = new StructElem { Type = structType };
        e.Objects.Add((pageNumber, widgetRef, widgetDict));
        _elements.Add(e);
    }

    public bool HasContent => _elements.Any(e => e.Content.Count > 0 || e.Objects.Count > 0);

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

        // pageNumber -> (mcid -> owning struct elem ref) for the ParentTree;
        // and a flat list of (widgetDict -> owning struct elem ref) for objects.
        var perPage = new Dictionary<int, Dictionary<int, PdfReference>>();
        var objectOwners = new List<(PdfDictionary Dict, PdfReference Elem)>();
        var docKids = new PdfArray();

        foreach (var e in _elements.Where(x => x.Content.Count > 0 || x.Objects.Count > 0))
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
            foreach (var (page, obj, dict) in e.Objects)
            {
                var pgRef = _doc.GetPageReference(page);
                var objr = new PdfDictionary();
                objr.SetName("Type", "OBJR");
                if (pgRef != null) objr["Pg"] = pgRef;
                objr["Obj"] = obj;
                kids.Add((PdfObject)objr);
                objectOwners.Add((dict, seRef));
            }
            se["K"] = kids.Count == 1 ? kids[0] : kids;
            docKids.Add((PdfObject)seRef);
        }
        docElem["K"] = docKids;

        // ParentTree: one number-tree key per page (StructParents -> array by MCID)
        // and per widget (StructParent -> the owning element ref).
        var entries = new List<(int Key, PdfObject Value)>();
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
            entries.Add((key, arr));
        }
        foreach (var (dict, elem) in objectOwners)
        {
            int key = nextKey++;
            dict.SetInt("StructParent", key);
            entries.Add((key, elem));
        }

        var nums = new PdfArray();
        foreach (var (key, value) in entries.OrderBy(e => e.Key))
        {
            nums.Add(key);
            nums.Add(value);
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
