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
/// <c>/Alt</c> description on the current page, so figures and images with
/// alternative text are announced too.
/// </para>
/// </summary>
internal sealed class PdfViewerAutomationPeer : ControlAutomationPeer
{
    private readonly PdfViewerControl _viewer;
    private PdfPageTextAutomationPeer? _textPeer;
    private List<PdfAltTextAutomationPeer> _altPeers = new();

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
        SyncAltTextPeers();

        var result = new List<AutomationPeer> { _textPeer };
        result.AddRange(_altPeers);
        var baseChildren = base.GetChildrenCore();
        if (baseChildren != null)
            result.AddRange(baseChildren);
        return result;
    }

    /// <summary>
    /// Rebuild the synthetic <c>/Alt</c>-description children if the current
    /// page's set of descriptions changed. Peer instances are kept stable
    /// while the descriptions are unchanged so assistive technology retains
    /// element identity across unrelated refreshes. Returns true when the
    /// children actually changed.
    /// </summary>
    private bool SyncAltTextPeers()
    {
        var alts = _viewer.GetAccessibleAltTexts();

        if (alts.Count == _altPeers.Count)
        {
            bool same = true;
            for (int i = 0; i < alts.Count; i++)
            {
                if (!string.Equals(alts[i], _altPeers[i].Description, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return false;
        }

        var peers = new List<PdfAltTextAutomationPeer>(alts.Count);
        for (int i = 0; i < alts.Count; i++)
            peers.Add(new PdfAltTextAutomationPeer(alts[i], i));
        _altPeers = peers;
        return true;
    }

    /// <summary>
    /// Called by the viewer when the current page's text content changed
    /// (page navigation, document swap, or a content rewrite such as
    /// redaction). Raises a Name property change on the synthetic text
    /// child so screen readers pick up the new content, and a
    /// children invalidation when the page's <c>/Alt</c> descriptions
    /// changed with it. Invalidation matters: <see cref="ControlAutomationPeer"/>
    /// caches the children list, so without <see cref="ControlAutomationPeer.InvalidateChildren"/>
    /// (which also raises the children-changed event) a page change would keep
    /// serving the previous page's description peers forever.
    /// </summary>
    internal void NotifyPageTextChanged()
    {
        _textPeer?.NotifyTextChanged();
        if (SyncAltTextPeers())
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

    protected override string? GetNameCore() => _viewer.GetAccessiblePageText();

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
/// Synthetic (control-less) peer that carries one tagged-PDF <c>/Alt</c>
/// alternative description from the current page's structure tree
/// (issue #631). Figures and images contribute nothing to the extractable
/// text layer, so without these peers a described image is silent to
/// screen readers even in a properly tagged document.
///
/// <para>
/// The description is fixed at construction; page changes replace the peer
/// set (see <see cref="PdfViewerAutomationPeer.NotifyPageTextChanged"/>),
/// which raises a children-changed event so assistive technology re-reads.
/// </para>
/// </summary>
internal sealed class PdfAltTextAutomationPeer : UnrealizedElementAutomationPeer
{
    private readonly int _index;
    private AutomationPeer? _parent;

    public PdfAltTextAutomationPeer(string description, int index)
    {
        Description = description;
        _index = index;
    }

    /// <summary>The <c>/Alt</c> alternative description this peer announces.</summary>
    public string Description { get; }

    protected override string? GetNameCore() => Description;

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Image;

    protected override string GetClassNameCore() => "PdfFigureAltText";

    protected override string GetAutomationIdCore() => $"PdfFigureAltText{_index}";

    protected override string GetLocalizedControlTypeCore() => "figure description";

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

    // Unrealized peers default to invisible-to-AT; this node exists solely
    // for assistive technology.
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
}
