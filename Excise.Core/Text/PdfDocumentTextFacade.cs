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
    /// <summary>
    /// How many pages of one document keep their <see cref="PdfPage.Letters"/>
    /// and <see cref="PdfPage.GetWords"/> cached at once (#1485).
    /// </summary>
    /// <remarks>
    /// Four covers what reads letters repeatedly: the page a selection is on
    /// and its neighbours, the pages a continuous view shows, and the single
    /// page a redaction pass re-reads between rewrites. Everything else pays a
    /// re-walk — measured on irs-1040-instructions.pdf at a median of 1.8 ms
    /// per page, p90 9.2 ms, 0.4 s for all 126 pages — instead of holding
    /// ~0.6 MB of letters per page for the document's lifetime.
    /// </remarks>
    internal const int PageLetterCacheCapacity = 4;

    /// <summary>
    /// Guards every page's text caches and <see cref="_letterCachedPages"/>.
    /// One gate per document, so an eviction can clear another page's cache
    /// without a lock-ordering hazard.
    /// </summary>
    internal readonly object TextCacheGate = new();

    // Pages whose letters are cached, least recently used first. Linear scans
    // are deliberate: the list never holds more than PageLetterCacheCapacity + 1.
    private readonly List<PdfPage> _letterCachedPages = new(PageLetterCacheCapacity + 1);

    /// <summary>
    /// Record that <paramref name="page"/> holds, or just used, its cached
    /// letters, and evict the least recently used pages past the bound.
    /// Caller holds <see cref="TextCacheGate"/>.
    /// </summary>
    internal void MarkPageLettersUsedLocked(PdfPage page)
    {
        var last = _letterCachedPages.Count - 1;
        if (last >= 0 && ReferenceEquals(_letterCachedPages[last], page))
            return;

        _letterCachedPages.Remove(page);
        _letterCachedPages.Add(page);
        while (_letterCachedPages.Count > PageLetterCacheCapacity)
        {
            _letterCachedPages[0].ReleaseLetterCacheLocked();
            _letterCachedPages.RemoveAt(0);
        }
    }

    /// <summary>
    /// Drop <paramref name="page"/> from the bound's bookkeeping after its
    /// caches were invalidated. Caller holds <see cref="TextCacheGate"/>.
    /// </summary>
    internal void ForgetPageLettersLocked(PdfPage page) => _letterCachedPages.Remove(page);

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

        // #1485: each referenced page's per-MCID text, not its letters. The
        // map holds, per mcid, exactly the letter-order concatenation the scan
        // below used to append, and it survives across calls where the page's
        // letters no longer do (see PdfPage.GetMarkedContentText).
        var textByPage = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var sb = new StringBuilder();
        foreach (var (page, mcid) in refs)
        {
            if (page < 1 || page > PageCount)
                continue;
            if (!textByPage.TryGetValue(page, out var byMcid))
                textByPage[page] = byMcid = GetPage(page).GetMarkedContentText();
            if (byMcid.TryGetValue(mcid, out var text))
                sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="ResolveStructElementText"/> as it was before #1485: a scan of
    /// each referenced page's letters per reference. Kept only so tests can
    /// hold the per-MCID map to the letter-derived text it replaced.
    /// </summary>
    internal string ResolveStructElementTextFromLetters(
        PdfStructElement element,
        int? inheritedPageNumber = null)
    {
        if (element == null)
            return string.Empty;

        int? elementPage = PageNumberFromPg(element.RawDictionary) ?? inheritedPageNumber;
        var refs = new List<(int Page, int Mcid)>();
        CollectMarkedContentRefs(element.RawDictionary.GetOptional("K"), elementPage, refs, depth: 0);
        if (refs.Count == 0)
            return string.Empty;

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
        => TryGetPageNumber(dict.GetOptional("Pg"), out var n) ? n : null;
}
