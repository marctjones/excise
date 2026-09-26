using Excise.Core.Document;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Accessibility support for the document content (issue #631): the tagged-PDF
/// structure tree's textual carriers for the current page — <c>/Alt</c>
/// alternative descriptions (figures/images contribute nothing to the
/// extractable text layer) and <c>/ActualText</c> replacement text
/// (ISO 32000-2 §14.9.4: the author-supplied text that assistive technology
/// should read <em>instead of</em> the raw glyphs, used where glyph
/// extraction is wrong — hyphenation rejoins, ligature or symbol
/// substitutions, stylized text) — and the role layer (headings, lists,
/// tables) with structure-based keyboard navigation.
///
/// <para>
/// Read-only over the one structure model, <see cref="PdfDocument.GetStructureTree"/>.
/// Replacement text is exposed as additional peers with a containment dedup
/// (see <see cref="GetAccessibleActualTexts"/>) so content is not announced twice.
/// </para>
/// </summary>
public partial class PdfViewerControl
{
    // The structure tree flattened once, in document order, each element tagged
    // with the page it is announced on. Doc-wide (not per-page) because keyboard
    // structure navigation crosses page boundaries. Keyed by (Document,
    // RenderVersion): a RenderVersion bump means the tree may have been rewritten
    // (redaction scrubs /Alt and /ActualText, #636), so the model is rebuilt and
    // the accessibility tree must not keep announcing redacted text.
    private PdfDocument? _structDocument;
    private long _structRenderVersion = -1;
    private IReadOnlyList<(int Page, PdfStructElement Element)> _structElements =
        Array.Empty<(int, PdfStructElement)>();
    // Structurally significant elements (headings, lists, tables) in document
    // order, for the role automation peers and keyboard navigation; built from
    // _structElements on first use.
    private IReadOnlyList<AccessibleStructNode>? _structNodes;
    // Index into the role nodes the last structure-navigation keystroke landed
    // on, or -1 before any navigation.
    private int _structNavCursor = -1;

    // The current page's /Alt and /ActualText carriers, keyed by the model they
    // were read from and the page.
    private IReadOnlyList<(int Page, PdfStructElement Element)>? _structTextElements;
    private int _structTextPage = -1;
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
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> GetAccessibleActualTexts()
    {
        EnsureStructTextCaches();
        return _actualTextCache;
    }

    private void EnsureStructTextCaches()
    {
        EnsureStructModel();
        var doc = Document;
        int page = CurrentPage;

        if (ReferenceEquals(_structTextElements, _structElements) && _structTextPage == page)
            return;

        IReadOnlyList<string> alts = Array.Empty<string>();
        IReadOnlyList<string> actuals = Array.Empty<string>();
        if (doc != null && page >= 1 && page <= doc.PageCount)
        {
            try
            {
                alts = TextCarriersOn(page, e => e.AltText);
                actuals = FilterActualTextsAlreadyInPageText(TextCarriersOn(page, e => e.ActualText));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Malformed structure trees must never take down the viewer;
                // accessibility degrades to the text layer alone.
                alts = Array.Empty<string>();
                actuals = Array.Empty<string>();
            }
        }

        _structTextElements = _structElements;
        _structTextPage = page;
        _altTextCache = alts;
        _actualTextCache = actuals;
    }

    /// <summary>
    /// The trimmed, non-blank text one carrier (<c>/Alt</c> or <c>/ActualText</c>)
    /// holds across the elements announced on <paramref name="page"/>, in
    /// document order.
    /// </summary>
    private List<string> TextCarriersOn(int page, Func<PdfStructElement, string?> carrier) =>
        _structElements
            .Where(e => e.Page == page)
            .Select(e => carrier(e.Element))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .ToList();

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

    /// <summary>
    /// The current page's text in reading order, ordered by the structure
    /// tree when the tagged PDF supplies orderable text (issue #631). Uses the
    /// document-order sequence of <c>/ActualText</c> carriers where present;
    /// otherwise falls back to the geometric reading-order text (see
    /// <see cref="GetAccessiblePageText"/>). Struct order is used only when it
    /// actually carries text: a tagged PDF that supplies no <c>/ActualText</c>
    /// reads geometrically.
    /// </summary>
    internal string GetAccessibleReadingOrderText()
    {
        EnsureStructModel();

        var parts = TextCarriersOn(CurrentPage, e => e.ActualText);
        return parts.Count > 0
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
        int page = CurrentPage;
        return StructNodes()
            .Where(node => node.Page == page && node.Role != AccessibleStructRole.Figure)
            .ToList();
    }

    /// <summary>
    /// The structure element the last structure-navigation keystroke landed
    /// on (see <see cref="MoveToNextStructure"/>), or null before any
    /// navigation. Exposed for assistive-technology announcement and tests.
    /// </summary>
    internal AccessibleStructNode? CurrentStructureNavigationTarget =>
        _structNodes is { } nodes && _structNavCursor >= 0 && _structNavCursor < nodes.Count
            ? nodes[_structNavCursor]
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
        var nodes = StructNodes();
        if (nodes.Count == 0)
            return false;

        int step = backward ? -1 : 1;
        for (int i = _structNavCursor + step; i >= 0 && i < nodes.Count; i += step)
        {
            var node = nodes[i];
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

    private void EnsureStructModel()
    {
        var doc = Document;
        long version = RenderVersion;

        if (ReferenceEquals(_structDocument, doc) && _structRenderVersion == version)
            return;

        var elements = new List<(int Page, PdfStructElement Element)>();
        if (doc != null && doc.PageCount > 0)
        {
            try
            {
                if (doc.GetStructureTree() is { } root)
                    Flatten(root, doc.PageCount == 1 ? 1 : 0, elements);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed structure tree must never take the viewer down;
                // structure navigation degrades to nothing.
                elements.Clear();
            }
        }

        _structDocument = doc;
        _structRenderVersion = version;
        _structElements = elements;
        _structNodes = null;
        _structNavCursor = -1; // a new model invalidates the navigation cursor
    }

    /// <summary>
    /// Append <paramref name="element"/> and its descendants in document order,
    /// each with the page it is announced on. An element with no determinable
    /// page can still be announced when the document has one page it could
    /// belong to (<paramref name="onlyPage"/>); one with neither is left out.
    /// </summary>
    private static void Flatten(
        PdfStructElement element,
        int onlyPage,
        List<(int Page, PdfStructElement Element)> into)
    {
        int page = element.PageNumber ?? onlyPage;
        if (page >= 1)
            into.Add((page, element));

        foreach (var child in element.Children)
            Flatten(child, onlyPage, into);
    }

    private IReadOnlyList<AccessibleStructNode> StructNodes()
    {
        EnsureStructModel();
        return _structNodes ??= BuildStructNodes(_structDocument!, _structElements);
    }

    private static List<AccessibleStructNode> BuildStructNodes(
        PdfDocument doc,
        IReadOnlyList<(int Page, PdfStructElement Element)> elements)
    {
        var nodes = new List<AccessibleStructNode>();
        foreach (var (page, element) in elements)
        {
            var (role, headingLevel) = ClassifyStructRole(element.RoleMappedType);
            if (role == AccessibleStructRole.Generic)
                continue;

            // /ActualText (author's replacement) wins, then /Alt (image
            // description). With neither, resolve the element's REAL body
            // glyphs from its /MCID marked-content references so a screen
            // reader reads the actual heading/cell text instead of a
            // role-only peer — the MCID→letter bridge (#776).
            string text = !string.IsNullOrWhiteSpace(element.ActualText)
                ? element.ActualText!.Trim()
                : (!string.IsNullOrWhiteSpace(element.AltText)
                    ? element.AltText!.Trim()
                    : ResolveMcidText(doc, element));
            nodes.Add(new AccessibleStructNode(role, headingLevel, text, page));
        }
        return nodes;
    }

    /// <summary>
    /// Resolve a structure element's real body text from its /MCID marked-content
    /// references (#776), returning it whitespace-trimmed, or empty on failure.
    /// A malformed structure tree or extraction hiccup must never take down the
    /// accessibility walk, so this is defensive: any failure degrades the node to
    /// role-only, exactly as before the bridge existed.
    /// </summary>
    private static string ResolveMcidText(PdfDocument doc, PdfStructElement element)
    {
        try
        {
            return doc.ResolveStructElementText(element).Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Map an element's <see cref="PdfStructElement.RoleMappedType"/> (the
    /// ISO 32000-2 §14.8.4 standard structure type its <c>/RoleMap</c> chain
    /// reaches, with the leading <c>/</c> that <c>PdfStructTreeParser</c>
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
