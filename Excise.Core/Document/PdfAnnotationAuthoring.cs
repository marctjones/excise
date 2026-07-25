using System.Globalization;
using System.Text;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

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
