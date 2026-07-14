using System;
using System.Collections.Generic;
using Avalonia;
using Pdfe.Core.Document;

namespace Pdfe.Avalonia.Controls;

/// <summary>
/// Event arguments for redaction drawn event.
/// </summary>
public class RedactionDrawnEventArgs : EventArgs
{
    public Rect Area { get; }
    public int RenderDpi { get; }
    public PdfPageRect PageArea { get; }

    public RedactionDrawnEventArgs(PdfPageRect pageArea)
    {
        PageArea = pageArea;
        Area = new Rect(pageArea.X, pageArea.Y, pageArea.Width, pageArea.Height);
        RenderDpi = (int)Math.Round(pageArea.Dpi);
    }

    public RedactionDrawnEventArgs(Rect area, int renderDpi)
        : this(PdfPageRect.ViewerDips(1, area.X, area.Y, area.Width, area.Height, renderDpi)) { }

    public RedactionDrawnEventArgs(Rect area) : this(area, 150) { }
}

/// <summary>
/// Event arguments for text selected event.
/// </summary>
public class TextSelectedEventArgs : EventArgs
{
    /// <summary>Joined text of the selected letter run, in reading order.</summary>
    public string Text { get; }
    /// <summary>Per-letter bounding boxes in viewer-DIP coordinates.</summary>
    public IReadOnlyList<Rect> LetterBoundsDips { get; }
    /// <summary>Bounding box of the entire selection. Backwards-compat with the rect-only listeners.</summary>
    public Rect Area { get; }

    public TextSelectedEventArgs(Rect area, string text, IReadOnlyList<Rect> letterBoundsDips)
    {
        Area = area;
        Text = text;
        LetterBoundsDips = letterBoundsDips;
    }

    /// <summary>Backwards-compat ctor — area only, empty text/bounds.</summary>
    public TextSelectedEventArgs(Rect area) : this(area, string.Empty, Array.Empty<Rect>()) { }
}

/// <summary>
/// Event arguments for an internal-document link click. Carries the
/// 1-based page number of the destination.
/// </summary>
public class LinkClickedEventArgs : EventArgs
{
    public int PageNumber { get; }
    public LinkClickedEventArgs(int pageNumber) { PageNumber = pageNumber; }
}

/// <summary>Event arguments for an external (http/https/mailto) link click (#625).</summary>
public class ExternalLinkClickedEventArgs : EventArgs
{
    public string Uri { get; }
    public ExternalLinkClickedEventArgs(string uri) { Uri = uri; }
}

/// <summary>
/// Event arguments for a click on a link pdfe refuses to run (#625) —
/// /Launch, /GoToE, /GoToR, or a URI action with a disallowed scheme.
/// </summary>
public class DangerousLinkClickedEventArgs : EventArgs
{
    /// <summary>What was refused, e.g. "Launch", "GoToE", "URI:file".</summary>
    public string ActionType { get; }
    public DangerousLinkClickedEventArgs(string actionType) { ActionType = actionType; }
}

/// <summary>
/// Event arguments for pointer hover over a link (#625). <see cref="DisplayText"/>
/// is null when the pointer has moved off the link.
/// </summary>
public class LinkHoveredEventArgs : EventArgs
{
    public string? DisplayText { get; }
    public LinkHoveredEventArgs(string? displayText) { DisplayText = displayText; }
}

/// <summary>
/// Event arguments for the user finishing a drag-rect in FormAuthoring
/// mode. The rect is in PDF points, bottom-left origin.
/// </summary>
public class FormFieldRectDrawnEventArgs : EventArgs
{
    public PdfRectangle Rect { get; }
    public int PageNumber { get; }
    public FormFieldRectDrawnEventArgs(PdfRectangle rect, int pageNumber)
    {
        Rect = rect;
        PageNumber = pageNumber;
    }
}

/// <summary>
/// Event arguments for an AcroForm field edit. The control has already
/// mutated <see cref="PdfField.SetValue"/>; carries the field's full name and
/// the new value (null if cleared).
/// </summary>
public class FormFieldEditedEventArgs : EventArgs
{
    public string FieldName { get; }
    public string? NewValue { get; }
    public int PageNumber { get; }
    public FormFieldEditedEventArgs(string fieldName, string? newValue, int pageNumber)
    {
        FieldName = fieldName;
        NewValue = newValue;
        PageNumber = pageNumber;
    }
}

public class TypewriterTextCreatedEventArgs : EventArgs
{
    public PdfRectangle Rect { get; }
    public int PageNumber { get; }

    public TypewriterTextCreatedEventArgs(PdfRectangle rect, int pageNumber)
    {
        Rect = rect;
        PageNumber = pageNumber;
    }
}

public class TypewriterTextEditedEventArgs : EventArgs
{
    public Guid OperationId { get; }
    public string Text { get; }
    public int PageNumber { get; }

    public TypewriterTextEditedEventArgs(Guid operationId, string text, int pageNumber)
    {
        OperationId = operationId;
        Text = text;
        PageNumber = pageNumber;
    }
}

public class TypewriterTextBoundsChangedEventArgs : EventArgs
{
    public Guid OperationId { get; }
    public PdfRectangle Rect { get; }
    public int PageNumber { get; }

    public TypewriterTextBoundsChangedEventArgs(Guid operationId, PdfRectangle rect, int pageNumber)
    {
        OperationId = operationId;
        Rect = rect;
        PageNumber = pageNumber;
    }
}

public class TypewriterTextDeletedEventArgs : EventArgs
{
    public Guid OperationId { get; }
    public int PageNumber { get; }

    public TypewriterTextDeletedEventArgs(Guid operationId, int pageNumber)
    {
        OperationId = operationId;
        PageNumber = pageNumber;
    }
}

/// <summary>
/// Event arguments for page changed event.
/// </summary>
public class PageChangedEventArgs : EventArgs
{
    public int PageNumber { get; }

    public PageChangedEventArgs(int pageNumber)
    {
        PageNumber = pageNumber;
    }
}

/// <summary>
/// How the viewer lays out pages. <see cref="SinglePage"/> shows one page with
/// full editing; <see cref="Continuous"/> is a render-virtualized scrolling
/// reading view of all pages with no editing.
/// </summary>
public enum PdfViewMode
{
    /// <summary>One page at a time, with all editing interactions (the default).</summary>
    SinglePage,

    /// <summary>Scrollable all-pages reading view, render-virtualized, no editing.</summary>
    Continuous,
}

/// <summary>
/// Interaction modes for the PDF viewer.
/// </summary>
public enum InteractionMode
{
    /// <summary>
    /// No interaction (view only).
    /// </summary>
    None,

    /// <summary>
    /// Draw redaction rectangles.
    /// </summary>
    Redaction,

    /// <summary>
    /// Select text areas.
    /// </summary>
    TextSelection,

    /// <summary>
    /// Pan/scroll the document.
    /// </summary>
    Pan,

    /// <summary>
    /// Drag to define a new AcroForm field rect. The host listens for
    /// <see cref="PdfViewerControl.FormFieldRectDrawn"/> and materialises
    /// a field of the user-selected type.
    /// </summary>
    FormAuthoring,

    /// <summary>
    /// Click or drag to place editable text that can be flattened into page
    /// content.
    /// </summary>
    Typewriter,
}
