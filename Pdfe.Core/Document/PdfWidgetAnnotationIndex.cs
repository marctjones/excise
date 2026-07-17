using Pdfe.Core.Primitives;

namespace Pdfe.Core.Document;

/// <summary>
/// Shared "does this page's own /Annots array contain this widget?" lookup,
/// used by both AcroForm widget-page resolution (#671) and orphaned
/// merged-field/widget discovery (#670). Both problems are the same underlying
/// question — trust the page's own /Annots array over /AcroForm linkage
/// metadata that is optional (/P, #671) or can simply be incomplete (the
/// /Fields tree, #670) — so the reference-matching walk lives here once.
/// </summary>
/// <remarks>
/// Identity is established by C# reference equality on the resolved
/// <see cref="PdfDictionary"/> instance rather than by comparing object/generation
/// numbers pulled off <see cref="PdfObject.ObjectNumber"/>. That property is only
/// populated when an object is parsed via the regular (non-compressed) path;
/// objects delivered through a cross-reference stream's compressed object
/// streams (<c>GetObjectFromStream</c>) come back with it unset. Reference
/// equality instead relies on <see cref="PdfDocument.GetObject(int)"/>'s object
/// cache: resolving the same indirect reference from /AcroForm/Fields and from
/// a page's /Annots always yields the exact same cached <see cref="PdfDictionary"/>
/// instance, so this works regardless of how the object was stored.
/// </remarks>
internal static class PdfWidgetAnnotationIndex
{
    /// <summary>
    /// Maps every Widget annotation dictionary referenced from any page's own
    /// /Annots array to that page's 1-based page number. Built by walking
    /// every page once. This is the ground-truth page association per PDF
    /// §12.5.2 — a widget belongs to whichever page lists it in /Annots,
    /// independent of whether the optional /P entry agrees or is even present.
    /// </summary>
    public static Dictionary<PdfDictionary, int> BuildWidgetToPageMap(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int pageNumber = 1; pageNumber <= doc.PageCount; pageNumber++)
        {
            var pageDict = doc.GetPage(pageNumber).Dictionary;
            foreach (var widget in GetPageAnnotWidgets(doc, pageDict))
            {
                // First page to claim a widget wins; a widget legitimately
                // belongs to exactly one page, so a second claim (malformed
                // PDF reusing the same annotation ref on two pages) is
                // ignored rather than overwriting the first association.
                if (!map.ContainsKey(widget))
                    map[widget] = pageNumber;
            }
        }
        return map;
    }

    /// <summary>
    /// Resolved Widget annotation dictionaries (/Subtype /Widget) directly in
    /// a page's own /Annots array. Annotation arrays are flat per §12.5.2 —
    /// this does not walk /Kids or /Parent chains.
    /// </summary>
    public static IEnumerable<PdfDictionary> GetPageAnnotWidgets(PdfDocument doc, PdfDictionary pageDict)
    {
        var annotsObj = pageDict.GetOptional("Annots");
        if (annotsObj == null) yield break;
        if (doc.Resolve(annotsObj) is not PdfArray annots) yield break;

        foreach (var entry in annots)
        {
            if (doc.Resolve(entry) is not PdfDictionary d) continue;
            if (d.GetNameOrNull("Subtype") == "Widget")
                yield return d;
        }
    }
}
