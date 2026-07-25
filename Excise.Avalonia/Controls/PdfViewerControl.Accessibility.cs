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

    // ── struct-tree reading order + role layer (#631) ────────────────────
    // Doc-wide (not per-page) caches: keyboard structure navigation crosses
    // page boundaries, so the ordered node list must span the whole document.
    // Keyed by (Document, RenderVersion) — a RenderVersion bump means the
    // structure tree may have been rewritten (redaction scrubs /Alt and
    // /ActualText, #636), so the model must be rebuilt and stale roles dropped.
    private PdfDocument? _structNavDocument;
    private long _structNavRenderVersion = -1;
    // Reading-order text carriers (elements with /ActualText) in document
    // order, tagged with their page. This is the ONLY per-element text the
    // read-only Excise.Core surface exposes: mapping marked-content IDs back
    // to the extracted glyphs (so a heading's real body text could be read in
    // struct order) needs Excise.Core to surface MCID→letter mapping, which it
    // does not yet — a follow-up slice of #631.
    private IReadOnlyList<(int Page, string Text)> _structReadingParts =
        Array.Empty<(int, string)>();
    // Structurally significant elements (headings, lists, tables) in document
    // order, for the role automation peers and keyboard navigation.
    private IReadOnlyList<AccessibleStructNode> _structNodes =
        Array.Empty<AccessibleStructNode>();
    // Index into _structNodes the last structure-navigation keystroke landed
    // on, or -1 before any navigation.
    private int _structNavCursor = -1;

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

    // ── struct-tree reading order + role model (#631) ────────────────────

    /// <summary>
    /// The current page's text in reading order, ordered by the structure
    /// tree when the tagged PDF supplies orderable text (issue #631). Uses the
    /// document-order sequence of <c>/ActualText</c> carriers where present;
    /// otherwise falls back to the geometric reading-order text (see
    /// <see cref="GetAccessiblePageText"/>). Struct order is used only when it
    /// actually carries text: without MCID-to-letter mapping (a follow-up
    /// slice of #631) a heading's body glyphs cannot be read in struct order,
    /// so a tagged PDF that supplies no <c>/ActualText</c> reads geometrically.
    /// </summary>
    internal string GetAccessibleReadingOrderText()
    {
        EnsureStructNavModel();

        int page = CurrentPage;
        List<string>? parts = null;
        foreach (var (p, text) in _structReadingParts)
        {
            if (p != page)
                continue;
            (parts ??= new List<string>()).Add(text);
        }

        return parts is { Count: > 0 }
            ? string.Join(" ", parts)
            : GetAccessiblePageText();
    }

    /// <summary>
    /// The structurally significant elements (headings, lists and list items,
    /// tables/rows/cells) on the current page in document order, each carrying
    /// its role and any text carrier (<c>/ActualText</c> then <c>/Alt</c>).
    /// Figures are deliberately excluded — they are already exposed as image
    /// description peers via <see cref="GetAccessibleAltTexts"/> — so a screen
    /// reader never hears a figure twice. Empty when the document is untagged.
    /// </summary>
    internal IReadOnlyList<AccessibleStructNode> GetAccessibleStructRoleNodes()
    {
        EnsureStructNavModel();

        int page = CurrentPage;
        List<AccessibleStructNode>? nodes = null;
        foreach (var node in _structNodes)
        {
            if (node.Page != page || node.Role == AccessibleStructRole.Figure)
                continue;
            (nodes ??= new List<AccessibleStructNode>()).Add(node);
        }

        return (IReadOnlyList<AccessibleStructNode>?)nodes
            ?? Array.Empty<AccessibleStructNode>();
    }

    /// <summary>
    /// The structure element the last structure-navigation keystroke landed
    /// on (see <see cref="MoveToNextStructure"/>), or null before any
    /// navigation. Exposed for assistive-technology announcement and tests.
    /// </summary>
    internal AccessibleStructNode? CurrentStructureNavigationTarget =>
        _structNavCursor >= 0 && _structNavCursor < _structNodes.Count
            ? _structNodes[_structNavCursor]
            : null;

    /// <summary>
    /// Move the structure-navigation cursor to the next (or previous) node,
    /// optionally restricted to headings — the "next/previous heading"
    /// screen-reader convention (issue #631). Crosses page boundaries: if the
    /// target lives on another page the current page is changed so the landed
    /// element is on screen. Returns true when the cursor moved.
    /// </summary>
    /// <param name="backward">Search toward the document start.</param>
    /// <param name="headingsOnly">Only stop on heading elements.</param>
    internal bool MoveToNextStructure(bool backward, bool headingsOnly)
    {
        EnsureStructNavModel();
        if (_structNodes.Count == 0)
            return false;

        int step = backward ? -1 : 1;
        for (int i = _structNavCursor + step; i >= 0 && i < _structNodes.Count; i += step)
        {
            var node = _structNodes[i];
            if (node.Role == AccessibleStructRole.Figure)
                continue; // navigable structure, not a figure description
            if (headingsOnly && node.Role != AccessibleStructRole.Heading)
                continue;

            _structNavCursor = i;
            if (node.Page >= 1 && node.Page <= (Document?.PageCount ?? 0)
                && node.Page != CurrentPage)
                CurrentPage = node.Page;

            // Tell assistive technology the accessible content the user is now
            // "on" changed, so it re-reads the role-peer set for this page.
            (global::Avalonia.Automation.Peers.ControlAutomationPeer.FromElement(this)
                as Excise.Avalonia.Automation.PdfViewerAutomationPeer)
                ?.NotifyPageTextChanged();
            return true;
        }

        return false; // already at the last (or first) matching element
    }

    private void EnsureStructNavModel()
    {
        var doc = Document;
        long version = RenderVersion;

        if (ReferenceEquals(_structNavDocument, doc) && _structNavRenderVersion == version)
            return;

        IReadOnlyList<(int, string)> reading = Array.Empty<(int, string)>();
        IReadOnlyList<AccessibleStructNode> nodes = Array.Empty<AccessibleStructNode>();
        if (doc != null && doc.PageCount > 0)
        {
            try
            {
                (reading, nodes) = CollectStructModel(doc);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed structure tree must never take the viewer down;
                // structure navigation degrades to nothing.
                reading = Array.Empty<(int, string)>();
                nodes = Array.Empty<AccessibleStructNode>();
            }
        }

        _structNavDocument = doc;
        _structNavRenderVersion = version;
        _structReadingParts = reading;
        _structNodes = nodes;
        _structNavCursor = -1; // a new model invalidates the navigation cursor
    }

    private static (IReadOnlyList<(int, string)> Reading,
                    IReadOnlyList<AccessibleStructNode> Nodes)
        CollectStructModel(PdfDocument doc)
    {
        var root = doc.GetStructureTree();
        if (root == null)
            return (Array.Empty<(int, string)>(), Array.Empty<AccessibleStructNode>());

        var pagesByDict = new Dictionary<PdfDictionary, int>();
        for (int i = 1; i <= doc.PageCount; i++)
            pagesByDict[doc.GetPage(i).Dictionary] = i;

        var reading = new List<(int, string)>();
        var nodes = new List<AccessibleStructNode>();
        WalkModel(doc, root, inheritedPage: null, pagesByDict, reading, nodes, depth: 0);
        return (reading.Count == 0 ? Array.Empty<(int, string)>() : reading,
                nodes.Count == 0 ? Array.Empty<AccessibleStructNode>() : nodes);
    }

    private static void WalkModel(
        PdfDocument doc,
        PdfStructElement element,
        int? inheritedPage,
        Dictionary<PdfDictionary, int> pagesByDict,
        List<(int, string)> reading,
        List<AccessibleStructNode> nodes,
        int depth)
    {
        if (depth > MaxStructWalkDepth)
            return;

        int? page = ResolveElementPage(doc, element, pagesByDict) ?? inheritedPage;
        int effectivePage = page ?? (doc.PageCount == 1 ? 1 : 0);

        if (effectivePage >= 1)
        {
            // Reading order: /ActualText is the author's "read this instead of
            // the glyphs" carrier, so it is the only content safe to splice
            // into linear reading order. /Alt is a supplementary description
            // (exposed separately as an image peer), never linear text.
            if (!string.IsNullOrWhiteSpace(element.ActualText))
                reading.Add((effectivePage, element.ActualText!.Trim()));

            var (role, headingLevel) = ClassifyStructRole(element.Type);
            if (role != AccessibleStructRole.Generic)
            {
                string text = !string.IsNullOrWhiteSpace(element.ActualText)
                    ? element.ActualText!.Trim()
                    : (!string.IsNullOrWhiteSpace(element.AltText)
                        ? element.AltText!.Trim()
                        : string.Empty);
                nodes.Add(new AccessibleStructNode(role, headingLevel, text, effectivePage));
            }
        }

        foreach (var child in element.Children)
            WalkModel(doc, child, page, pagesByDict, reading, nodes, depth + 1);
    }

    /// <summary>
    /// Map a structure element type (ISO 32000-2 §14.8.4 standard structure
    /// types, with the leading <c>/</c> that <c>PdfStructTreeParser</c>
    /// prepends) to an accessibility role. Bare <c>H</c> and numbered
    /// <c>H1</c>–<c>H6</c> both map to <see cref="AccessibleStructRole.Heading"/>.
    /// </summary>
    private static (AccessibleStructRole Role, int HeadingLevel) ClassifyStructRole(string type)
    {
        string t = type.TrimStart('/');
        if (t.Length == 0)
            return (AccessibleStructRole.Generic, 0);

        if (t == "H")
            return (AccessibleStructRole.Heading, 0);
        if (t.Length >= 2 && t[0] == 'H' && char.IsDigit(t[1])
            && int.TryParse(t.AsSpan(1), out int level) && level is >= 1 and <= 6)
            return (AccessibleStructRole.Heading, level);

        return t switch
        {
            "L" => (AccessibleStructRole.List, 0),
            "LI" => (AccessibleStructRole.ListItem, 0),
            "Table" => (AccessibleStructRole.Table, 0),
            "TR" => (AccessibleStructRole.TableRow, 0),
            "TH" => (AccessibleStructRole.TableHeaderCell, 0),
            "TD" => (AccessibleStructRole.TableCell, 0),
            "Figure" => (AccessibleStructRole.Figure, 0),
            _ => (AccessibleStructRole.Generic, 0),
        };
    }
}

/// <summary>
/// A structurally significant element of a tagged PDF's structure tree, mapped
/// to an accessibility role for the automation peer tree and structure-based
/// keyboard navigation (issue #631).
/// </summary>
/// <param name="Role">The accessibility role.</param>
/// <param name="HeadingLevel">1–6 for <c>/H1</c>–<c>/H6</c>; 0 for a bare
/// <c>/H</c> or a non-heading role.</param>
/// <param name="Text">The element's text carrier (<c>/ActualText</c> then
/// <c>/Alt</c>), or empty when the tagged PDF supplies neither.</param>
/// <param name="Page">The 1-based page the element belongs to.</param>
internal readonly record struct AccessibleStructNode(
    AccessibleStructRole Role,
    int HeadingLevel,
    string Text,
    int Page);

/// <summary>Accessibility roles mapped from PDF structure element types (#631).</summary>
internal enum AccessibleStructRole
{
    Generic,
    Heading,
    List,
    ListItem,
    Table,
    TableRow,
    TableHeaderCell,
    TableCell,
    Figure,
}
