using System.Collections.Generic;
using System.Text;

namespace Excise.TestSupport;

/// <summary>
/// One way the rest of a document can still point AT a page after it leaves
/// <c>/Kids</c> (#2012). Any of them keeps the page dictionary reachable from the
/// trailer, and the page dictionary reaches its content.
/// </summary>
public enum RemovedPageBackReference
{
    None,
    Outline,
    NamedDestination,
    LinkOnKeptPage,
    OpenAction,
    StructureElement,
    /// <summary>Merged field and widget on the page, listed in /Fields.</summary>
    AcroFormWidget,
    /// <summary>Field in /Fields and /CO whose /Kids holds the page's widget.</summary>
    AcroFormFieldKids,
}

/// <summary>
/// A three-page PDF whose page 2 carries a unique token in each place a removed
/// page can survive a save: its content stream, a font only it uses, an image,
/// a text annotation and (for the form variants) a field value. Shared by the
/// Core leak tests and the Rendering qpdf oracle tests (#2012).
/// </summary>
internal static class RemovedPageFixtures
{
    public const string KeptOne = "KEPTPAGEONETEXT";
    public const string KeptThree = "KEPTPAGETHREETEXT";
    public const string PageText = "REMOVEDPAGETEXTSECRET";
    public const string FontName = "REMOVEDPAGEFONTSECRET";
    public const string ImageBytes = "REMOVEDIMAGESECRET";
    public const string AnnotText = "REMOVEDANNOTSECRET";
    public const string FieldValue = "REMOVEDFIELDVALUESECRET";

    /// <summary>Tokens only page 2 carries in every variant.</summary>
    public static readonly string[] PageTokens = [PageText, FontName, ImageBytes, AnnotText];

    /// <summary>Tokens that must be gone once page 2 is removed.</summary>
    public static string[] RemovedTokens(RemovedPageBackReference back)
        => back is RemovedPageBackReference.AcroFormWidget or RemovedPageBackReference.AcroFormFieldKids
            ? [.. PageTokens, FieldValue]
            : PageTokens;

    /// <summary>
    /// Three pages. Streams are uncompressed so the tokens are plain in the
    /// source. <paramref name="pageThreeSharesFont"/> makes page 3 use page 2's
    /// font too; <paramref name="widgetOnPageOneToo"/> gives the
    /// <see cref="RemovedPageBackReference.AcroFormFieldKids"/> field a second
    /// widget on page 1; <paramref name="nestPagesTwoAndThree"/> puts pages 2
    /// and 3 under an intermediate <c>/Pages</c> node.
    /// </summary>
    public static byte[] Build(
        RemovedPageBackReference back,
        string version,
        bool pageThreeSharesFont = false,
        bool widgetOnPageOneToo = false,
        bool nestPagesTwoAndThree = false)
    {
        // 1 catalog, 2 pages, 3/4/5 pages, 6/7/8 contents, 9 shared font,
        // 10 page-2 font, 11 page-2 image, 12 page-2 annotation, 13+ extras.
        var catalogExtra = "";
        var page1Annots = "";
        var page2Extra = "";
        var page2Annots = "12 0 R";
        var extras = new List<string>();
        const string p2 = "4 0 R";

        switch (back)
        {
            case RemovedPageBackReference.Outline:
                catalogExtra = "/Outlines 13 0 R";
                extras.Add("<< /Type /Outlines /First 14 0 R /Last 14 0 R /Count 1 >>");
                extras.Add($"<< /Title (Go to page two) /Parent 13 0 R /Dest [{p2} /Fit] >>");
                break;
            case RemovedPageBackReference.NamedDestination:
                catalogExtra = $"/Names << /Dests << /Names [(pagetwo) [{p2} /Fit]] >> >>";
                break;
            case RemovedPageBackReference.LinkOnKeptPage:
                page1Annots = "/Annots [13 0 R]";
                extras.Add($"<< /Type /Annot /Subtype /Link /Rect [72 600 200 620] /Border [0 0 0] /Dest [{p2} /XYZ null null null] >>");
                break;
            case RemovedPageBackReference.OpenAction:
                catalogExtra = $"/OpenAction [{p2} /Fit]";
                break;
            case RemovedPageBackReference.StructureElement:
                catalogExtra = "/MarkInfo << /Marked true >> /StructTreeRoot 13 0 R";
                page2Extra = "/StructParents 0";
                extras.Add("<< /Type /StructTreeRoot /K [14 0 R] >>");
                extras.Add($"<< /Type /StructElem /S /P /P 13 0 R /Pg {p2} /K 0 >>");
                break;
            case RemovedPageBackReference.AcroFormWidget:
                catalogExtra = "/AcroForm << /Fields [13 0 R] >>";
                page2Annots = "12 0 R 13 0 R";
                extras.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) "
                    + $"/V ({FieldValue}) /Rect [72 400 300 420] /P {p2} >>");
                break;
            case RemovedPageBackReference.AcroFormFieldKids:
                catalogExtra = "/AcroForm 15 0 R";
                page2Annots = "12 0 R 14 0 R";
                if (widgetOnPageOneToo)
                    page1Annots = "/Annots [16 0 R]";
                extras.Add($"<< /FT /Tx /T (parent) /V ({FieldValue}) /Kids [14 0 R{(widgetOnPageOneToo ? " 16 0 R" : "")}] >>");
                extras.Add($"<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [72 400 300 420] /P {p2} >>");
                extras.Add("<< /Fields [13 0 R] /CO [13 0 R] >>");
                if (widgetOnPageOneToo)
                    extras.Add("<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [72 400 300 420] /P 3 0 R >>");
                break;
        }

        static string Content(string text, string extra = "") =>
            $"BT /F1 12 Tf 72 700 Td ({text}) Tj ET{extra}";
        static string Stream(string dict, string data) =>
            $"<< {dict} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

        var tagged = back == RemovedPageBackReference.StructureElement;
        var objs = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 6 0 R {page1Annots} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> /XObject << /Im1 11 0 R >> >> "
                + $"/Contents 7 0 R /Annots [{page2Annots}] {page2Extra} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R "
                + (pageThreeSharesFont ? "/F2 10 0 R " : "") + ">> >> /Contents 8 0 R >>",
            Stream("", Content(KeptOne)),
            Stream("", (tagged ? "/P << /MCID 0 >> BDC " : "")
                + Content(PageText, " q 100 0 0 10 72 500 cm /Im1 Do Q")
                + (tagged ? " EMC" : "")),
            Stream("", Content(KeptThree)),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /{FontName} /Encoding /WinAnsiEncoding >>",
            Stream($"/Type /XObject /Subtype /Image /Width {ImageBytes.Length} /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", ImageBytes),
            $"<< /Type /Annot /Subtype /Text /Rect [72 650 92 670] /Contents ({AnnotText}) /P {p2} >>",
        };
        objs.AddRange(extras);
        if (nestPagesTwoAndThree)
        {
            // Pages 2 and 3 under an intermediate /Pages node, the last object.
            var node = objs.Count + 1;
            objs[1] = $"<< /Type /Pages /Kids [3 0 R {node} 0 R] /Count 3 >>";
            objs[3] = objs[3].Replace("/Parent 2 0 R", $"/Parent {node} 0 R");
            objs[4] = objs[4].Replace("/Parent 2 0 R", $"/Parent {node} 0 R");
            objs.Add("<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 >>");
        }
        return Assemble(objs, version);
    }

    private static byte[] Assemble(List<string> objs, string version)
    {
        var sb = new StringBuilder($"%PDF-{version}\n%âãÏÓ\n");
        var offsets = new int[objs.Count];
        for (var i = 0; i < objs.Count; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
