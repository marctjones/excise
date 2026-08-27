using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1098 — redacting a term from a form field must REWRITE the field's /AP
/// appearance (remove the term's glyphs), not drop it. A dropped appearance
/// plus /NeedAppearances renders as an empty field in every reader that ignores
/// that flag. Here the appearance uses a standard font (extractable), so the
/// glyph-level rewrite applies; issue18036's subsetted-font appearance is the
/// other branch (falls back to a leak-safe drop, unavoidable per #637).
/// </summary>
public class AppearanceRedactionTests
{
    // One-page PDF with a /Tx field whose /AP/N appearance draws
    // "Name: SECRET Jones" in Helvetica (WinAnsi — extractable).
    private static byte[] BuildFieldWithAppearance()
    {
        var ap = "/Tx BMC q BT /Helv 12 Tf 4 4 Td (Name: SECRET Jones) Tj ET Q EMC\n";
        var apBytes = Encoding.Latin1.GetByteCount(ap);
        var objs = new[]
        {
            // 1 catalog
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
            // 2 pages
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3 page (widget 7 is its annotation)
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Annots [7 0 R] /Resources << >> >>",
            // 4 helvetica
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            // 5 appearance stream (Form XObject)
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >> /Length {apBytes} >>\nstream\n{ap}endstream",
            // 6 acroform
            "<< /Fields [7 0 R] /DR << /Font << /Helv 4 0 R >> >> >>",
            // 7 widget/field (merged)
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Name: SECRET Jones) /Rect [20 40 220 60] /P 3 0 R /AP << /N 5 0 R >> >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        {
            offs[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string AppearanceText(PdfDocument doc)
    {
        // Widget 7 → /AP /N stream → its own letters (extract with its resources).
        var page = doc.GetPage(1);
        var annots = doc.Resolve(page.Dictionary.GetOptional("Annots")!) as PdfArray;
        var widget = doc.Resolve(annots![0]) as PdfDictionary;
        var ap = doc.Resolve(widget!.GetOptional("AP")!) as PdfDictionary;
        if (doc.Resolve(ap!.GetOptional("N") ?? PdfNull.Instance) is not PdfStream n) return "<no appearance>";
        var resources = doc.Resolve(n.GetOptional("Resources") ?? PdfNull.Instance) as PdfDictionary;
        var letters = new TextExtractor(page) { IncludeFormFieldValues = false }
            .ExtractLettersFrom(n.DecodedData, resources);
        return string.Concat(letters.Select(l => l.Value));
    }

    [Fact]
    public void RedactingATerm_RewritesTheAppearance_KeepsItAndRemovesOnlyTheTerm()
    {
        using var doc = PdfDocument.Open(BuildFieldWithAppearance());

        // guard: the appearance really draws the term before redaction.
        AppearanceText(doc).Should().Contain("SECRET").And.Contain("Jones");

        doc.RedactText("SECRET");

        using var ms = new System.IO.MemoryStream();
        doc.Save(ms);
        using var after = PdfDocument.Open(ms.ToArray());

        // The appearance SURVIVES (not dropped) and no longer draws the term,
        // but keeps the rest — the whole point of #1098.
        var page = after.GetPage(1);
        var annots = after.Resolve(page.Dictionary.GetOptional("Annots")!) as PdfArray;
        var widget = after.Resolve(annots![0]) as PdfDictionary;
        widget!.GetOptional("AP").Should().NotBeNull("the appearance must be rewritten, not dropped");

        var appearance = AppearanceText(after);
        appearance.Should().NotContain("SECRET", "the term's glyphs must be gone from the appearance");
        appearance.Should().Contain("Jones", "the rest of the field must still render");
    }
}
