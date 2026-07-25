using System.Globalization;
using System.Text;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// FreeText annotation quadding (justification) — the /Q entry of
/// ISO 32000-2:2020 §12.5.6.6 Table 177.
/// </summary>
public enum PdfFreeTextQuadding
{
    /// <summary>Left-justified text (/Q 0, the default).</summary>
    LeftJustified = 0,

    /// <summary>Centered text (/Q 1).</summary>
    Centered = 1,

    /// <summary>Right-justified text (/Q 2).</summary>
    RightJustified = 2
}

/// <summary>
/// Programmatic PDF annotation authoring for common office workflows.
/// </summary>
public static class PdfAnnotationAuthoring
{
    /// <summary>
    /// Add a sticky-note Text annotation to a page.
    /// </summary>
    public static PdfAnnotation AddTextAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string contents,
        string? author = null,
        bool open = false,
        string iconName = "Note")
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);

        if (string.IsNullOrWhiteSpace(contents))
            throw new ArgumentException("Annotation contents must not be empty.", nameof(contents));
        if (string.IsNullOrWhiteSpace(iconName))
            throw new ArgumentException("Icon name must not be empty.", nameof(iconName));

        var annot = NewAnnotationDict("Text", rect);
        annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);
        annot.SetBool("Open", open);
        annot.SetName("Name", iconName);

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Add a rectangular Highlight text-markup annotation to a page.
    /// </summary>
    public static PdfAnnotation AddHighlightAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 1,
        double blue = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);
        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict("Highlight", normalized);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);

        annot["C"] = new PdfArray(
            new PdfReal(red),
            new PdfReal(green),
            new PdfReal(blue));

        annot["QuadPoints"] = new PdfArray(
            new PdfReal(normalized.Left),
            new PdfReal(normalized.Top),
            new PdfReal(normalized.Right),
            new PdfReal(normalized.Top),
            new PdfReal(normalized.Left),
            new PdfReal(normalized.Bottom),
            new PdfReal(normalized.Right),
            new PdfReal(normalized.Bottom));

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Add a Square (rectangle) shape annotation to a page
    /// (ISO 32000-2:2020 §12.5.6.8, Table 180).
    /// </summary>
    /// <remarks>
    /// Unlike the earlier sticky-note / highlight authoring methods, shape
    /// annotations are written with a baked normal appearance stream
    /// (<c>/AP /N</c>) so every ISO-conforming viewer renders the exact same
    /// pixels — excise, Acrobat, Preview, mutool, pdftocairo. Synthesized
    /// no-/AP fallbacks are viewer-specific and not interoperable (#626).
    /// </remarks>
    /// <param name="document">Target document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="rect">Annotation rectangle in PDF points (Y-up).</param>
    /// <param name="contents">Optional pop-up comment text (/Contents).</param>
    /// <param name="author">Optional author (/T).</param>
    /// <param name="red">Border color red component, 0–1 (/C).</param>
    /// <param name="green">Border color green component, 0–1 (/C).</param>
    /// <param name="blue">Border color blue component, 0–1 (/C).</param>
    /// <param name="borderWidth">Border stroke width in points (/BS /W). Pass 0
    /// for a borderless filled shape (interior color then required).</param>
    /// <param name="interiorRed">Interior fill red component, 0–1 (/IC).
    /// All three interior components must be given together, or none.</param>
    /// <param name="interiorGreen">Interior fill green component, 0–1 (/IC).</param>
    /// <param name="interiorBlue">Interior fill blue component, 0–1 (/IC).</param>
    public static PdfAnnotation AddSquareAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 0,
        double blue = 0,
        double borderWidth = 1,
        double? interiorRed = null,
        double? interiorGreen = null,
        double? interiorBlue = null)
        => AddShapeAnnotation(
            document, pageNumber, rect, isEllipse: false, contents, author,
            red, green, blue, borderWidth, interiorRed, interiorGreen, interiorBlue);

    /// <summary>
    /// Add a Circle (ellipse) shape annotation to a page
    /// (ISO 32000-2:2020 §12.5.6.8, Table 180). The ellipse is inscribed in
    /// <paramref name="rect"/>. See <see cref="AddSquareAnnotation"/> for
    /// parameter semantics — the two subtypes share Table 180.
    /// </summary>
    public static PdfAnnotation AddCircleAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 0,
        double blue = 0,
        double borderWidth = 1,
        double? interiorRed = null,
        double? interiorGreen = null,
        double? interiorBlue = null)
        => AddShapeAnnotation(
            document, pageNumber, rect, isEllipse: true, contents, author,
            red, green, blue, borderWidth, interiorRed, interiorGreen, interiorBlue);

    /// <summary>
    /// Add a FreeText annotation — a text box drawn directly on the page
    /// (ISO 32000-2:2020 §12.5.6.6, Table 177). This is the most common
    /// review annotation: unlike a sticky note, the text is always visible.
    /// </summary>
    /// <remarks>
    /// The annotation carries both the machine-readable entries — /Contents,
    /// /DA (default appearance string: color + Helvetica + size), /Q
    /// (quadding) — and a baked normal appearance stream (<c>/AP /N</c>) that
    /// actually draws the text with BT/Tf/Td/Tj, so every ISO-conforming
    /// viewer renders the same pixels (#626). The /DA font is the base-14
    /// Helvetica referenced as <c>/Helv</c>; the same font dictionary is
    /// placed in the appearance stream's own /Resources so the appearance is
    /// self-contained. Full font embedding is out of scope. Characters
    /// outside printable ASCII are replaced with '?' in the drawn appearance
    /// (the /Contents string keeps the full text).
    /// </remarks>
    /// <param name="document">Target document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="rect">Annotation rectangle in PDF points (Y-up).</param>
    /// <param name="text">The text shown in the box (/Contents, and drawn by /AP).</param>
    /// <param name="author">Optional author (/T).</param>
    /// <param name="fontSize">Font size in points for the /DA string and the appearance.</param>
    /// <param name="textRed">Text color red component, 0–1 (in /DA).</param>
    /// <param name="textGreen">Text color green component, 0–1 (in /DA).</param>
    /// <param name="textBlue">Text color blue component, 0–1 (in /DA).</param>
    /// <param name="quadding">Justification of the text within the box (/Q).</param>
    /// <param name="borderWidth">Border stroke width in points (/BS /W); 0 for
    /// no border. The border is stroked in the text color.</param>
    /// <param name="backgroundRed">Background fill red component, 0–1 (/C).
    /// All three background components must be given together, or none.</param>
    /// <param name="backgroundGreen">Background fill green component, 0–1 (/C).</param>
    /// <param name="backgroundBlue">Background fill blue component, 0–1 (/C).</param>
    public static PdfAnnotation AddFreeTextAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string text,
        string? author = null,
        double fontSize = 12,
        double textRed = 0,
        double textGreen = 0,
        double textBlue = 0,
        PdfFreeTextQuadding quadding = PdfFreeTextQuadding.LeftJustified,
        double borderWidth = 0,
        double? backgroundRed = null,
        double? backgroundGreen = null,
        double? backgroundBlue = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("FreeText annotation text must not be empty.", nameof(text));
        if (double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize),
                "Font size must be a finite, positive number of points.");
        ValidateColor(textRed, nameof(textRed));
        ValidateColor(textGreen, nameof(textGreen));
        ValidateColor(textBlue, nameof(textBlue));
        if (!Enum.IsDefined(quadding))
            throw new ArgumentOutOfRangeException(nameof(quadding));
        if (double.IsNaN(borderWidth) || double.IsInfinity(borderWidth) || borderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(borderWidth),
                "Border width must be a finite, non-negative number of points.");

        var backgroundSet = new[] { backgroundRed, backgroundGreen, backgroundBlue }
            .Count(c => c.HasValue);
        if (backgroundSet is not (0 or 3))
            throw new ArgumentException(
                "Background color requires all three of backgroundRed, backgroundGreen " +
                "and backgroundBlue, or none.", nameof(backgroundRed));

        (double R, double G, double B)? background = null;
        if (backgroundSet == 3)
        {
            ValidateColor(backgroundRed!.Value, nameof(backgroundRed));
            ValidateColor(backgroundGreen!.Value, nameof(backgroundGreen));
            ValidateColor(backgroundBlue!.Value, nameof(backgroundBlue));
            background = (backgroundRed.Value, backgroundGreen.Value, backgroundBlue.Value);
        }

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict("FreeText", normalized);

        annot.SetString("Contents", text);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);

        // Markup annotations carry /CreationDate (§12.5.6.2 Table 172).
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        // /DA — required by Table 177. Fill color + base-14 Helvetica.
        annot.SetString("DA",
            $"{Num(textRed)} {Num(textGreen)} {Num(textBlue)} rg /Helv {Num(fontSize)} Tf");
        annot.SetInt("Q", (int)quadding);

        // For FreeText, viewers treat /C as the box background color.
        if (background is { } bg)
            annot["C"] = new PdfArray(
                new PdfReal(bg.R), new PdfReal(bg.G), new PdfReal(bg.B));

        var bs = new PdfDictionary();
        bs.SetName("Type", "Border");
        bs.SetNumber("W", borderWidth);
        bs.SetName("S", "S");
        annot["BS"] = bs;

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildFreeTextAppearanceStream(
            document, normalized, text, fontSize,
            (textRed, textGreen, textBlue), quadding, borderWidth, background);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Add an Ink (freehand) annotation — one or more hand-drawn polylines
    /// (ISO 32000-2:2020 §12.5.6.13, Table 182). Each stroke is a list of
    /// (x, y) points in PDF page coordinates (Y-up); consecutive points are
    /// connected with straight lines.
    /// </summary>
    /// <remarks>
    /// The annotation carries the machine-readable entries — /InkList (an
    /// array of arrays of alternating x/y numbers, one inner array per
    /// stroke), /C stroke color, /BS border width — and a baked normal
    /// appearance stream (<c>/AP /N</c>) that strokes each polyline with
    /// m/l…S in the annotation color at the /BS width, so every
    /// ISO-conforming viewer renders the same pixels (#626). /Rect is the
    /// bounding box of all points, expanded by half the stroke width so
    /// round line caps at the extreme points are not clipped. The appearance
    /// is fully self-contained (paths only, no fonts or images).
    /// </remarks>
    /// <param name="document">Target document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="strokes">One or more polylines; each needs at least two
    /// points, in PDF page coordinates (Y-up).</param>
    /// <param name="contents">Optional pop-up comment text (/Contents).</param>
    /// <param name="author">Optional author (/T).</param>
    /// <param name="red">Stroke color red component, 0–1 (/C).</param>
    /// <param name="green">Stroke color green component, 0–1 (/C).</param>
    /// <param name="blue">Stroke color blue component, 0–1 (/C).</param>
    /// <param name="borderWidth">Stroke (pen) width in points (/BS /W). Must
    /// be positive — a zero-width ink annotation would be invisible.</param>
    public static PdfAnnotation AddInkAnnotation(
        this PdfDocument document,
        int pageNumber,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
        string? contents = null,
        string? author = null,
        double red = 0,
        double green = 0,
        double blue = 0,
        double borderWidth = 2)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(strokes);

        if (strokes.Count == 0)
            throw new ArgumentException(
                "An ink annotation needs at least one stroke.", nameof(strokes));
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Count < 2)
                throw new ArgumentException(
                    "Each ink stroke needs at least two points.", nameof(strokes));
            foreach (var (x, y) in stroke)
                if (double.IsNaN(x) || double.IsInfinity(x) ||
                    double.IsNaN(y) || double.IsInfinity(y))
                    throw new ArgumentException(
                        "Ink stroke coordinates must be finite numbers.", nameof(strokes));
        }

        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));
        if (double.IsNaN(borderWidth) || double.IsInfinity(borderWidth) || borderWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(borderWidth),
                "Ink stroke width must be a finite, positive number of points — " +
                "a zero-width ink annotation would be invisible.");

        // /Rect: bounding box of every point, padded by half the stroke width
        // so the round line caps at the extreme points stay inside the BBox.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var stroke in strokes)
        foreach (var (x, y) in stroke)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        double pad = borderWidth / 2;
        var rect = new PdfRectangle(minX - pad, minY - pad, maxX + pad, maxY + pad);

        var annot = NewAnnotationDict("Ink", rect);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);

        // Markup annotations carry /CreationDate (§12.5.6.2 Table 172).
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        // /InkList — Table 182: an array of arrays; each inner array holds
        // alternating x/y page-space coordinates for one stroke.
        var inkList = new PdfArray();
        foreach (var stroke in strokes)
        {
            var points = new PdfArray();
            foreach (var (x, y) in stroke)
            {
                points.Add(x);
                points.Add(y);
            }
            inkList.Add(points);
        }
        annot["InkList"] = inkList;

        annot["C"] = new PdfArray(
            new PdfReal(red), new PdfReal(green), new PdfReal(blue));

        var bs = new PdfDictionary();
        bs.SetName("Type", "Border");
        bs.SetNumber("W", borderWidth);
        bs.SetName("S", "S");
        annot["BS"] = bs;

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildInkAppearanceStream(
            rect, strokes, (red, green, blue), borderWidth);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for an Ink annotation
    /// (§12.5.5 appearance streams). Each polyline is stroked with
    /// m/l…S in the annotation color at the /BS width, with round line
    /// caps and joins — the conventional freehand-pen look. The stream
    /// draws in a local space whose <c>/BBox</c> is <c>[0 0 w h]</c>
    /// (points translated by the /Rect origin); viewers map that box onto
    /// the annotation's <c>/Rect</c>. Fully self-contained: paths only.
    /// </summary>
    private static PdfStream BuildInkAppearanceStream(
        PdfRectangle rect,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
        (double R, double G, double B) stroke,
        double borderWidth)
    {
        double ox = rect.Left;
        double oy = rect.Bottom;

        var sb = new StringBuilder();
        sb.Append($"{Num(stroke.R)} {Num(stroke.G)} {Num(stroke.B)} RG\n");
        sb.Append($"{Num(borderWidth)} w\n");
        sb.Append("1 J\n1 j\n"); // round caps and joins — freehand pen look

        foreach (var polyline in strokes)
        {
            sb.Append($"{Num(polyline[0].X - ox)} {Num(polyline[0].Y - oy)} m\n");
            for (int i = 1; i < polyline.Count; i++)
                sb.Append($"{Num(polyline[i].X - ox)} {Num(polyline[i].Y - oy)} l\n");
            sb.Append("S\n");
        }

        var streamObj = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        streamObj.SetName("Type", "XObject");
        streamObj.SetName("Subtype", "Form");
        streamObj.SetInt("FormType", 1);
        streamObj["BBox"] = PdfArray.FromRectangle(0, 0, rect.Width, rect.Height);
        streamObj["Resources"] = new PdfDictionary();
        return streamObj;
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a FreeText annotation
    /// (§12.5.5 appearance streams). Draws, in order: the /C background fill,
    /// the border (stroked in the text color at /BS /W), then the text as
    /// BT/Tf/Td/Tj lines — word-wrapped to the box width with real Helvetica
    /// advance widths and offset per line for /Q quadding. The stream's own
    /// /Resources carries the /Helv font dictionary so the appearance is
    /// self-contained; the /BBox clips any overflowing text.
    /// </summary>
    private static PdfStream BuildFreeTextAppearanceStream(
        PdfDocument document,
        PdfRectangle rect,
        string text,
        double fontSize,
        (double R, double G, double B) textColor,
        PdfFreeTextQuadding quadding,
        double borderWidth,
        (double R, double G, double B)? background)
    {
        double w = rect.Width;
        double h = rect.Height;

        var sb = new StringBuilder();

        if (background is { } bg)
        {
            sb.Append($"{Num(bg.R)} {Num(bg.G)} {Num(bg.B)} rg\n");
            sb.Append($"0 0 {Num(w)} {Num(h)} re f\n");
        }

        if (borderWidth > 0)
        {
            // Inset by half the stroke width so the border lies fully inside
            // the BBox; never so far that the rectangle inverts.
            double inset = Math.Min(borderWidth / 2, Math.Min(w, h) / 2 * 0.999);
            sb.Append($"{Num(textColor.R)} {Num(textColor.G)} {Num(textColor.B)} RG\n");
            sb.Append($"{Num(borderWidth)} w\n");
            sb.Append($"{Num(inset)} {Num(inset)} {Num(w - 2 * inset)} {Num(h - 2 * inset)} re S\n");
        }

        // Text area inset: border plus a 2pt padding, matching what common
        // viewers leave around FreeText content.
        double pad = borderWidth + 2;
        double availableWidth = Math.Max(1, w - 2 * pad);

        var font = PdfFont.Helvetica(fontSize);
        var lines = WrapFreeTextLines(text, font, availableWidth);
        double leading = fontSize * 1.2;

        sb.Append("BT\n");
        sb.Append($"/Helv {Num(fontSize)} Tf\n");
        sb.Append($"{Num(textColor.R)} {Num(textColor.G)} {Num(textColor.B)} rg\n");

        // First baseline sits one ascender below the top padding edge.
        double prevX = 0, prevY = 0;
        double baseline = h - pad - fontSize * 0.8;
        foreach (var line in lines)
        {
            if (baseline < -fontSize)
                break; // fully below the BBox — clipped anyway, stop emitting

            double lineWidth = font.MeasureWidth(line);
            double x = quadding switch
            {
                PdfFreeTextQuadding.Centered => pad + (availableWidth - lineWidth) / 2,
                PdfFreeTextQuadding.RightJustified => w - pad - lineWidth,
                _ => pad
            };
            sb.Append($"{Num(x - prevX)} {Num(baseline - prevY)} Td\n");
            sb.Append('(').Append(EscapePdfTextString(line)).Append(") Tj\n");
            prevX = x;
            prevY = baseline;
            baseline -= leading;
        }

        sb.Append("ET\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);

        // Self-contained resources: the /Helv the /DA and the Tf refer to.
        var helv = new PdfDictionary();
        helv.SetName("Type", "Font");
        helv.SetName("Subtype", "Type1");
        helv.SetName("BaseFont", "Helvetica");
        helv.SetName("Encoding", "WinAnsiEncoding");

        var fonts = new PdfDictionary();
        fonts["Helv"] = document.AddIndirectObject(helv);
        var resources = new PdfDictionary();
        resources["Font"] = fonts;
        stream["Resources"] = resources;

        return stream;
    }

    /// <summary>
    /// Split text on explicit newlines, then greedily word-wrap each paragraph
    /// to <paramref name="maxWidth"/> points using real font advance widths.
    /// A single word wider than the box is hard-broken by characters.
    /// </summary>
    private static List<string> WrapFreeTextLines(string text, PdfFont font, double maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (font.MeasureWidth(paragraph) <= maxWidth)
            {
                result.Add(paragraph);
                continue;
            }

            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (font.MeasureWidth(candidate) <= maxWidth)
                {
                    current.Clear();
                    current.Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                // Word alone still too wide: hard-break by characters.
                var piece = new StringBuilder();
                foreach (var ch in word)
                {
                    if (piece.Length > 0 && font.MeasureWidth(piece.ToString() + ch) > maxWidth)
                    {
                        result.Add(piece.ToString());
                        piece.Clear();
                    }
                    piece.Append(ch);
                }
                current.Append(piece);
            }

            if (current.Length > 0)
                result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>
    /// Escape a line for a PDF literal string in the appearance stream.
    /// Printable-ASCII MVP: anything outside 0x20–0x7E draws as '?' (the
    /// annotation's /Contents keeps the full text — this only affects the
    /// baked appearance, which uses WinAnsi-encoded Helvetica).
    /// </summary>
    private static string EscapePdfTextString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(':  sb.Append("\\(");  break;
                case ')':  sb.Append("\\)");  break;
                default:
                    sb.Append(ch is < ' ' or > '~' ? '?' : ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Attach a fully-built annotation dictionary to a page — the shared
    /// /P + /Annots plumbing, reused by the XFDF importer
    /// (<c>Excise.Core.Forms.XfdfSerializer</c>) for subtypes that have no
    /// dedicated authoring method (#626).
    /// </summary>
    internal static PdfAnnotation AttachImported(PdfDocument document, int pageNumber, PdfDictionary annot)
        => AttachAnnotation(document, pageNumber, annot);

    private static PdfAnnotation AddShapeAnnotation(
        PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        bool isEllipse,
        string? contents,
        string? author,
        double red,
        double green,
        double blue,
        double borderWidth,
        double? interiorRed,
        double? interiorGreen,
        double? interiorBlue)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);
        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));

        if (double.IsNaN(borderWidth) || double.IsInfinity(borderWidth) || borderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(borderWidth),
                "Border width must be a finite, non-negative number of points.");

        var interiorSet = new[] { interiorRed, interiorGreen, interiorBlue }
            .Count(c => c.HasValue);
        if (interiorSet is not (0 or 3))
            throw new ArgumentException(
                "Interior color requires all three of interiorRed, interiorGreen " +
                "and interiorBlue, or none.", nameof(interiorRed));

        (double R, double G, double B)? interior = null;
        if (interiorSet == 3)
        {
            ValidateColor(interiorRed!.Value, nameof(interiorRed));
            ValidateColor(interiorGreen!.Value, nameof(interiorGreen));
            ValidateColor(interiorBlue!.Value, nameof(interiorBlue));
            interior = (interiorRed.Value, interiorGreen.Value, interiorBlue.Value);
        }

        if (borderWidth == 0 && interior == null)
            throw new ArgumentException(
                "A shape annotation with zero border width and no interior color " +
                "would be invisible. Give it a border, a fill, or both.",
                nameof(borderWidth));

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict(isEllipse ? "Circle" : "Square", normalized);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);

        // Markup annotations carry /CreationDate (§12.5.6.2 Table 172).
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        annot["C"] = new PdfArray(
            new PdfReal(red), new PdfReal(green), new PdfReal(blue));
        if (interior is { } ic)
            annot["IC"] = new PdfArray(
                new PdfReal(ic.R), new PdfReal(ic.G), new PdfReal(ic.B));

        var bs = new PdfDictionary();
        bs.SetName("Type", "Border");
        bs.SetNumber("W", borderWidth);
        bs.SetName("S", "S");
        annot["BS"] = bs;

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildShapeAppearanceStream(
            normalized, isEllipse, (red, green, blue), interior, borderWidth);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a Square or Circle annotation
    /// (§12.5.5 appearance streams). The stream draws in a local space whose
    /// <c>/BBox</c> is <c>[0 0 w h]</c>; viewers map that box onto the
    /// annotation's <c>/Rect</c>. Geometry is inset by half the border width so
    /// the stroke lies fully inside the BBox and nothing is clipped away.
    /// </summary>
    private static PdfStream BuildShapeAppearanceStream(
        PdfRectangle rect,
        bool isEllipse,
        (double R, double G, double B) stroke,
        (double R, double G, double B)? interior,
        double borderWidth)
    {
        double w = rect.Width;
        double h = rect.Height;

        // Inset the path by half the stroke width, but never so far that the
        // shape inverts — an oversized border degrades gracefully instead of
        // producing a negative-extent rectangle.
        double inset = Math.Min(borderWidth / 2, Math.Min(w, h) / 2 * 0.999);

        var sb = new StringBuilder();
        if (interior is { } ic)
            sb.Append($"{Num(ic.R)} {Num(ic.G)} {Num(ic.B)} rg\n");
        if (borderWidth > 0)
        {
            sb.Append($"{Num(stroke.R)} {Num(stroke.G)} {Num(stroke.B)} RG\n");
            sb.Append($"{Num(borderWidth)} w\n");
        }

        if (isEllipse)
            AppendEllipsePath(sb, inset, inset, w - inset, h - inset);
        else
            sb.Append($"{Num(inset)} {Num(inset)} {Num(w - 2 * inset)} {Num(h - 2 * inset)} re\n");

        // Paint operator: fill+stroke, fill only, or stroke only.
        sb.Append(interior != null
            ? (borderWidth > 0 ? "B\n" : "f\n")
            : "S\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);
        stream["Resources"] = new PdfDictionary();
        return stream;
    }

    /// <summary>
    /// Append a four-arc cubic Bézier approximation of the ellipse inscribed
    /// in the box (x0,y0)–(x1,y1), closed with <c>h</c>. Standard circle
    /// constant κ = 4(√2−1)/3 ≈ 0.5523 — the same approximation every PDF
    /// producer uses; the radial error is under 0.02% of the radius.
    /// </summary>
    private static void AppendEllipsePath(
        StringBuilder sb, double x0, double y0, double x1, double y1)
    {
        const double Kappa = 0.5522847498307936;
        double cx = (x0 + x1) / 2;
        double cy = (y0 + y1) / 2;
        double rx = (x1 - x0) / 2;
        double ry = (y1 - y0) / 2;
        double ox = rx * Kappa;
        double oy = ry * Kappa;

        sb.Append($"{Num(cx + rx)} {Num(cy)} m\n");
        sb.Append($"{Num(cx + rx)} {Num(cy + oy)} {Num(cx + ox)} {Num(cy + ry)} {Num(cx)} {Num(cy + ry)} c\n");
        sb.Append($"{Num(cx - ox)} {Num(cy + ry)} {Num(cx - rx)} {Num(cy + oy)} {Num(cx - rx)} {Num(cy)} c\n");
        sb.Append($"{Num(cx - rx)} {Num(cy - oy)} {Num(cx - ox)} {Num(cy - ry)} {Num(cx)} {Num(cy - ry)} c\n");
        sb.Append($"{Num(cx + ox)} {Num(cy - ry)} {Num(cx + rx)} {Num(cy - oy)} {Num(cx + rx)} {Num(cy)} c\n");
        sb.Append("h\n");
    }

    private static string Num(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static PdfDictionary NewAnnotationDict(string subtype, PdfRectangle rect)
    {
        var normalized = rect.Normalize();
        var annot = new PdfDictionary();
        annot.SetName("Type", "Annot");
        annot.SetName("Subtype", subtype);
        annot["Rect"] = PdfArray.FromRectangle(
            normalized.Left,
            normalized.Bottom,
            normalized.Right,
            normalized.Top);
        annot.SetInt("F", (int)PdfAnnotationFlags.Print);
        annot.SetString("NM", $"excise-{Guid.NewGuid():N}");
        annot.SetString("M", PdfDate(DateTimeOffset.UtcNow));
        return annot;
    }

    private static PdfAnnotation AttachAnnotation(PdfDocument document, int pageNumber, PdfDictionary annot)
    {
        var page = document.GetPage(pageNumber);
        var pageRef = FindPageRef(document, pageNumber);
        if (pageRef != null)
            annot["P"] = pageRef;

        var annotRef = document.AddIndirectObject(annot);
        var annots = GetOrCreateAnnotsArray(document, page.Dictionary);
        annots.Add(annotRef);

        return page.GetAnnotations().LastOrDefault(a => ReferenceEquals(a.RawDictionary, annot))
            ?? page.GetAnnotations().Last();
    }

    private static PdfArray GetOrCreateAnnotsArray(PdfDocument document, PdfDictionary pageDict)
    {
        var annotsObj = pageDict.GetOptional("Annots");
        if (annotsObj == null)
        {
            var created = new PdfArray();
            pageDict["Annots"] = created;
            return created;
        }

        if (document.Resolve(annotsObj) is PdfArray existing)
            return existing;

        var replacement = new PdfArray();
        pageDict["Annots"] = replacement;
        return replacement;
    }

    private static PdfReference? FindPageRef(PdfDocument document, int pageNumber)
    {
        var pagesObj = document.Catalog.GetOptional("Pages");
        if (pagesObj == null) return null;
        if (document.Resolve(pagesObj) is not PdfDictionary pages) return null;

        int target = pageNumber - 1;
        int counter = 0;
        return WalkKids(document, pages, ref counter, target);
    }

    private static PdfReference? WalkKids(
        PdfDocument document,
        PdfDictionary node,
        ref int counter,
        int target)
    {
        var kidsObj = node.GetOptional("Kids");
        if (kidsObj == null || document.Resolve(kidsObj) is not PdfArray kids)
            return null;

        foreach (var kidObj in kids)
        {
            if (document.Resolve(kidObj) is not PdfDictionary kid) continue;
            var type = kid.GetNameOrNull("Type");

            if (type == "Page")
            {
                if (counter == target)
                    return kidObj as PdfReference;
                counter++;
            }
            else if (type == "Pages")
            {
                var hit = WalkKids(document, kid, ref counter, target);
                if (hit != null) return hit;
            }
        }

        return null;
    }

    private static void ValidateRect(PdfRectangle rect)
    {
        var normalized = rect.Normalize();
        if (normalized.Width <= 0 || normalized.Height <= 0)
            throw new ArgumentException("Annotation rectangle must have positive width and height.", nameof(rect));
    }

    private static void ValidateColor(double value, string name)
    {
        if (value is < 0 or > 1 || double.IsNaN(value))
            throw new ArgumentOutOfRangeException(name, "Color components must be between 0 and 1.");
    }

    private static string PdfDate(DateTimeOffset date)
        => $"D:{date.UtcDateTime:yyyyMMddHHmmss}+00'00'";
}
