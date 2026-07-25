using Excise.Core.Document;
using Excise.Core.Primitives;
using System;
using System.Collections.Generic;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Accessibility support for the document content (issue #631): reading the
/// tagged-PDF structure tree's textual carriers for the current page —
/// <c>/Alt</c> alternative descriptions (figures/images contribute nothing to
/// the extractable text layer) and <c>/ActualText</c> replacement text
/// (ISO 32000-2 §14.9.4: the author-supplied text that assistive technology
/// should read <em>instead of</em> the raw glyphs, used where glyph
/// extraction is wrong — hyphenation rejoins, ligature or symbol
/// substitutions, stylized text).
///
/// <para>
/// This is deliberately read-only over Excise.Core's public surface
/// (<see cref="PdfDocument.GetStructureTree"/> plus raw-dictionary
/// resolution). Splicing <c>/ActualText</c> into the page text stream
/// in-place (true glyph substitution) and full struct-tree reading order
/// both require mapping marked-content IDs to extracted letters, which the
/// extraction pipeline does not surface yet — those remain follow-up slices
/// of #631. Until then, replacement text is exposed as additional peers with
/// a containment dedup (see <see cref="GetAccessibleActualTexts"/>) so
/// content is not announced twice.
/// </para>
/// </summary>
public partial class PdfViewerControl
{
    // Caches for the current page's structure-tree text carriers, keyed by
    // the same content identity the announced-text dedupe uses: (Document,
    // CurrentPage, RenderVersion). A RenderVersion bump matters here too —
    // redaction scrubs /Alt and /ActualText from the structure tree (#636),
    // and the accessibility tree must not keep announcing redacted text.
    private PdfDocument? _structTextDocument;
    private int _structTextPage = -1;
    private long _structTextRenderVersion = -1;
    private IReadOnlyList<string> _altTextCache = Array.Empty<string>();
    private IReadOnlyList<string> _actualTextCache = Array.Empty<string>();

    /// <summary>
    /// The <c>/Alt</c> alternative descriptions of tagged structure elements
    /// (typically <c>/Figure</c>) associated with the current page, in
    /// structure-tree order. Empty when no document is loaded, the document
    /// is untagged, or the page has no described figures.
    /// </summary>
    internal IReadOnlyList<string> GetAccessibleAltTexts()
    {
        EnsureStructTextCaches();
        return _altTextCache;
    }

    /// <summary>
    /// The <c>/ActualText</c> replacement texts of tagged structure elements
    /// associated with the current page, in structure-tree order — minus any
    /// whose content the extractable text layer already carries.
    ///
    /// <para>
    /// The dedup is containment-based: a replacement text whose
    /// whitespace-normalized content is already a substring of the page's
    /// accessible text is dropped, because announcing it again would double
    /// the content a screen reader hears. What survives is exactly the case
    /// <c>/ActualText</c> exists for: spans where glyph extraction reads
    /// wrong (<c>back- ground</c> vs <c>background</c>, ligature and symbol
    /// substitutions), including pages where extraction fails entirely.
    /// In-place substitution (replacing the raw glyphs inside the text
    /// stream) requires MCID-to-letter mapping from Excise.Core — a
    /// follow-up slice of #631.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> GetAccessibleActualTexts()
    {
        EnsureStructTextCaches();
        return _actualTextCache;
    }

    private void EnsureStructTextCaches()
    {
        var doc = Document;
        int page = CurrentPage;
        long version = RenderVersion;

        if (ReferenceEquals(_structTextDocument, doc)
            && _structTextPage == page
            && _structTextRenderVersion == version)
            return;

        IReadOnlyList<string> alts = Array.Empty<string>();
        IReadOnlyList<string> actuals = Array.Empty<string>();
        if (doc != null && page >= 1 && page <= doc.PageCount)
        {
            try
            {
                (alts, actuals) = CollectStructTextsForPage(doc, page);
                actuals = FilterActualTextsAlreadyInPageText(actuals);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Malformed structure trees must never take down the viewer;
                // accessibility degrades to the text layer alone.
                alts = Array.Empty<string>();
                actuals = Array.Empty<string>();
            }
        }

        _structTextDocument = doc;
        _structTextPage = page;
        _structTextRenderVersion = version;
        _altTextCache = alts;
        _actualTextCache = actuals;
    }

    /// <summary>
    /// Drop replacement texts the extractable text layer already contains
    /// (whitespace-normalized ordinal containment). Case-sensitive on
    /// purpose: a case difference (e.g. small-caps glyphs extracting as
    /// upper case) is itself a correction worth announcing.
    /// </summary>
    private IReadOnlyList<string> FilterActualTextsAlreadyInPageText(
        IReadOnlyList<string> actualTexts)
    {
        if (actualTexts.Count == 0)
            return actualTexts;

        string pageText = NormalizeWhitespace(GetAccessiblePageText());
        if (pageText.Length == 0)
            return actualTexts; // extraction got nothing — every replacement is news

        var kept = new List<string>();
        foreach (var text in actualTexts)
        {
            if (!pageText.Contains(NormalizeWhitespace(text), StringComparison.Ordinal))
                kept.Add(text);
        }
        return kept.Count == 0 ? Array.Empty<string>() : kept;
    }

    private static string NormalizeWhitespace(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (IReadOnlyList<string> Alts, IReadOnlyList<string> ActualTexts)
        CollectStructTextsForPage(PdfDocument doc, int pageNumber)
    {
        var root = doc.GetStructureTree();
        if (root == null)
            return (Array.Empty<string>(), Array.Empty<string>());

        // Page dictionaries resolve through the document's object cache, so
        // reference identity maps a resolved /Pg target back to its number.
        var pagesByDict = new Dictionary<PdfDictionary, int>();
        for (int i = 1; i <= doc.PageCount; i++)
            pagesByDict[doc.GetPage(i).Dictionary] = i;

        var alts = new List<string>();
        var actuals = new List<string>();
        Walk(doc, root, inheritedPage: null, pagesByDict, pageNumber, alts, actuals, depth: 0);
        return (alts.Count == 0 ? Array.Empty<string>() : alts,
                actuals.Count == 0 ? Array.Empty<string>() : actuals);
    }

    private const int MaxStructWalkDepth = 64; // mirrors PdfStructTreeParser.MaxDepth

    private static void Walk(
        PdfDocument doc,
        PdfStructElement element,
        int? inheritedPage,
        Dictionary<PdfDictionary, int> pagesByDict,
        int targetPage,
        List<string> alts,
        List<string> actuals,
        int depth)
    {
        if (depth > MaxStructWalkDepth)
            return;

        int? page = ResolveElementPage(doc, element, pagesByDict) ?? inheritedPage;

        // An element with no determinable page can still be safely announced
        // when there is only one page it could belong to.
        int? effectivePage = page ?? (doc.PageCount == 1 ? 1 : (int?)null);
        if (effectivePage == targetPage)
        {
            if (!string.IsNullOrWhiteSpace(element.AltText))
                alts.Add(element.AltText!.Trim());
            if (!string.IsNullOrWhiteSpace(element.ActualText))
                actuals.Add(element.ActualText!.Trim());
        }

        foreach (var child in element.Children)
            Walk(doc, child, page, pagesByDict, targetPage, alts, actuals, depth + 1);
    }

    /// <summary>
    /// Determine which page a structure element belongs to: its own
    /// <c>/Pg</c>, else the <c>/Pg</c> of a marked-content-reference or
    /// object-reference kid (<c>/MCR</c>/<c>/OBJR</c> dictionaries, which
    /// <c>PdfStructTreeParser</c> does not surface), else null so the caller
    /// falls back to the ancestor's page.
    /// </summary>
    private static int? ResolveElementPage(
        PdfDocument doc,
        PdfStructElement element,
        Dictionary<PdfDictionary, int> pagesByDict)
    {
        int? FromPg(PdfDictionary dict)
        {
            var pgObj = dict.GetOptional("Pg");
            if (pgObj == null)
                return null;
            return doc.Resolve(pgObj) is PdfDictionary pageDict
                && pagesByDict.TryGetValue(pageDict, out int n) ? n : null;
        }

        var own = FromPg(element.RawDictionary);
        if (own != null)
            return own;

        // Reference-kid dictionaries (no /S of their own) carry the /Pg for
        // content the element marks on a page.
        var k = element.RawDictionary.GetOptional("K");
        if (k == null)
            return null;

        var resolvedK = doc.Resolve(k);
        if (resolvedK is PdfDictionary kidDict && kidDict.GetOptional("S") == null)
            return FromPg(kidDict);

        if (resolvedK is PdfArray kids)
        {
            foreach (var item in kids)
            {
                if (doc.Resolve(item) is PdfDictionary refDict
                    && refDict.GetOptional("S") == null)
                {
                    var page = FromPg(refDict);
                    if (page != null)
                        return page;
                }
            }
        }

        return null;
    }
}
