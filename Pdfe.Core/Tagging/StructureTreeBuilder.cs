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

    /// <summary>A structure element (e.g. H1, P, Table, TR, TD, Form) and its content.</summary>
    internal sealed class StructElem
    {
        public string Type = "P";
        public readonly List<StructElem> Children = new();                             // nested elems
        public readonly List<(int Page, int Mcid)> Content = new();                    // /MCR runs
        public readonly List<(int Page, PdfReference Obj, PdfDictionary Dict)> Objects = new(); // /OBJR widgets
    }

    /// <summary>
    /// Register a new structure element. With no <paramref name="parent"/> it's a
    /// child of Document; otherwise it nests under the parent (e.g. Table→TR→TD).
    /// </summary>
    public StructElem AddElement(string structType, StructElem? parent = null)
    {
        var e = new StructElem { Type = structType };
        if (parent != null) parent.Children.Add(e);
        else _elements.Add(e);
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

    private static bool ElemHasContent(StructElem e) =>
        e.Content.Count > 0 || e.Objects.Count > 0 || e.Children.Any(ElemHasContent);

    public bool HasContent => _elements.Any(ElemHasContent);

    /// <summary>Recursively serialize an element (and its children); returns its ref.</summary>
    private PdfReference WriteElem(StructElem e, PdfReference parentRef)
    {
        var se = new PdfDictionary();
        se.SetName("Type", "StructElem");
        se.SetName("S", e.Type);
        se["P"] = parentRef;
        // PDF/UA 7.5: header cells need a Scope so cell associations are
        // determinable. Header-row cells are column headers.
        if (e.Type == "TH")
        {
            var attr = new PdfDictionary();
            attr.SetName("O", "Table");
            attr.SetName("Scope", "Column");
            se["A"] = attr;
        }
        var seRef = _doc.AddIndirectObject(se);

        var kids = new PdfArray();
        foreach (var child in e.Children.Where(ElemHasContent))
            kids.Add((PdfObject)WriteElem(child, seRef));
        foreach (var (page, mcid) in e.Content)
        {
            var pgRef = _doc.GetPageReference(page);
            var mcr = new PdfDictionary();
            mcr.SetName("Type", "MCR");
            if (pgRef != null) mcr["Pg"] = pgRef;
            mcr.SetInt("MCID", mcid);
            kids.Add((PdfObject)mcr);
            if (!_perPage.TryGetValue(page, out var map)) _perPage[page] = map = new();
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
            _objectOwners.Add((dict, seRef));
        }
        se["K"] = kids.Count == 1 ? kids[0] : kids;
        return seRef;
    }

    // Populated during Write() recursion.
    private readonly Dictionary<int, Dictionary<int, PdfReference>> _perPage = new();
    private readonly List<(PdfDictionary Dict, PdfReference Elem)> _objectOwners = new();

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

        // Build the element tree recursively (populates _perPage + _objectOwners).
        var docKids = new PdfArray();
        foreach (var e in _elements.Where(ElemHasContent))
            docKids.Add((PdfObject)WriteElem(e, docRef));
        docElem["K"] = docKids;
        var perPage = _perPage;
        var objectOwners = _objectOwners;

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

        WriteXmpMetadata();
    }

    /// <summary>
    /// Write an XMP metadata stream into the catalog (PDF/UA 7.1: required, with
    /// the pdfuaid identifier; mirrors dc:title / dc:language from the document).
    /// </summary>
    private void WriteXmpMetadata()
    {
        if (_doc.Catalog.ContainsKey("Metadata")) return;
        string title = XmlEscape(_doc.Title ?? string.Empty);
        string lang = XmlEscape(_doc.Language ?? "en-US");
        string xmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            "  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n" +
            (title.Length > 0
                ? $"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>\n"
                : "") +
            $"   <dc:language><rdf:Bag><rdf:li>{lang}</rdf:li></rdf:Bag></dc:language>\n" +
            "   <pdfuaid:part>1</pdfuaid:part>\n" +
            "  </rdf:Description>\n </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>";

        var bytes = System.Text.Encoding.UTF8.GetBytes(xmp);
        var dict = new PdfDictionary();
        dict.SetName("Type", "Metadata");
        dict.SetName("Subtype", "XML");
        dict.SetInt("Length", bytes.Length);   // unfiltered plaintext, per spec
        _doc.Catalog["Metadata"] = _doc.AddIndirectObject(new PdfStream(dict, bytes));
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
