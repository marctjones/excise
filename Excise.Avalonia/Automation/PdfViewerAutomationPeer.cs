using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Excise.Avalonia.Controls;
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
/// that exposes the current page's extractable text in reading order.
/// </para>
/// </summary>
internal sealed class PdfViewerAutomationPeer : ControlAutomationPeer
{
    private readonly PdfViewerControl _viewer;
    private PdfPageTextAutomationPeer? _textPeer;

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

        var result = new List<AutomationPeer> { _textPeer };
        var baseChildren = base.GetChildrenCore();
        if (baseChildren != null)
            result.AddRange(baseChildren);
        return result;
    }

    /// <summary>
    /// Called by the viewer when the current page's text content changed
    /// (page navigation, document swap, or a content rewrite such as
    /// redaction). Raises a Name property change on the synthetic text
    /// child so screen readers pick up the new content.
    /// </summary>
    internal void NotifyPageTextChanged() => _textPeer?.NotifyTextChanged();
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
