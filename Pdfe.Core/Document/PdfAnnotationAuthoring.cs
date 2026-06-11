using Pdfe.Core.Primitives;

namespace Pdfe.Core.Document;

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
        annot.SetString("NM", $"pdfe-{Guid.NewGuid():N}");
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
