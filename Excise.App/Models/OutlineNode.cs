using System.Collections.ObjectModel;

namespace Excise.App.Models;

/// <summary>
/// View-model wrapper around a <see cref="Excise.Core.Document.PdfOutlineItem"/>
/// for binding into a TreeView. Carries Title, optional 1-based page
/// number, and a child collection (recursive). The Avalonia TreeView
/// expects an ObservableCollection per level so we materialise children
/// eagerly — outlines are typically small (hundreds of nodes max).
/// </summary>
public sealed class OutlineNode
{
    public string Title { get; }
    public int? PageNumber { get; }
    public ObservableCollection<OutlineNode> Children { get; }
    /// <summary>
    /// Display string for the row, with page-number suffix when known.
    /// </summary>
    /// <remarks>
    /// #1205: a bookmark title is document-authored text shown as a NAVIGATION
    /// LABEL — the user picks a destination by reading it. An embedded bidi
    /// override lets a title display as something other than what it is, so
    /// invisible controls are made explicit HERE, in the label, while
    /// <see cref="Title"/> keeps the document's exact characters for anything
    /// that needs to match or copy them.
    /// </remarks>
    public string DisplayText
    {
        get
        {
            var shown = Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(Title);
            return PageNumber.HasValue ? $"{shown}  p.{PageNumber}" : shown;
        }
    }

    public OutlineNode(string title, int? pageNumber, ObservableCollection<OutlineNode> children)
    {
        Title = title;
        PageNumber = pageNumber;
        Children = children;
    }

    public static OutlineNode From(Excise.Core.Document.PdfOutlineItem item)
    {
        var children = new ObservableCollection<OutlineNode>();
        foreach (var c in item.Children) children.Add(From(c));
        return new OutlineNode(item.Title, item.PageNumber, children);
    }
}
