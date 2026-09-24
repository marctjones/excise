using System;
using System.Collections.Generic;
using Avalonia;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

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
    /// <summary>
    /// <see cref="Area"/> bound to its page and coordinate space — single-page
    /// viewer DIPs or continuous page-local DIPs, each with its own scale. Null
    /// when the selection spans pages or is empty. <see cref="Area"/> alone
    /// cannot be converted: the same Rect means different points in the two
    /// view modes, and a listener that guessed placed markup off the text (#1796).
    /// </summary>
    public PdfPageRect? PageArea { get; }

    public TextSelectedEventArgs(Rect area, string text, IReadOnlyList<Rect> letterBoundsDips)
        : this(area, text, letterBoundsDips, null) { }

    public TextSelectedEventArgs(Rect area, string text, IReadOnlyList<Rect> letterBoundsDips, PdfPageRect? pageArea)
    {
        Area = area;
        Text = text;
        LetterBoundsDips = letterBoundsDips;
        PageArea = pageArea;
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
/// Event arguments for a click on a link excise refuses to run (#625) —
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
/// <summary>
/// Payload for <c>AnnotationHovered</c> (#1074): a one-line description of the
/// annotation under the pointer, or null when the pointer leaves it.
///
/// <para>A string rather than the annotation itself, on purpose. The viewer
/// control is reusable and the host decides how to present it; handing out a
/// live <c>PdfAnnotation</c> would invite hosts to reach back into the document
/// model from a hover handler.</para>
/// </summary>
public class AnnotationHoveredEventArgs : EventArgs
{
    /// <summary>Description to display, or null when no annotation is hovered.</summary>
    public string? DisplayText { get; }

    public AnnotationHoveredEventArgs(string? displayText) { DisplayText = displayText; }
}

public class LinkHoveredEventArgs : EventArgs
{
    public string? DisplayText { get; }
    public LinkHoveredEventArgs(string? displayText) { DisplayText = displayText; }
}

/// <summary>
/// Payload for <c>StickyNoteClicked</c> (#1788): the user clicked an EXISTING
/// /Text annotation's icon, ambient in any interaction mode — like a link
/// click. Carries the page and the note's own /Rect, not the live
/// <c>PdfAnnotation</c>, for the same reason <see cref="FormFieldRectDrawnEventArgs"/>
/// does not: the host re-reads its own document model rather than being
/// handed a reference into the control's.
/// </summary>
public class StickyNoteClickedEventArgs : EventArgs
{
    public int PageNumber { get; }
    public PdfRectangle Rect { get; }
    public StickyNoteClickedEventArgs(int pageNumber, PdfRectangle rect)
    {
        PageNumber = pageNumber;
        Rect = rect;
    }
}

/// <summary>
/// Payload for <c>StickyNotePlacementRequested</c> (#1788): the sticky-note
/// tool is active (<see cref="InteractionMode.StickyNote"/>) and the user
/// clicked an empty page point. <see cref="PdfX"/>/<see cref="PdfY"/> are
/// content-space PDF points (bottom-left origin) — already converted through
/// <c>PdfCoordinateMapper</c>, the one boundary crossing this control makes.
/// </summary>
public class StickyNotePlacementRequestedEventArgs : EventArgs
{
    public int PageNumber { get; }
    public double PdfX { get; }
    public double PdfY { get; }
    public StickyNotePlacementRequestedEventArgs(int pageNumber, double pdfX, double pdfY)
    {
        PageNumber = pageNumber;
        PdfX = pdfX;
        PdfY = pdfY;
    }
}

/// <summary>
/// Payload for <c>StickyNoteMoved</c> (#1794): a press-and-drag on an
/// existing, NOT-currently-editing note past the click/drag threshold —
/// ambient, like <see cref="StickyNoteClickedEventArgs"/>, and fired instead
/// of <c>StickyNoteClicked</c> for the SAME gesture when the pointer moved
/// past the threshold before release. Both rects are PDF content-space,
/// bottom-left origin — the one coordinate boundary this control crosses.
/// </summary>
public class StickyNoteMovedEventArgs : EventArgs
{
    public int PageNumber { get; }
    /// <summary>The note's own /Rect — its identity, unaffected by the move (#1797).</summary>
    public PdfRectangle IconRect { get; }
    /// <summary>The card's (linked /Popup's) new /Rect.</summary>
    public PdfRectangle NewCardRect { get; }
    public StickyNoteMovedEventArgs(int pageNumber, PdfRectangle iconRect, PdfRectangle newCardRect)
    {
        PageNumber = pageNumber;
        IconRect = iconRect;
        NewCardRect = newCardRect;
    }
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
/// Event arguments for a finished drag-rect in <see cref="InteractionMode.ShapeAnnotation"/>
/// mode. Same shape as <see cref="FormFieldRectDrawnEventArgs"/> — the rect is
/// in PDF points, bottom-left origin — because it is the same gesture; only
/// what the host does with the rect differs.
/// </summary>
public class ShapeAnnotationRectDrawnEventArgs : EventArgs
{
    public PdfRectangle Rect { get; }
    public int PageNumber { get; }
    public ShapeAnnotationRectDrawnEventArgs(PdfRectangle rect, int pageNumber)
    {
        Rect = rect;
        PageNumber = pageNumber;
    }
}

/// <summary>
/// Event arguments for a finished free-form drawing gesture in
/// <see cref="InteractionMode.PathAnnotation"/> mode.
///
/// Carries STROKES — a list of point lists — rather than a single path,
/// because that is the shape `/InkList` needs and it degenerates cleanly to
/// the one-stroke case a Line, Polygon or PolyLine wants (#934 rows E and F).
/// Getting this shape right now is why those rows are wiring rather than a
/// second capture mode.
///
/// Points are in PDF content-stream coordinates, bottom-left origin, already
/// through the same rotation- and zoom-aware mapper the redaction and
/// form-authoring drags use.
/// </summary>
public class AnnotationPathDrawnEventArgs : EventArgs
{
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes { get; }
    public int PageNumber { get; }

    public AnnotationPathDrawnEventArgs(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, int pageNumber)
    {
        Strokes = strokes;
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

    /// <summary>The value the field held before this edit (null if it was empty); what Undo restores.</summary>
    public string? OldValue { get; }

    public FormFieldEditedEventArgs(string fieldName, string? newValue, int pageNumber)
        : this(fieldName, newValue, pageNumber, oldValue: null)
    {
    }

    public FormFieldEditedEventArgs(string fieldName, string? newValue, int pageNumber, string? oldValue)
    {
        FieldName = fieldName;
        NewValue = newValue;
        PageNumber = pageNumber;
        OldValue = oldValue;
    }
}

/// <summary>
/// Event arguments for an AcroForm field edit the field REFUSED (#1671): the
/// value was NOT stored. <see cref="Message"/> names the characters and says
/// why, so the host can show it — the edit must never fail silently.
/// </summary>
public class FormFieldEditRejectedEventArgs : EventArgs
{
    public string FieldName { get; }
    public string Message { get; }
    public FormFieldEditRejectedEventArgs(string fieldName, string message)
    {
        FieldName = fieldName;
        Message = message;
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
/// How <see cref="InteractionMode.PathAnnotation"/> collects its points.
///
/// The capture, the coordinate conversion and the event are shared; only the
/// TERMINATION RULE differs between an ink stroke, a line and a polygon. This
/// enum is that rule, which is why those are three annotations and one mode.
/// </summary>
public enum PathCaptureKind
{
    /// <summary>Pointer-down, sample while moving, pointer-up ends the stroke. Ink.</summary>
    Freehand,

    /// <summary>Pointer-down and pointer-up are the only two points. Line and Arrow.</summary>
    Segment,

    /// <summary>
    /// Each click plants a vertex; the gesture ends on an EXPLICIT finish
    /// (double-click or Enter), not on pointer-up. Polygon and PolyLine.
    ///
    /// This is the one capture kind that spans multiple clicks, which is why
    /// its vertex list is separate from the drag state and is NOT cleared on
    /// pointer-press — the second click must not erase the first.
    /// </summary>
    Vertices,
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

    /// <summary>
    /// Draw a free-form path that becomes an annotation. The host listens for
    /// <see cref="PdfViewerControl.AnnotationPathDrawn"/>.
    ///
    /// Named for the PATH, not for Ink specifically: the same capture and the
    /// same event serve the segment gesture Line/Arrow needs and the vertex
    /// gesture Polygon/PolyLine needs (#934 E, F). Only the termination rule
    /// differs, so those rows add a gesture kind here rather than a mode.
    /// </summary>
    PathAnnotation,

    /// <summary>
    /// Click a page point to place a sticky note there (#1788). Unlike the
    /// other editing modes this is a CLICK, not a drag — the host listens for
    /// <see cref="PdfViewerControl.StickyNotePlacementRequested"/>. An
    /// existing note's icon is clickable in every mode via
    /// <see cref="PdfViewerControl.StickyNoteClicked"/>, ambient like a link.
    /// </summary>
    StickyNote,

    /// <summary>
    /// Drag a rectangle that becomes a Square, Circle, FreeText box, Stamp or
    /// Image Stamp annotation directly — which one is the host's own current
    /// selection, not anything this control tracks. The host listens for
    /// <see cref="PdfViewerControl.ShapeAnnotationRectDrawn"/>.
    ///
    /// Exists because these five annotation types used to have NO drawing
    /// mode of their own: their Add*FromDrag commands read a rect staged by
    /// <see cref="InteractionMode.Redaction"/>'s OWN drag gesture, which only
    /// a genuinely-enabled Redaction Mode can produce — and doing so ALSO
    /// marks that area as a pending redaction as a side effect, then clears
    /// the rect it just staged. There was no gesture that left a shape
    /// annotation both reachable and safe. This mode gives them the same
    /// direct one-drag-places-it path <see cref="FormAuthoring"/> and
    /// <see cref="PathAnnotation"/> already have.
    /// </summary>
    ShapeAnnotation,
}
