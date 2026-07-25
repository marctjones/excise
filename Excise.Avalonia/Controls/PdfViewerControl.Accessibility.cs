using Excise.Core.Document;
using Excise.Core.Primitives;
using System;
using System.Collections.Generic;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Accessibility support for the document content (issue #631): reading the
/// tagged-PDF structure tree's <c>/Alt</c> alternative descriptions for the
/// current page so figures/images — which contribute nothing to the
/// extractable text layer — are not silent to screen readers.
///
/// <para>
/// This is deliberately read-only over Excise.Core's public surface
/// (<see cref="PdfDocument.GetStructureTree"/> plus raw-dictionary
/// resolution). Full struct-tree reading order (re-ordering the text layer
/// by logical structure) additionally requires mapping marked-content IDs
/// to extracted letters, which the extraction pipeline does not surface yet
/// — that remains a follow-up slice of #631.
/// </para>
/// </summary>
public partial class PdfViewerControl
{
    // Cache for the current page's /Alt descriptions, keyed by the same
    // content identity the announced-text dedupe uses: (Document,
    // CurrentPage, RenderVersion). A RenderVersion bump matters here too —
    // redaction scrubs /Alt from the structure tree (#636), and the
    // accessibility tree must not keep announcing redacted descriptions.
    private PdfDocument? _altTextDocument;
    private int _altTextPage = -1;
    private long _altTextRenderVersion = -1;
    private IReadOnlyList<string>? _altTextCache;

    /// <summary>
    /// The <c>/Alt</c> alternative descriptions of tagged structure elements
    /// (typically <c>/Figure</c>) associated with the current page, in
    /// structure-tree order. Empty when no document is loaded, the document
    /// is untagged, or the page has no described figures.
    /// </summary>
    internal IReadOnlyList<string> GetAccessibleAltTexts()
    {
        var doc = Document;
        if (doc == null || CurrentPage < 1 || CurrentPage > doc.PageCount)
            return Array.Empty<string>();

        if (ReferenceEquals(_altTextDocument, doc)
            && _altTextPage == CurrentPage
            && _altTextRenderVersion == RenderVersion
            && _altTextCache != null)
            return _altTextCache;

        IReadOnlyList<string> result;
        try
        {
            result = CollectAltTextsForPage(doc, CurrentPage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed structure trees must never take down the viewer;
            // accessibility degrades to the text layer alone.
            result = Array.Empty<string>();
        }

        _altTextDocument = doc;
        _altTextPage = CurrentPage;
        _altTextRenderVersion = RenderVersion;
        _altTextCache = result;
        return result;
    }

    private static IReadOnlyList<string> CollectAltTextsForPage(PdfDocument doc, int pageNumber)
    {
        var root = doc.GetStructureTree();
        if (root == null)
            return Array.Empty<string>();

        // Page dictionaries resolve through the document's object cache, so
        // reference identity maps a resolved /Pg target back to its number.
        var pagesByDict = new Dictionary<PdfDictionary, int>();
        for (int i = 1; i <= doc.PageCount; i++)
            pagesByDict[doc.GetPage(i).Dictionary] = i;

        var results = new List<string>();
        Walk(doc, root, inheritedPage: null, pagesByDict, pageNumber, results, depth: 0);
        return results.Count == 0 ? Array.Empty<string>() : results;
    }

    private const int MaxStructWalkDepth = 64; // mirrors PdfStructTreeParser.MaxDepth

    private static void Walk(
        PdfDocument doc,
        PdfStructElement element,
        int? inheritedPage,
        Dictionary<PdfDictionary, int> pagesByDict,
        int targetPage,
        List<string> results,
        int depth)
    {
        if (depth > MaxStructWalkDepth)
            return;

        int? page = ResolveElementPage(doc, element, pagesByDict) ?? inheritedPage;

        if (!string.IsNullOrWhiteSpace(element.AltText))
        {
            // An element with no determinable page can still be safely
            // announced when there is only one page it could belong to.
            int? effectivePage = page ?? (doc.PageCount == 1 ? 1 : (int?)null);
            if (effectivePage == targetPage)
                results.Add(element.AltText!.Trim());
        }

        foreach (var child in element.Children)
            Walk(doc, child, page, pagesByDict, targetPage, results, depth + 1);
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
