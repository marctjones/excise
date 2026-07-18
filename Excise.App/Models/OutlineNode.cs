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
    /// <summary>Display string for the row, with page-number suffix when known.</summary>
    public string DisplayText => PageNumber.HasValue ? $"{Title}  p.{PageNumber}" : Title;

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
