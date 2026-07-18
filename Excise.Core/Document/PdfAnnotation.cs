using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Annotation subtypes defined in ISO 32000-2:2020 §12.5.6.
/// </summary>
public enum PdfAnnotationSubtype
{
    Unknown,
    Text,           // Sticky-note comment (§12.5.6.4)
    Link,           // Internal or URI link (§12.5.6.5)
    FreeText,       // Text directly on the page (§12.5.6.6)
    Line,           // Straight line (§12.5.6.7)
    Square,         // Rectangle shape (§12.5.6.8)
    Circle,         // Ellipse shape (§12.5.6.8)
    Polygon,        // Closed polygon (§12.5.6.9)
    PolyLine,       // Open polygon (§12.5.6.9)
    Highlight,      // Yellow-highlight text markup (§12.5.6.10)
    Underline,      // Underline text markup (§12.5.6.10)
    Squiggly,       // Squiggly underline markup (§12.5.6.10)
    StrikeOut,      // Strikethrough markup (§12.5.6.10)
    Stamp,          // Rubber-stamp (§12.5.6.12)
    Caret,          // Caret insertion (§12.5.6.11)
    Ink,            // Freehand drawing (§12.5.6.13)
    Popup,          // Pop-up comment window (§12.5.6.14)
    FileAttachment, // Attached file (§12.5.6.15)
    Sound,          // Sound (§12.5.6.16)
    Movie,          // Movie (§12.5.6.17)
    Widget,         // Form field (§12.5.6.19)
    Screen,         // Media (§12.5.6.18)
    Watermark,      // Fixed watermark (§12.5.6.22)
    Redact          // Redaction annotation (§12.5.6.23)
}

/// <summary>
/// Annotation flags defined in ISO 32000-2:2020 §12.5.3 table 165.
/// </summary>
[Flags]
public enum PdfAnnotationFlags
{
    None        = 0,
    Invisible   = 1,
    Hidden      = 2,
    Print       = 4,
    NoZoom      = 8,
    NoRotate    = 16,
    NoView      = 32,
    ReadOnly    = 64,
    Locked      = 128,
    ToggleNoView = 256,
    LockedContents = 512
}

/// <summary>
/// A PDF annotation read from a page's /Annots array.
/// Covers the common properties from §12.5.2 plus subtype-specific ones.
/// </summary>
public sealed class PdfAnnotation
{
    // ── Common properties (§12.5.2 Table 164) ────────────────────────────────

    /// <summary>Annotation subtype (e.g. Highlight, Link, Widget).</summary>
    public PdfAnnotationSubtype Subtype { get; }

    /// <summary>Bounding rectangle in PDF points (Y-up).</summary>
    public PdfRectangle Rect { get; }

    /// <summary>Tooltip / body text (/Contents).</summary>
    public string? Contents { get; }

    /// <summary>Author or annotation title (/T).</summary>
    public string? Author { get; }

    /// <summary>Last-modification date (/M).</summary>
    public DateTimeOffset? ModDate { get; }

    /// <summary>Creation date (/CreationDate) — markup annotations only.</summary>
    public DateTimeOffset? CreationDate { get; }

    /// <summary>Annotation colour (/C — first three components as R,G,B 0-1).</summary>
    public (double R, double G, double B)? Color { get; }

    /// <summary>Annotation flags (/F).</summary>
    public PdfAnnotationFlags Flags { get; }

    /// <summary>Annotation name/identifier (/NM).</summary>
    public string? Name { get; }

    // ── Text-markup specific (§12.5.6.10 — Highlight/Underline/Squiggly/StrikeOut) ──

    /// <summary>
    /// Quad points defining the marked region (/QuadPoints).
    /// Each group of 4 PdfPoints forms one quadrilateral (8 numbers per rect).
    /// </summary>
    public IReadOnlyList<PdfRectangle>? QuadPoints { get; }

    // ── Link-specific (§12.5.6.5) ────────────────────────────────────────────

    /// <summary>1-based destination page for internal links (null for external/URI).</summary>
    public int? DestinationPage { get; }

    /// <summary>URI string for external links.</summary>
    public string? Uri { get; }

    // ── Text (sticky note) specific (§12.5.6.4) ──────────────────────────────

    /// <summary>Whether the pop-up window is initially open (/Open).</summary>
    public bool IsOpen { get; }

    /// <summary>Icon name for Text (sticky-note) annotations (/Name), e.g. "Note", "Comment".</summary>
    public string? IconName { get; }

    // ── Line-specific (§12.5.6.7) ────────────────────────────────────────────

    /// <summary>Endpoints (x1, y1, x2, y2) for Line annotations (/L).</summary>
    public (double X1, double Y1, double X2, double Y2)? LineEndpoints { get; }

    // ── Polygon / PolyLine specific (§12.5.6.9) ──────────────────────────────

    /// <summary>Vertex points for Polygon and PolyLine annotations (/Vertices).</summary>
    public IReadOnlyList<(double X, double Y)>? Vertices { get; }

    // ── Ink specific (§12.5.6.13) ────────────────────────────────────────────

    /// <summary>
    /// Ink strokes (/InkList): one inner list per stroke, each a sequence of
    /// (x,y) coordinates the user drew. Multiple strokes per Ink annot for
    /// pen-up / pen-down events.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>>? InkStrokes { get; }

    // ── FileAttachment specific (§12.5.6.15) ─────────────────────────────────

    /// <summary>Display filename for the attached file (/FS → /UF or /F).</summary>
    public string? AttachmentFileName { get; }

    /// <summary>
    /// Decoded contents of the embedded file. Pulled from /FS → /EF → /F (or
    /// /UF) → stream's DecodedData. Null if the attachment is unembedded
    /// (rare) or decoding failed.
    /// </summary>
    public byte[]? AttachmentBytes { get; }

    /// <summary>MIME type for the embedded file (/FS → /EF → /F → /Subtype).</summary>
    public string? AttachmentMimeType { get; }

    // ── Border / Border Style (§12.5.4) ──────────────────────────────────────

    /// <summary>
    /// Border width from the /Border array's third element. The /Border array
    /// shape is [HCornerR VCornerR Width DashPattern?]. Defaults to 1pt
    /// when the array is absent.
    /// </summary>
    public double? BorderWidth { get; }

    /// <summary>Border style ("S" Solid, "D" Dashed, "B" Beveled, "I" Inset, "U" Underline) from /BS /S.</summary>
    public string? BorderStyle { get; }

    /// <summary>Dash pattern (/Border[3] or /BS /D) — null when unset.</summary>
    public IReadOnlyList<double>? BorderDashPattern { get; }

    // ── Appearance (§12.5.5) ─────────────────────────────────────────────────

    /// <summary>True when an /AP appearance dictionary is present on the annotation.</summary>
    public bool HasAppearance { get; }

    // ── Raw dictionary ────────────────────────────────────────────────────────

    /// <summary>The underlying PDF dictionary for properties not exposed above.</summary>
    public PdfDictionary RawDictionary { get; }

    internal PdfAnnotation(
        PdfAnnotationSubtype subtype,
        PdfRectangle rect,
        string? contents,
        string? author,
        DateTimeOffset? modDate,
        DateTimeOffset? creationDate,
        (double R, double G, double B)? color,
        PdfAnnotationFlags flags,
        string? name,
        IReadOnlyList<PdfRectangle>? quadPoints,
        int? destinationPage,
        string? uri,
        bool isOpen,
        string? iconName,
        (double X1, double Y1, double X2, double Y2)? lineEndpoints,
        IReadOnlyList<(double X, double Y)>? vertices,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>>? inkStrokes,
        string? attachmentFileName,
        byte[]? attachmentBytes,
        string? attachmentMimeType,
        double? borderWidth,
        string? borderStyle,
        IReadOnlyList<double>? borderDashPattern,
        bool hasAppearance,
        PdfDictionary rawDictionary)
    {
        Subtype             = subtype;
        Rect                = rect;
        Contents            = contents;
        Author              = author;
        ModDate             = modDate;
        CreationDate        = creationDate;
        Color               = color;
        Flags               = flags;
        Name                = name;
        QuadPoints          = quadPoints;
        DestinationPage     = destinationPage;
        Uri                 = uri;
        IsOpen              = isOpen;
        IconName            = iconName;
        LineEndpoints       = lineEndpoints;
        Vertices            = vertices;
        InkStrokes          = inkStrokes;
        AttachmentFileName  = attachmentFileName;
        AttachmentBytes     = attachmentBytes;
        AttachmentMimeType  = attachmentMimeType;
        BorderWidth         = borderWidth;
        BorderStyle         = borderStyle;
        BorderDashPattern   = borderDashPattern;
        HasAppearance       = hasAppearance;
        RawDictionary       = rawDictionary;
    }

    /// <summary>Whether this is a text-markup annotation (Highlight/Underline/Squiggly/StrikeOut).</summary>
    public bool IsTextMarkup =>
        Subtype is PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline
                 or PdfAnnotationSubtype.Squiggly  or PdfAnnotationSubtype.StrikeOut;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Subtype} @ [{Rect.Left:F0},{Rect.Bottom:F0},{Rect.Right:F0},{Rect.Top:F0}]" +
        (Contents != null ? $" \"{Contents}\"" : "");
}
