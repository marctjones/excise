using System.Text;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Text-engine compatibility members exposed on the document model.
/// The partial type preserves the established public API while tagged-content
/// text resolution remains owned by <c>core-text</c>.
/// </summary>
public partial class PdfDocument
{
    private Dictionary<PdfDictionary, int>? _pagesByDict;

    /// <summary>
    /// Resolve the real body text of a tagged-PDF structure element from its
    /// marked-content references (#776 — the accessibility MCID→letter bridge).
    /// Gathers the extracted <see cref="Excise.Core.Text.Letter"/>s whose /MCID
    /// (and page) match the element's references — both /MCID integers directly
    /// in the element's /K (which belong to the element's own /Pg, or the supplied
    /// <paramref name="inheritedPageNumber"/> when the element has none) and
    /// marked-content-reference (/MCR) child dictionaries (which carry their own
    /// /Pg) — and concatenates them in reference (reading) order.
    ///
    /// <para>
    /// This is how a heading or paragraph with no /ActualText carrier can still
    /// have its real glyphs read in structure order: /ActualText is the author's
    /// explicit replacement text, but most tagged elements have none and their
    /// text lives only in MCID-tagged content. Returns an empty string when the
    /// element references no resolvable marked content (e.g. a /Figure, or an
    /// element whose page cannot be determined).
    /// </para>
    /// </summary>
    public string ResolveStructElementText(
        PdfStructElement element,
        int? inheritedPageNumber = null)
    {
        if (element == null)
            return string.Empty;

        int? elementPage = PageNumberFromPg(element.RawDictionary) ?? inheritedPageNumber;

        // Ordered (page, mcid) references this element points at directly. Child
        // struct elements (/K dicts with their own /S) are NOT descended into —
        // each resolves its own text.
        var refs = new List<(int Page, int Mcid)>();
        CollectMarkedContentRefs(
            element.RawDictionary.GetOptional("K"),
            elementPage,
            refs,
            depth: 0);
        if (refs.Count == 0)
            return string.Empty;

        // Cache each referenced page's letters once.
        var lettersByPage = new Dictionary<int, IReadOnlyList<Excise.Core.Text.Letter>>();
        var sb = new StringBuilder();
        foreach (var (page, mcid) in refs)
        {
            if (page < 1 || page > PageCount)
                continue;
            if (!lettersByPage.TryGetValue(page, out var letters))
                lettersByPage[page] = letters = GetPage(page).Letters;
            foreach (var letter in letters)
            {
                if (letter.MarkedContentId == mcid)
                    sb.Append(letter.Value);
            }
        }
        return sb.ToString();
    }

    private void CollectMarkedContentRefs(
        PdfObject? kObj,
        int? elementPage,
        List<(int Page, int Mcid)> refs,
        int depth)
    {
        if (kObj == null || depth > 64)
            return;

        var resolved = Resolve(kObj);
        switch (resolved)
        {
            case PdfInteger mcidInt when elementPage.HasValue:
                refs.Add((elementPage.Value, (int)mcidInt.Value));
                break;

            case PdfArray arr:
                foreach (var item in arr)
                    CollectMarkedContentRefs(item, elementPage, refs, depth + 1);
                break;

            case PdfDictionary dict:
                // A child struct element (has /S) is a separate element; skip it.
                // A marked-content-reference dict (/MCR, or any /S-less dict with
                // an /MCID) carries the mcid and optionally its own /Pg.
                if (dict.GetOptional("S") != null)
                    break;
                var mcidObj = dict.GetOptional("MCID");
                if (mcidObj != null && Resolve(mcidObj) is PdfInteger mcrMcid)
                {
                    int? refPage = PageNumberFromPg(dict) ?? elementPage;
                    if (refPage.HasValue)
                        refs.Add((refPage.Value, (int)mcrMcid.Value));
                }
                break;
        }
    }

    // Map a dictionary's /Pg entry (a page reference) to its 1-based page number.
    private int? PageNumberFromPg(PdfDictionary dict)
    {
        var pgObj = dict.GetOptional("Pg");
        if (pgObj == null)
            return null;
        if (Resolve(pgObj) is not PdfDictionary pageDict)
            return null;

        if (_pagesByDict == null)
        {
            _pagesByDict = new Dictionary<PdfDictionary, int>();
            for (int i = 1; i <= PageCount; i++)
                _pagesByDict[GetPage(i).Dictionary] = i;
        }
        return _pagesByDict.TryGetValue(pageDict, out int n) ? n : (int?)null;
    }
}
