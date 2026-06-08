using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Authoring;

/// <summary>The PDF/A conformance level to target.</summary>
public enum PdfAConformance
{
    /// <summary>PDF/A-2b — visual reproducibility (recommended for archival).</summary>
    PdfA2B,

    /// <summary>PDF/A-1b — the original visual-reproducibility level.</summary>
    PdfA1B,
}

/// <summary>
/// Adds the document-level structures a PDF/A file requires: an XMP metadata
/// packet with the <c>pdfaid</c> identifier, and an OutputIntent referencing an
/// embedded sRGB ICC profile. The caller must also embed all fonts (e.g. via
/// <see cref="PdfDocumentBuilder.DefaultFont"/>) — PDF/A forbids the non-embedded
/// base-14 fonts.
/// </summary>
internal static class PdfAWriter
{
    public static void Apply(PdfDocument document, PdfAConformance conformance)
    {
        WriteXmp(document, conformance);
        WriteOutputIntent(document);
    }

    private static void WriteXmp(PdfDocument document, PdfAConformance conformance)
    {
        var part = conformance == PdfAConformance.PdfA1B ? 1 : 2;
        var title = XmlEscape(document.Info?.GetStringOrNull("Title") ?? document.Title ?? string.Empty);
        var producer = XmlEscape(document.Info?.GetStringOrNull("Producer") ?? "pdfe");
        var creator = XmlEscape(document.Info?.GetStringOrNull("Creator") ?? string.Empty);

        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append($"   <pdfaid:part>{part}</pdfaid:part>\n");
        sb.Append("   <pdfaid:conformance>B</pdfaid:conformance>\n");
        sb.Append("  </rdf:Description>\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append("   <dc:format>application/pdf</dc:format>\n");
        if (title.Length > 0)
        {
            sb.Append($"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>\n");
        }
        sb.Append("  </rdf:Description>\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        sb.Append($"   <pdf:Producer>{producer}</pdf:Producer>\n");
        sb.Append("  </rdf:Description>\n");
        if (creator.Length > 0)
        {
            sb.Append("  <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
            sb.Append($"   <xmp:CreatorTool>{creator}</xmp:CreatorTool>\n");
            sb.Append("  </rdf:Description>\n");
        }
        sb.Append(" </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var dict = new PdfDictionary();
        dict.SetName("Type", "Metadata");
        dict.SetName("Subtype", "XML");
        dict.SetInt("Length", bytes.Length);
        document.Catalog["Metadata"] = document.AddIndirectObject(new PdfStream(dict, bytes));
    }

    private static void WriteOutputIntent(PdfDocument document)
    {
        if (document.Catalog.ContainsKey("OutputIntents")) return;

        var icc = Convert.FromBase64String(SrgbIccProfileBase64);
        var iccDict = new PdfDictionary();
        iccDict.SetInt("N", 3);
        iccDict.SetInt("Length", icc.Length);
        var iccRef = document.AddIndirectObject(new PdfStream(iccDict, icc));

        var outputIntent = new PdfDictionary();
        outputIntent.SetName("Type", "OutputIntent");
        outputIntent.SetName("S", "GTS_PDFA1");
        outputIntent.SetString("OutputConditionIdentifier", "sRGB IEC61966-2.1");
        outputIntent.SetString("Info", "sRGB IEC61966-2.1");
        outputIntent.Set("DestOutputProfile", iccRef);

        document.Catalog["OutputIntents"] = new PdfArray(outputIntent);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Minimal valid sRGB v2 ICC profile (736 bytes), suitable for PDF/A OutputIntent embedding.
    private const string SrgbIccProfileBase64 =
        "AAAC4GxjbXMCEAAAbW50clJHQiBYWVogB+IAAwAUAAkADgAdYWNzcE1TRlQAAAAAc2F3c2N0cmwAAAAAAAAAAAAAAAAAAPbWAAEAAAAA0y1oYW5kk7I0qQ6wIoqY/Zqvo2eJmwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAABfY3BydAAAAQwAAAAMd3RwdAAAARgAAAAUclhZWgAAASwAAAAUZ1hZWgAAAUAAAAAUYlhZWgAAAVQAAAAUclRSQwAAAWgAAAF4Z1RSQwAAAWgAAAF4YlRSQwAAAWgAAAF4ZGVzYwAAAAAAAAAFc1JHQgAAAAAAAAAAAAAAAHRleHQAAAAAQ0MwAFhZWiAAAAAAAADzVAABAAAAARbJWFlaIAAAAAAAAG+gAAA48gAAA49YWVogAAAAAAAAYpYAALeJAAAY2lhZWiAAAAAAAAAkoAAAD4UAALbEY3VydgAAAAAAAAC2AAAAHAA4AFQAcACMAKgAxADhAQABIgFGAW0BlQHBAfACIAJVAosCxAMBAz8DggPGBA4EWQSnBPkFTAWkBf4GXAa+ByEHigf0CGMI1QlJCcMKPwq/C0ILyQxUDOENdA4JDqIPQA/gEIURLRHaEooTPhP2FLIVcRY2Fv0XyhiZGW4aRhsiHAMc5x3QHr0friCkIZ4inCOfJKUlsSbAJ9Uo7SoKKyssUS18Lqov3jEWMlIzlDTZNiQ3czjGOiA7fDzfPkU/sEEhQpZEEEWPRxJIm0ooS7tNUU7uUI9SNVPgVZBXRVkAWr5chF5MYBth72PHZaZniWlxa19tUW9KcUZzSnVRd155cXuIfaZ/yIHwhB6GUIiJisWNCY9RkZ+T85ZLmKubDp14n+eiW6TWp1ap26xnrvexj7Qqtsy5dLwhvtXBjcRMxxDJ2syrz3/SXNU92CTbEt4E4P7j/OcB6gztHPA081D2c/mb/Mr//w==";
}
