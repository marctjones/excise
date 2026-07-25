using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Excise.Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace Excise.Avalonia.Automation;

/// <summary>
/// Automation peer for <see cref="PdfViewerControl"/> (issue #631).
///
/// <para>
/// The rendered PDF page is an opaque bitmap to assistive technology: the
/// viewer's visual children are an <c>Image</c> and overlay <c>Canvas</c>es,
/// none of which carry the document's text. This peer inserts a synthetic
/// <see cref="PdfPageTextAutomationPeer"/> child — first in the children list,
/// so it is the first thing a screen reader reaches when entering the viewer —
/// that exposes the current page's extractable text in reading order, followed
/// by one <see cref="PdfAltTextAutomationPeer"/> child per tagged-PDF
/// <c>/Alt</c> description on the current page (figures and images with
/// alternative text), and one <see cref="PdfActualTextAutomationPeer"/> child
/// per <c>/ActualText</c> replacement text the extractable text layer does
/// not already carry (spans where glyph extraction reads wrong).
/// </para>
/// </summary>
internal sealed class PdfViewerAutomationPeer : ControlAutomationPeer
{
    private readonly PdfViewerControl _viewer;
    private PdfPageTextAutomationPeer? _textPeer;
    private List<PdfAltTextAutomationPeer> _altPeers = new();
    private List<PdfActualTextAutomationPeer> _actualTextPeers = new();
    private List<PdfStructRoleAutomationPeer> _rolePeers = new();

    public PdfViewerAutomationPeer(PdfViewerControl viewer)
        : base(viewer)
    {
        _viewer = viewer;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Document;

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore()
    {
        _textPeer ??= new PdfPageTextAutomationPeer(_viewer);
        SyncDescriptionPeers();

        var result = new List<AutomationPeer> { _textPeer };
        result.AddRange(_altPeers);
        result.AddRange(_actualTextPeers);
        result.AddRange(_rolePeers);
        var baseChildren = base.GetChildrenCore();
        if (baseChildren != null)
            result.AddRange(baseChildren);
        return result;
    }

    /// <summary>
    /// Rebuild the synthetic structure-tree children (<c>/Alt</c>
    /// descriptions and <c>/ActualText</c> replacements) if the current
    /// page's sets changed. Peer instances are kept stable while the texts
    /// are unchanged so assistive technology retains element identity across
    /// unrelated refreshes. Returns true when the children actually changed.
    /// </summary>
    private bool SyncDescriptionPeers()
    {
        // Non-short-circuit: all sets must be brought current.
        return SyncPeerList(_viewer.GetAccessibleAltTexts(), ref _altPeers,
                   static (text, i) => new PdfAltTextAutomationPeer(text, i))
             | SyncPeerList(_viewer.GetAccessibleActualTexts(), ref _actualTextPeers,
                   static (text, i) => new PdfActualTextAutomationPeer(text, i))
             | SyncRolePeers();
    }

    /// <summary>
    /// Rebuild the structure-role children (headings, lists, tables) if the
    /// current page's role set changed. Keyed on role + heading level + text
    /// so a page change that swaps one heading for another of the same text
    /// but a different level is still detected. Returns true when they changed.
    /// </summary>
    private bool SyncRolePeers()
    {
        var nodes = _viewer.GetAccessibleStructRoleNodes();
        if (nodes.Count == _rolePeers.Count)
        {
            bool same = true;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!_rolePeers[i].Matches(nodes[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return false;
        }

        var replacement = new List<PdfStructRoleAutomationPeer>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            replacement.Add(new PdfStructRoleAutomationPeer(nodes[i], i));
        _rolePeers = replacement;
        return true;
    }

    private static bool SyncPeerList<TPeer>(
        IReadOnlyList<string> texts,
        ref List<TPeer> peers,
        Func<string, int, TPeer> create)
        where TPeer : PdfStructTextAutomationPeer
    {
        if (texts.Count == peers.Count)
        {
            bool same = true;
            for (int i = 0; i < texts.Count; i++)
            {
                if (!string.Equals(texts[i], peers[i].Description, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return false;
        }

        var replacement = new List<TPeer>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
            replacement.Add(create(texts[i], i));
        peers = replacement;
        return true;
    }

    /// <summary>
    /// Called by the viewer when the current page's text content changed
    /// (page navigation, document swap, or a content rewrite such as
    /// redaction). Raises a Name property change on the synthetic text
    /// child so screen readers pick up the new content, and a
    /// children invalidation when the page's <c>/Alt</c> or <c>/ActualText</c>
    /// sets changed with it. Invalidation matters: <see cref="ControlAutomationPeer"/>
    /// caches the children list, so without <see cref="ControlAutomationPeer.InvalidateChildren"/>
    /// (which also raises the children-changed event) a page change would keep
    /// serving the previous page's description peers forever.
    /// </summary>
    internal void NotifyPageTextChanged()
    {
        _textPeer?.NotifyTextChanged();
        if (SyncDescriptionPeers())
            InvalidateChildren();
    }
}

/// <summary>
/// Synthetic (control-less) peer that carries the current page's extractable
/// text so screen readers can read the document content (issue #631).
///
/// <para>
/// The text is produced by the same pipeline text-selection copy uses —
/// <c>Excise.Core</c> letter extraction sorted into geometric reading order
/// (top-to-bottom lines, left-to-right within a line) and joined with
/// word/line breaks. Struct-tree (tagged PDF) reading order is a follow-up
/// slice of #631: the parsed structure tree does not yet map elements to
/// pages or MCIDs to letters, so geometric order is used for all documents.
/// </para>
///
/// <para>
/// Avalonia has no TextPattern provider, so the content is exposed through
/// the peer's Name — the property every platform backend (UIA, AX,
/// AT-SPI) surfaces to screen readers.
/// </para>
/// </summary>
internal sealed class PdfPageTextAutomationPeer : UnrealizedElementAutomationPeer
{
    private readonly PdfViewerControl _viewer;
    private AutomationPeer? _parent;

    public PdfPageTextAutomationPeer(PdfViewerControl viewer)
    {
        _viewer = viewer;
    }

    protected override string? GetNameCore() => _viewer.GetAccessibleReadingOrderText();

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Text;

    protected override string GetClassNameCore() => "PdfPageText";

    protected override string GetAutomationIdCore() => "PdfPageTextContent";

    protected override string GetLocalizedControlTypeCore() => "page text";

    protected override string? GetAcceleratorKeyCore() => null;

    protected override string? GetAccessKeyCore() => null;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationPeer? GetParentCore() => _parent;

    // The base UnrealizedElementAutomationPeer refuses parents (returns
    // false), which orphans the peer. ControlAutomationPeer's child
    // wiring calls TrySetParent on every child it returns, so accepting
    // here is what links this synthetic node into the tree.
    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }

    // Unrealized peers default to invisible-to-AT (content/control element
    // both false). This node exists solely for assistive technology.
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    internal void NotifyTextChanged() =>
        RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, null, GetName());
}

/// <summary>
/// Common base for the synthetic (control-less) peers that each carry one
/// piece of structure-tree text from the current page (issue #631). The text
/// is fixed at construction; page changes replace the peer set (see
/// <see cref="PdfViewerAutomationPeer.NotifyPageTextChanged"/>), which raises
/// a children-changed event so assistive technology re-reads.
/// </summary>
internal abstract class PdfStructTextAutomationPeer : UnrealizedElementAutomationPeer
{
    private AutomationPeer? _parent;

    protected PdfStructTextAutomationPeer(string description)
    {
        Description = description;
    }

    /// <summary>The structure-tree text this peer announces.</summary>
    public string Description { get; }

    protected override string? GetNameCore() => Description;

    protected override string? GetAcceleratorKeyCore() => null;

    protected override string? GetAccessKeyCore() => null;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationPeer? GetParentCore() => _parent;

    // Same synthetic-node parenting contract as PdfPageTextAutomationPeer:
    // accepting the parent set by ControlAutomationPeer's child wiring is
    // what links this node into the tree.
    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }

    // Unrealized peers default to invisible-to-AT; these nodes exist solely
    // for assistive technology.
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
}

/// <summary>
/// Synthetic peer that carries one tagged-PDF <c>/Alt</c> alternative
/// description from the current page's structure tree (issue #631). Figures
/// and images contribute nothing to the extractable text layer, so without
/// these peers a described image is silent to screen readers even in a
/// properly tagged document.
/// </summary>
internal sealed class PdfAltTextAutomationPeer : PdfStructTextAutomationPeer
{
    private readonly int _index;

    public PdfAltTextAutomationPeer(string description, int index)
        : base(description)
    {
        _index = index;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Image;

    protected override string GetClassNameCore() => "PdfFigureAltText";

    protected override string GetAutomationIdCore() => $"PdfFigureAltText{_index}";

    protected override string GetLocalizedControlTypeCore() => "figure description";
}

/// <summary>
/// Synthetic peer that carries one tagged-PDF <c>/ActualText</c> replacement
/// text from the current page's structure tree (issue #631). <c>/ActualText</c>
/// (ISO 32000-2 §14.9.4) is the author's statement of what a content span
/// really says when its glyphs extract wrong — hyphenation rejoins, ligature
/// or symbol substitutions, stylized text. Only replacements the extractable
/// text layer does not already carry are exposed (see
/// <see cref="PdfViewerControl.GetAccessibleActualTexts"/>), so a screen
/// reader never hears the same content twice; in-place substitution inside
/// the page-text stream awaits MCID-to-letter mapping from Excise.Core.
/// </summary>
internal sealed class PdfActualTextAutomationPeer : PdfStructTextAutomationPeer
{
    private readonly int _index;

    public PdfActualTextAutomationPeer(string description, int index)
        : base(description)
    {
        _index = index;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Text;

    protected override string GetClassNameCore() => "PdfActualText";

    protected override string GetAutomationIdCore() => $"PdfActualText{_index}";

    protected override string GetLocalizedControlTypeCore() => "replacement text";
}

/// <summary>
/// Synthetic peer for one structurally significant tagged-PDF element on the
/// current page — a heading, list, list item, table, row, or cell (issue
/// #631). It maps the PDF structure role to the nearest Avalonia
/// <see cref="AutomationControlType"/> and a spoken localized control type, so
/// a screen reader can enumerate the document's structure and jump between
/// headings. The element's text (<c>/ActualText</c> then <c>/Alt</c>) is the
/// Name; it is empty for a tagged PDF that supplies neither, in which case the
/// role alone is announced (the body glyphs need MCID-to-letter mapping to
/// reach, a follow-up slice of #631).
/// </summary>
internal sealed class PdfStructRoleAutomationPeer : UnrealizedElementAutomationPeer
{
    private readonly AccessibleStructNode _node;
    private readonly int _index;
    private AutomationPeer? _parent;

    public PdfStructRoleAutomationPeer(AccessibleStructNode node, int index)
    {
        _node = node;
        _index = index;
    }

    /// <summary>True when this peer already represents the given node.</summary>
    public bool Matches(AccessibleStructNode node) =>
        _node.Role == node.Role
        && _node.HeadingLevel == node.HeadingLevel
        && string.Equals(_node.Text, node.Text, StringComparison.Ordinal);

    protected override string? GetNameCore() => _node.Text;

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        _node.Role switch
        {
            AccessibleStructRole.Heading => AutomationControlType.Text,
            AccessibleStructRole.List => AutomationControlType.List,
            AccessibleStructRole.ListItem => AutomationControlType.ListItem,
            AccessibleStructRole.Table => AutomationControlType.Table,
            AccessibleStructRole.TableRow => AutomationControlType.DataItem,
            AccessibleStructRole.TableHeaderCell => AutomationControlType.Header,
            AccessibleStructRole.TableCell => AutomationControlType.Text,
            _ => AutomationControlType.Text,
        };

    protected override string GetLocalizedControlTypeCore() =>
        _node.Role switch
        {
            AccessibleStructRole.Heading =>
                _node.HeadingLevel >= 1 ? $"heading level {_node.HeadingLevel}" : "heading",
            AccessibleStructRole.List => "list",
            AccessibleStructRole.ListItem => "list item",
            AccessibleStructRole.Table => "table",
            AccessibleStructRole.TableRow => "table row",
            AccessibleStructRole.TableHeaderCell => "table header cell",
            AccessibleStructRole.TableCell => "table cell",
            _ => "content",
        };

    /// <summary>The structure role this peer exposes. Used by tests.</summary>
    public AccessibleStructRole Role => _node.Role;

    /// <summary>The heading level (1–6), or 0 for non-headings. Used by tests.</summary>
    public int HeadingLevel => _node.HeadingLevel;

    protected override string GetClassNameCore() => "PdfStructRole";

    protected override string GetAutomationIdCore() => $"PdfStructRole{_index}";

    protected override string? GetAcceleratorKeyCore() => null;

    protected override string? GetAccessKeyCore() => null;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationPeer? GetParentCore() => _parent;

    // Same synthetic-node parenting contract as the other peers: accepting the
    // parent set by ControlAutomationPeer's child wiring links this node in.
    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }

    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
}
