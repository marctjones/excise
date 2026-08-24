using System.Linq;
using System.Text;
using Excise.Core.Document;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1117 — counts of the document structures a character-level text diff cannot
/// see: pages, links, bookmarks, form fields, attachments, and whether the file
/// still claims PDF/A. A redaction that silently drops any of these has done
/// collateral damage the collateral harness's text delta is blind to. Shared by
/// <see cref="StructuralConservationTests"/> (the dropped-bookmark proof) and the
/// benchmark's before/after column.
/// </summary>
public readonly record struct StructuralInventory(
    int Pages, int Links, int Bookmarks, int FormFields, int Attachments, bool IsPdfA)
{
    public static StructuralInventory Of(PdfDocument doc)
    {
        var links = 0;
        for (var p = 1; p <= doc.PageCount; p++)
            links += doc.GetPage(p).GetAnnotations()
                .Count(a => a.Subtype == PdfAnnotationSubtype.Link);

        var bookmarks = CountBookmarks(PdfOutlineParser.Parse(doc));
        var fields = doc.GetAcroForm()?.Fields.Count ?? 0;
        var attachments = doc.GetEmbeddedFiles().Count;
        var isPdfA = doc.EnumerateMetadataStreams()
            .Any(s => Encoding.Latin1.GetString(s.DecodedData).Contains("pdfaid"));

        return new StructuralInventory(doc.PageCount, links, bookmarks, fields, attachments, isPdfA);
    }

    /// <summary>The structures present here but missing in <paramref name="after"/>.</summary>
    public string DroppedVersus(StructuralInventory after)
    {
        var d = new System.Collections.Generic.List<string>();
        if (after.Pages < Pages) d.Add($"pages {Pages}->{after.Pages}");
        if (after.Links < Links) d.Add($"links {Links}->{after.Links}");
        if (after.Bookmarks < Bookmarks) d.Add($"bookmarks {Bookmarks}->{after.Bookmarks}");
        if (after.FormFields < FormFields) d.Add($"fields {FormFields}->{after.FormFields}");
        if (after.Attachments < Attachments) d.Add($"attachments {Attachments}->{after.Attachments}");
        if (IsPdfA && !after.IsPdfA) d.Add("pdf/a lost");
        return string.Join(", ", d);
    }

    private static int CountBookmarks(System.Collections.Generic.IReadOnlyList<PdfOutlineItem> items)
        => items.Sum(i => 1 + CountBookmarks(i.Children));
}
