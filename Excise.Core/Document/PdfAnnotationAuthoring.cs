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

    // ── Text markup: Underline / StrikeOut / Squiggly (#626, ISO 32000-2 §12.5.6.10) ──

    /// <summary>
    /// Add an Underline text-markup annotation — a straight line drawn under
    /// the marked text (ISO 32000-2:2020 §12.5.6.10, Table 179). Mirrors
    /// <see cref="AddHighlightAnnotation"/>'s shape: a single axis-aligned
    /// <paramref name="rect"/> becomes one /QuadPoints quad. Unlike Highlight
    /// (which most viewers synthesize an appearance for), Underline carries a
    /// baked <c>/AP /N</c> stroke so every viewer draws the same line (#626).
    /// </summary>
    public static PdfAnnotation AddUnderlineAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 0,
        double blue = 0)
        => AddTextMarkupAnnotation(document, pageNumber, rect, "Underline", contents, author, red, green, blue);

    /// <summary>
    /// Add a StrikeOut text-markup annotation — a line through the middle of
    /// the marked text (ISO 32000-2:2020 §12.5.6.10, Table 179). See
    /// <see cref="AddUnderlineAnnotation"/> for shared parameter semantics.
    /// </summary>
    public static PdfAnnotation AddStrikeOutAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 0,
        double blue = 0)
        => AddTextMarkupAnnotation(document, pageNumber, rect, "StrikeOut", contents, author, red, green, blue);

    /// <summary>
    /// Add a Squiggly text-markup annotation — a wavy underline (ISO
    /// 32000-2:2020 §12.5.6.10, Table 179). See
    /// <see cref="AddUnderlineAnnotation"/> for shared parameter semantics.
    /// </summary>
    public static PdfAnnotation AddSquigglyAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string? contents = null,
        string? author = null,
        double red = 1,
        double green = 0,
        double blue = 0)
        => AddTextMarkupAnnotation(document, pageNumber, rect, "Squiggly", contents, author, red, green, blue);

    private static PdfAnnotation AddTextMarkupAnnotation(
        PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string subtype,
        string? contents,
        string? author,
        double red,
        double green,
        double blue)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);
        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict(subtype, normalized);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);

        // Markup annotations carry /CreationDate (§12.5.6.2 Table 172).
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        annot["C"] = new PdfArray(new PdfReal(red), new PdfReal(green), new PdfReal(blue));

        annot["QuadPoints"] = new PdfArray(
            new PdfReal(normalized.Left), new PdfReal(normalized.Top),
            new PdfReal(normalized.Right), new PdfReal(normalized.Top),
            new PdfReal(normalized.Left), new PdfReal(normalized.Bottom),
            new PdfReal(normalized.Right), new PdfReal(normalized.Bottom));

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildTextMarkupAppearanceStream(normalized, subtype, (red, green, blue));
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for an Underline/StrikeOut/Squiggly
    /// markup annotation. Draws a single stroked line (Underline/StrikeOut) or
    /// a zig-zag (Squiggly) across the local BBox width, positioned per
    /// conventional placement: underline near the baseline (~12% up from the
    /// bottom), strikeout through the visual middle (~45% up), squiggly at the
    /// underline height.
    /// </summary>
    private static PdfStream BuildTextMarkupAppearanceStream(
        PdfRectangle rect, string subtype, (double R, double G, double B) color)
    {
        double w = rect.Width;
        double h = rect.Height;
        double lineWidth = Math.Max(0.5, h * 0.06);

        var sb = new StringBuilder();
        sb.Append($"{Num(color.R)} {Num(color.G)} {Num(color.B)} RG\n");
        sb.Append($"{Num(lineWidth)} w\n");

        if (subtype == "Squiggly")
        {
            double baseline = h * 0.12;
            double amplitude = Math.Max(1, h * 0.06);
            double period = Math.Max(2, h * 0.18);
            sb.Append($"0 {Num(baseline)} m\n");
            bool up = true;
            int emitted = 0;
            for (double x = period; x <= w + period && emitted < 200; x += period, emitted++)
            {
                double y = baseline + (up ? amplitude : -amplitude);
                sb.Append($"{Num(Math.Min(x, w))} {Num(y)} l\n");
                up = !up;
            }
            if (emitted == 0)
                sb.Append($"{Num(w)} {Num(baseline)} l\n");
            sb.Append("S\n");
        }
        else
        {
            double y = subtype == "StrikeOut" ? h * 0.45 : h * 0.12;
            sb.Append($"0 {Num(y)} m\n{Num(w)} {Num(y)} l\nS\n");
        }

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);
        stream["Resources"] = new PdfDictionary();
        return stream;
    }

    // ── Line / Arrow (#626, ISO 32000-2 §12.5.6.7) ───────────────────────────

    /// <summary>
    /// Add a Line annotation — a straight line between two points (ISO
    /// 32000-2:2020 §12.5.6.7, Table 178).
    /// </summary>
    /// <remarks>
    /// Carries <c>/L</c> (the two absolute-page-space endpoints) and a baked
    /// <c>/AP /N</c> stroke so every viewer draws the same line (#626).
    /// <c>/Rect</c> is the line's bounding box, padded by half the stroke
    /// width so the stroke isn't clipped.
    /// </remarks>
    public static PdfAnnotation AddLineAnnotation(
        this PdfDocument document,
        int pageNumber,
        double x1, double y1, double x2, double y2,
        string? contents = null,
        string? author = null,
        double red = 0,
        double green = 0,
        double blue = 0,
        double lineWidth = 1)
        => AddLineOrArrowAnnotation(
            document, pageNumber, x1, y1, x2, y2, contents, author,
            red, green, blue, lineWidth, startLineEnding: "None", endLineEnding: "None");

    /// <summary>
    /// Add an Arrow annotation — a Line annotation whose <c>/LE</c> entry
    /// gives one or both ends an arrowhead (ISO 32000-2:2020 §12.5.6.7,
    /// Table 178, <c>/LE</c> line-ending styles per Table 179). The default
    /// draws a closed arrowhead at the end point only — the common "points at
    /// X" review mark.
    /// </summary>
    /// <param name="startLineEnding">Line-ending style at (x1,y1): "None",
    /// "OpenArrow" or "ClosedArrow".</param>
    /// <param name="endLineEnding">Line-ending style at (x2,y2): "None",
    /// "OpenArrow" or "ClosedArrow".</param>
    public static PdfAnnotation AddArrowAnnotation(
        this PdfDocument document,
        int pageNumber,
        double x1, double y1, double x2, double y2,
        string? contents = null,
        string? author = null,
        double red = 0,
        double green = 0,
        double blue = 0,
        double lineWidth = 1,
        string startLineEnding = "None",
        string endLineEnding = "ClosedArrow")
        => AddLineOrArrowAnnotation(
            document, pageNumber, x1, y1, x2, y2, contents, author,
            red, green, blue, lineWidth, startLineEnding, endLineEnding);

    private static readonly HashSet<string> SupportedLineEndings =
        new(StringComparer.Ordinal) { "None", "OpenArrow", "ClosedArrow" };

    private static PdfAnnotation AddLineOrArrowAnnotation(
        PdfDocument document,
        int pageNumber,
        double x1, double y1, double x2, double y2,
        string? contents,
        string? author,
        double red, double green, double blue,
        double lineWidth,
        string startLineEnding,
        string endLineEnding)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (double.IsNaN(x1) || double.IsInfinity(x1) || double.IsNaN(y1) || double.IsInfinity(y1) ||
            double.IsNaN(x2) || double.IsInfinity(x2) || double.IsNaN(y2) || double.IsInfinity(y2))
            throw new ArgumentException("Line endpoints must be finite numbers.");
        if (x1 == x2 && y1 == y2)
            throw new ArgumentException("A line annotation needs two distinct endpoints.");

        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));
        if (double.IsNaN(lineWidth) || double.IsInfinity(lineWidth) || lineWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineWidth),
                "Line width must be a finite, positive number of points.");
        if (!SupportedLineEndings.Contains(startLineEnding))
            throw new ArgumentException(
                $"Unsupported start line ending \"{startLineEnding}\". Supported: " +
                string.Join(", ", SupportedLineEndings) + ".",
                nameof(startLineEnding));
        if (!SupportedLineEndings.Contains(endLineEnding))
            throw new ArgumentException(
                $"Unsupported end line ending \"{endLineEnding}\". Supported: " +
                string.Join(", ", SupportedLineEndings) + ".",
                nameof(endLineEnding));

        // Arrowheads extend past the endpoint; pad the rect enough to contain them.
        double headSize = Math.Max(6, lineWidth * 4);
        bool hasHead = startLineEnding != "None" || endLineEnding != "None";
        double pad = lineWidth / 2 + (hasHead ? headSize : 0);
        var rect = new PdfRectangle(
            Math.Min(x1, x2) - pad, Math.Min(y1, y2) - pad,
            Math.Max(x1, x2) + pad, Math.Max(y1, y2) + pad);

        var annot = NewAnnotationDict("Line", rect);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        annot["L"] = new PdfArray(new PdfReal(x1), new PdfReal(y1), new PdfReal(x2), new PdfReal(y2));
        annot["C"] = new PdfArray(new PdfReal(red), new PdfReal(green), new PdfReal(blue));
        annot["LE"] = new PdfArray(new PdfName(startLineEnding), new PdfName(endLineEnding));

        var bs = new PdfDictionary();
        bs.SetName("Type", "Border");
        bs.SetNumber("W", lineWidth);
        bs.SetName("S", "S");
        annot["BS"] = bs;

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildLineAppearanceStream(
            rect, x1, y1, x2, y2, (red, green, blue), lineWidth, startLineEnding, endLineEnding, headSize);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a Line/Arrow annotation. The
    /// stream draws in a local space whose <c>/BBox</c> is <c>[0 0 w h]</c>
    /// (points translated by the /Rect origin), strokes the line itself, then
    /// appends a triangular arrowhead at each end whose <c>/LE</c> style is
    /// not "None".
    /// </summary>
    private static PdfStream BuildLineAppearanceStream(
        PdfRectangle rect,
        double x1, double y1, double x2, double y2,
        (double R, double G, double B) color,
        double lineWidth,
        string startLineEnding,
        string endLineEnding,
        double headSize)
    {
        double ox = rect.Left, oy = rect.Bottom;
        double lx1 = x1 - ox, ly1 = y1 - oy, lx2 = x2 - ox, ly2 = y2 - oy;

        var sb = new StringBuilder();
        sb.Append($"{Num(color.R)} {Num(color.G)} {Num(color.B)} RG\n");
        sb.Append($"{Num(color.R)} {Num(color.G)} {Num(color.B)} rg\n");
        sb.Append($"{Num(lineWidth)} w\n1 J\n");
        sb.Append($"{Num(lx1)} {Num(ly1)} m\n{Num(lx2)} {Num(ly2)} l\nS\n");

        double dx = lx2 - lx1, dy = ly2 - ly1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux = len > 0 ? dx / len : 1, uy = len > 0 ? dy / len : 0;

        if (endLineEnding != "None")
            AppendArrowHead(sb, lx2, ly2, ux, uy, endLineEnding, headSize);
        if (startLineEnding != "None")
            AppendArrowHead(sb, lx1, ly1, -ux, -uy, startLineEnding, headSize);

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, rect.Width, rect.Height);
        stream["Resources"] = new PdfDictionary();
        return stream;
    }

    /// <summary>
    /// Append a triangular arrowhead at (tipX,tipY) pointing along the unit
    /// direction (dirX,dirY) — the line's own direction for an end-of-line
    /// head, or its negation for a start-of-line head. "ClosedArrow" fills
    /// the triangle; "OpenArrow" strokes just the two wings (open at the
    /// base, per the ISO 32000-2 Table 179 line-ending gallery).
    /// </summary>
    private static void AppendArrowHead(
        StringBuilder sb, double tipX, double tipY, double dirX, double dirY, string style, double size)
    {
        double px = -dirY, py = dirX; // perpendicular to the direction
        double wingSpread = size * 0.4;

        double baseX = tipX - dirX * size, baseY = tipY - dirY * size;
        double leftX = baseX + px * wingSpread, leftY = baseY + py * wingSpread;
        double rightX = baseX - px * wingSpread, rightY = baseY - py * wingSpread;

        if (style == "ClosedArrow")
        {
            sb.Append($"{Num(tipX)} {Num(tipY)} m\n");
            sb.Append($"{Num(leftX)} {Num(leftY)} l\n");
            sb.Append($"{Num(rightX)} {Num(rightY)} l\n");
            sb.Append("h\nB\n");
        }
        else // OpenArrow
        {
            sb.Append($"{Num(leftX)} {Num(leftY)} m\n");
            sb.Append($"{Num(tipX)} {Num(tipY)} l\n");
            sb.Append($"{Num(rightX)} {Num(rightY)} l\nS\n");
        }
    }

    // ── Polygon / PolyLine (#626, ISO 32000-2 §12.5.6.9) ─────────────────────

    /// <summary>
    /// Add a Polygon annotation — a closed multi-sided shape (ISO
    /// 32000-2:2020 §12.5.6.9, Table 178). See <see cref="AddSquareAnnotation"/>
    /// for the shared border/interior-fill parameter semantics.
    /// </summary>
    public static PdfAnnotation AddPolygonAnnotation(
        this PdfDocument document,
        int pageNumber,
        IReadOnlyList<(double X, double Y)> vertices,
        string? contents = null,
        string? author = null,
        double red = 0,
        double green = 0,
        double blue = 0,
        double borderWidth = 1,
        double? interiorRed = null,
        double? interiorGreen = null,
        double? interiorBlue = null)
        => AddPolyAnnotation(
            document, pageNumber, vertices, isClosed: true, contents, author,
            red, green, blue, borderWidth, interiorRed, interiorGreen, interiorBlue);

    /// <summary>
    /// Add a PolyLine annotation — an open multi-segment line (ISO
    /// 32000-2:2020 §12.5.6.9, Table 178). Unlike Polygon, PolyLine has no
    /// interior fill — it is always stroke-only.
    /// </summary>
    public static PdfAnnotation AddPolyLineAnnotation(
        this PdfDocument document,
        int pageNumber,
        IReadOnlyList<(double X, double Y)> vertices,
        string? contents = null,
        string? author = null,
        double red = 0,
        double green = 0,
        double blue = 0,
        double borderWidth = 1)
        => AddPolyAnnotation(
            document, pageNumber, vertices, isClosed: false, contents, author,
            red, green, blue, borderWidth, null, null, null);

    private static PdfAnnotation AddPolyAnnotation(
        PdfDocument document,
        int pageNumber,
        IReadOnlyList<(double X, double Y)> vertices,
        bool isClosed,
        string? contents,
        string? author,
        double red, double green, double blue,
        double borderWidth,
        double? interiorRed, double? interiorGreen, double? interiorBlue)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vertices);

        int minVertices = isClosed ? 3 : 2;
        if (vertices.Count < minVertices)
            throw new ArgumentException(
                $"A {(isClosed ? "Polygon" : "PolyLine")} annotation needs at least {minVertices} vertices.",
                nameof(vertices));
        foreach (var (x, y) in vertices)
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                throw new ArgumentException("Vertex coordinates must be finite numbers.", nameof(vertices));

        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));
        if (double.IsNaN(borderWidth) || double.IsInfinity(borderWidth) || borderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(borderWidth),
                "Border width must be a finite, non-negative number of points.");

        var interiorSet = new[] { interiorRed, interiorGreen, interiorBlue }.Count(c => c.HasValue);
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
                $"A {(isClosed ? "polygon" : "polyline")} with zero border width and no interior " +
                "color would be invisible. Give it a border, a fill, or both.",
                nameof(borderWidth));

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in vertices)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        double pad = borderWidth / 2;
        var rect = new PdfRectangle(minX - pad, minY - pad, maxX + pad, maxY + pad);

        var annot = NewAnnotationDict(isClosed ? "Polygon" : "PolyLine", rect);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        var verticesArr = new PdfArray();
        foreach (var (x, y) in vertices)
        {
            verticesArr.Add(x);
            verticesArr.Add(y);
        }
        annot["Vertices"] = verticesArr;

        annot["C"] = new PdfArray(new PdfReal(red), new PdfReal(green), new PdfReal(blue));
        if (interior is { } ic)
            annot["IC"] = new PdfArray(new PdfReal(ic.R), new PdfReal(ic.G), new PdfReal(ic.B));

        var bs = new PdfDictionary();
        bs.SetName("Type", "Border");
        bs.SetNumber("W", borderWidth);
        bs.SetName("S", "S");
        annot["BS"] = bs;

        // Baked normal appearance so third-party viewers draw the same pixels.
        var apStream = BuildPolyAppearanceStream(rect, vertices, isClosed, (red, green, blue), interior, borderWidth);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a Polygon/PolyLine annotation
    /// — a single path visiting every vertex in order, closed with <c>h</c>
    /// for Polygon, left open for PolyLine.
    /// </summary>
    private static PdfStream BuildPolyAppearanceStream(
        PdfRectangle rect,
        IReadOnlyList<(double X, double Y)> vertices,
        bool isClosed,
        (double R, double G, double B) stroke,
        (double R, double G, double B)? interior,
        double borderWidth)
    {
        double ox = rect.Left, oy = rect.Bottom;
        var sb = new StringBuilder();
        if (interior is { } ic)
            sb.Append($"{Num(ic.R)} {Num(ic.G)} {Num(ic.B)} rg\n");
        if (borderWidth > 0)
        {
            sb.Append($"{Num(stroke.R)} {Num(stroke.G)} {Num(stroke.B)} RG\n");
            sb.Append($"{Num(borderWidth)} w\n1 j\n");
        }

        sb.Append($"{Num(vertices[0].X - ox)} {Num(vertices[0].Y - oy)} m\n");
        for (int i = 1; i < vertices.Count; i++)
            sb.Append($"{Num(vertices[i].X - ox)} {Num(vertices[i].Y - oy)} l\n");
        if (isClosed)
            sb.Append("h\n");

        sb.Append(interior != null
            ? (borderWidth > 0 ? "B\n" : "f\n")
            : "S\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, rect.Width, rect.Height);
        stream["Resources"] = new PdfDictionary();
        return stream;
    }

    // ── Stamp (#626, ISO 32000-2 §12.5.6.12) ─────────────────────────────────

    /// <summary>
    /// The standard rubber-stamp names defined in ISO 32000-2:2020 §12.5.6.12
    /// (Table 181) that <see cref="AddStampAnnotation"/> accepts for
    /// <c>stampName</c>.
    /// </summary>
    public static IReadOnlyList<string> StandardStampNames { get; } =
    [
        "Approved", "Experimental", "NotApproved", "AsIs", "Expired",
        "NotForPublicRelease", "Confidential", "Sold", "Departmental",
        "TopSecret", "Draft", "ForComment", "Final", "ForPublicRelease",
        "InformationOnly"
    ];

    private static readonly HashSet<string> StandardStampNameSet =
        new(StandardStampNames, StringComparer.Ordinal);

    private static readonly HashSet<string> NegativeStampNames = new(StringComparer.Ordinal)
        { "NotApproved", "Expired", "NotForPublicRelease", "Confidential", "TopSecret", "Draft" };

    private static readonly HashSet<string> PositiveStampNames = new(StringComparer.Ordinal)
        { "Approved", "Final", "Sold", "ForPublicRelease" };

    /// <summary>
    /// Add a Stamp annotation using one of the standard rubber-stamp names
    /// (ISO 32000-2:2020 §12.5.6.12, Table 181).
    /// </summary>
    /// <remarks>
    /// excise has no bundled stamp icon artwork, so the baked <c>/AP /N</c>
    /// appearance draws a bordered box with the stamp name as bold, centered
    /// text in a color matching common reviewer convention (red for
    /// negative/urgent stamps such as "Confidential"/"Draft"/"Expired", green
    /// for positive ones such as "Approved"/"Final", blue otherwise) — not
    /// Acrobat's own icon artwork, but every ISO-conforming viewer renders the
    /// exact same pixels, which is the property #626 cares about. For a
    /// company logo or other custom artwork use
    /// <see cref="AddImageStampAnnotation"/> instead.
    /// </remarks>
    /// <param name="stampName">One of <see cref="StandardStampNames"/>.</param>
    public static PdfAnnotation AddStampAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string stampName,
        string? contents = null,
        string? author = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateRect(rect);
        if (string.IsNullOrWhiteSpace(stampName) || !StandardStampNameSet.Contains(stampName))
            throw new ArgumentException(
                $"\"{stampName}\" is not a standard stamp name. Supported: " +
                string.Join(", ", StandardStampNames) + ".",
                nameof(stampName));

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict("Stamp", normalized);
        annot.SetName("Name", stampName);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        var color = StampColor(stampName);
        annot["C"] = new PdfArray(new PdfReal(color.R), new PdfReal(color.G), new PdfReal(color.B));

        var apStream = BuildStampAppearanceStream(document, normalized, stampName, color);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    private static (double R, double G, double B) StampColor(string stampName) =>
        NegativeStampNames.Contains(stampName) ? (0.8, 0, 0) :
        PositiveStampNames.Contains(stampName) ? (0, 0.55, 0) :
        (0, 0.3, 0.7);

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a standard-name Stamp: a
    /// stroked border plus the stamp name as bold Helvetica, sized to fit the
    /// box (bounded by both width and height) and centered.
    /// </summary>
    private static PdfStream BuildStampAppearanceStream(
        PdfDocument document, PdfRectangle rect, string label, (double R, double G, double B) color)
    {
        double w = rect.Width, h = rect.Height;
        double borderWidth = Math.Max(1, Math.Min(w, h) * 0.04);
        double inset = borderWidth / 2;

        var sb = new StringBuilder();
        sb.Append($"{Num(color.R)} {Num(color.G)} {Num(color.B)} RG\n");
        sb.Append($"{Num(borderWidth)} w\n");
        sb.Append($"{Num(inset)} {Num(inset)} {Num(w - 2 * inset)} {Num(h - 2 * inset)} re S\n");

        var unitFont = PdfFont.HelveticaBold(1);
        double widthAt1 = Math.Max(0.001, unitFont.MeasureWidth(label));
        double fontSizeByWidth = (w * 0.85) / widthAt1;
        double fontSizeByHeight = h * 0.4;
        double fontSize = Math.Max(4, Math.Min(fontSizeByWidth, fontSizeByHeight));
        var sized = PdfFont.HelveticaBold(fontSize);
        double textWidth = sized.MeasureWidth(label);
        double x = Math.Max(0, (w - textWidth) / 2);
        double y = h / 2 - fontSize * 0.35;

        sb.Append("BT\n");
        sb.Append($"/HelvB {Num(fontSize)} Tf\n");
        sb.Append($"{Num(color.R)} {Num(color.G)} {Num(color.B)} rg\n");
        sb.Append($"{Num(x)} {Num(y)} Td\n");
        sb.Append('(').Append(EscapePdfTextString(label)).Append(") Tj\n");
        sb.Append("ET\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);

        var helvBold = new PdfDictionary();
        helvBold.SetName("Type", "Font");
        helvBold.SetName("Subtype", "Type1");
        helvBold.SetName("BaseFont", "Helvetica-Bold");
        helvBold.SetName("Encoding", "WinAnsiEncoding");

        var fonts = new PdfDictionary();
        fonts["HelvB"] = document.AddIndirectObject(helvBold);
        var resources = new PdfDictionary();
        resources["Font"] = fonts;
        stream["Resources"] = resources;

        return stream;
    }

    /// <summary>
    /// Add a Stamp annotation whose appearance is a caller-supplied raster
    /// image (e.g. a company logo) rather than one of the standard names
    /// (ISO 32000-2:2020 §12.5.6.12).
    /// </summary>
    /// <remarks>
    /// The image is embedded as an uncompressed DeviceRGB Image XObject
    /// referenced from the baked <c>/AP /N</c> Form XObject, so every
    /// ISO-conforming viewer draws the exact same pixels (#626) — deliberately
    /// no dependency on an external image codec (JPEG/PNG decoding); callers
    /// that already have decoded pixels (e.g. from SkiaSharp in the GUI layer)
    /// can pass them straight through.
    /// </remarks>
    /// <param name="document">Target document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="rect">Annotation rectangle in PDF points (Y-up); the
    /// image is stretched to fill it.</param>
    /// <param name="rgbPixels">Top-down, row-major RGB24 pixel data: exactly
    /// <c>pixelWidth * pixelHeight * 3</c> bytes, 3 bytes (R,G,B) per pixel,
    /// no padding between rows.</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    public static PdfAnnotation AddImageStampAnnotation(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        byte[] rgbPixels,
        int pixelWidth,
        int pixelHeight,
        string? contents = null,
        string? author = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rgbPixels);
        ValidateRect(rect);

        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentException("Image dimensions must be positive.", nameof(pixelWidth));
        long expected = (long)pixelWidth * pixelHeight * 3;
        if (rgbPixels.LongLength != expected)
            throw new ArgumentException(
                $"rgbPixels must be exactly pixelWidth*pixelHeight*3 bytes ({expected}), " +
                $"got {rgbPixels.LongLength}.",
                nameof(rgbPixels));

        var normalized = rect.Normalize();
        var annot = NewAnnotationDict("Stamp", normalized);

        if (!string.IsNullOrWhiteSpace(contents))
            annot.SetString("Contents", contents);
        if (!string.IsNullOrWhiteSpace(author))
            annot.SetString("T", author);
        annot.SetString("CreationDate", PdfDate(DateTimeOffset.UtcNow));

        var apStream = BuildImageStampAppearanceStream(document, normalized, rgbPixels, pixelWidth, pixelHeight);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        annot["AP"] = ap;

        return AttachAnnotation(document, pageNumber, annot);
    }

    /// <summary>
    /// Build the <c>/AP /N</c> Form XObject for a custom image Stamp: an
    /// uncompressed DeviceRGB Image XObject, drawn full-bleed into the
    /// <c>/BBox</c> via the standard <c>cx 0 0 cy 0 0 cm /Im0 Do</c> unit-square
    /// mapping (§8.9.5.2).
    /// </summary>
    private static PdfStream BuildImageStampAppearanceStream(
        PdfDocument document, PdfRectangle rect, byte[] rgbPixels, int pixelWidth, int pixelHeight)
    {
        double w = rect.Width, h = rect.Height;

        var image = new PdfStream(rgbPixels);
        image.SetName("Type", "XObject");
        image.SetName("Subtype", "Image");
        image.SetInt("Width", pixelWidth);
        image.SetInt("Height", pixelHeight);
        image.SetName("ColorSpace", "DeviceRGB");
        image.SetInt("BitsPerComponent", 8);
        var imageRef = document.AddIndirectObject(image);

        var sb = new StringBuilder();
        sb.Append("q\n");
        sb.Append($"{Num(w)} 0 0 {Num(h)} 0 0 cm\n");
        sb.Append("/Im0 Do\n");
        sb.Append("Q\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);

        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        stream["Resources"] = resources;

        return stream;
    }

    // ── Edit / delete existing annotations (#626) ────────────────────────────

    /// <summary>
    /// Update an existing annotation's <c>/Contents</c> (comment/body text) in
    /// place. Pass <c>null</c> to remove the entry. Also refreshes <c>/M</c>
    /// (last-modified date, §12.5.2 Table 164's edit-tracking convention).
    /// </summary>
    public static void SetAnnotationContents(this PdfAnnotation annotation, string? contents)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (contents == null)
            annotation.RawDictionary.Remove("Contents");
        else
            annotation.RawDictionary.SetString("Contents", contents);
        annotation.RawDictionary.SetString("M", PdfDate(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Update an existing annotation's <c>/C</c> color in place (border/stroke
    /// color for shapes and lines, icon color for Text, background for
    /// FreeText). Does not touch <c>/IC</c> (interior fill) or repaint any
    /// existing <c>/AP</c> appearance stream — callers that need the baked
    /// pixels to match should re-author the annotation instead.
    /// </summary>
    public static void SetAnnotationColor(this PdfAnnotation annotation, double red, double green, double blue)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ValidateColor(red, nameof(red));
        ValidateColor(green, nameof(green));
        ValidateColor(blue, nameof(blue));
        annotation.RawDictionary["C"] = new PdfArray(new PdfReal(red), new PdfReal(green), new PdfReal(blue));
        annotation.RawDictionary.SetString("M", PdfDate(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Update an existing annotation's <c>/CA</c> constant opacity in place
    /// (ISO 32000-2:2020 §12.5.2 Table 164). 0 is fully transparent, 1 (the
    /// default when absent) is fully opaque.
    /// </summary>
    public static void SetAnnotationOpacity(this PdfAnnotation annotation, double opacity)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (double.IsNaN(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between 0 and 1.");
        annotation.RawDictionary.SetNumber("CA", opacity);
        annotation.RawDictionary.SetString("M", PdfDate(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Remove an annotation from a page's <c>/Annots</c> array.
    /// </summary>
    /// <remarks>
    /// The underlying indirect object is left in the xref (unreachable, and
    /// garbage-collected on the next full rewrite by whatever wrote the file)
    /// — this only detaches it from the page, matching how every other
    /// mutation in this class works on the in-memory object graph.
    /// </remarks>
    /// <returns><c>true</c> if the annotation was found and removed;
    /// <c>false</c> if it wasn't on that page's /Annots array (already
    /// removed, or belongs to a different page).</returns>
    public static bool RemoveAnnotation(this PdfDocument document, int pageNumber, PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(annotation);

        var page = document.GetPage(pageNumber);
        var annotsObj = page.Dictionary.GetOptional("Annots");
        if (annotsObj == null || document.Resolve(annotsObj) is not PdfArray annots)
            return false;

        for (int i = 0; i < annots.Count; i++)
        {
            if (document.Resolve(annots[i]) is PdfDictionary d && ReferenceEquals(d, annotation.RawDictionary))
            {
                annots.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    // ── Reply threads (#626, ISO 32000-2 §12.5.6.2 — /IRT and /RT) ───────────

    /// <summary>
    /// Turn <paramref name="reply"/> into a reply to <paramref name="parent"/>
    /// by setting <c>/IRT</c> (in-reply-to — an indirect reference to the
    /// parent annotation) and <c>/RT</c> (reply type).
    /// </summary>
    /// <remarks>
    /// Both annotations must already be attached to <paramref name="document"/>
    /// (created through one of the <c>Add*Annotation</c> methods, or
    /// imported) — <c>/IRT</c> needs the parent's real indirect object
    /// reference, which only exists once it has been written into the
    /// document.
    /// </remarks>
    /// <param name="replyType">"R" (the default — a visible threaded reply)
    /// or "Group" (groups the annotations without implying a reply
    /// relationship, per Table 173).</param>
    /// <exception cref="InvalidOperationException"><paramref name="parent"/>
    /// is not a top-level indirect object of <paramref name="document"/>.</exception>
    public static void SetReplyTo(
        this PdfAnnotation reply, PdfDocument document, PdfAnnotation parent, string replyType = "R")
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parent);
        if (replyType is not ("R" or "Group"))
            throw new ArgumentException("Reply type must be \"R\" or \"Group\".", nameof(replyType));

        var parentRef = document.GetReferenceTo(parent.RawDictionary)
            ?? throw new InvalidOperationException(
                "The parent annotation is not an indirect object of this document yet " +
                "— attach it first (Add*Annotation, or an XFDF/FDF import).");

        reply.RawDictionary["IRT"] = parentRef;
        reply.RawDictionary.SetName("RT", replyType);
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

    private static string Num(double value) => PdfNumberFormatter.Format(value);

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
