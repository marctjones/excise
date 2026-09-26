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
    /// marked-content references (#776 — the accessibility MCID→letter bridge):
    /// the text of each <see cref="PdfStructElement.MarkedContent"/> reference,
    /// on its page or else the element's <see cref="PdfStructElement.PageNumber"/>,
    /// concatenated in reference (reading) order.
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
    public string ResolveStructElementText(PdfStructElement element)
    {
        if (element == null)
            return string.Empty;

        // #1485: each referenced page's per-MCID text, not its letters. The
        // map holds, per mcid, exactly the letter-order concatenation the scan
        // below used to append, and it survives across calls where the page's
        // letters no longer do (see PdfPage.GetMarkedContentText).
        var textByPage = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var sb = new StringBuilder();
        foreach (var (page, mcid) in PagedMarkedContent(element))
        {
            if (!textByPage.TryGetValue(page, out var byMcid))
                textByPage[page] = byMcid = GetPage(page).GetMarkedContentText();
            if (byMcid.TryGetValue(mcid, out var text))
                sb.Append(text);
        }
        return sb.ToString();
    }

    private IEnumerable<(int Page, int Mcid)> PagedMarkedContent(PdfStructElement element)
    {
        foreach (var reference in element.MarkedContent)
        {
            if ((reference.PageNumber ?? element.PageNumber) is int page && page >= 1 && page <= PageCount)
                yield return (page, reference.Mcid);
        }
    }
}
