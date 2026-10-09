using System.Text;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.TestSupport;

/// <summary>
/// Synthetic STATIC XFA forms for the datasets write-back (#2013): AcroForm
/// widgets whose full names are the XFA-SOM paths of template fields (XFA 3.3
/// "Field Names", p72-74), and a template that exercises each binding shape the
/// write-back maps: a transparent page subform (<c>bind match="none"</c>, the
/// IRS shape), a named subform bound to a data group, an unnamed subform
/// (<c>#subform[0]</c>), a two-item and a one-item check button, an exclusion
/// group in short format, a choice list with a <c>save="1"</c> column, a
/// <c>match="none"</c> field, a field with no data node, and a global field
/// whose data sits inside another group.
/// </summary>
internal static class XfaStaticFillFixtures
{
    public const string TemplateNamespace = "http://www.xfa.org/schema/xfa-template/3.3/";
    public const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";
    public const string FormNamespace = "http://www.xfa.org/schema/xfa-form/2.8/";

    /// <summary>The value the datasets hold for Name before any fill.</summary>
    public const string OriginalName = "ORIGINALNAMEVALUE";

    public const string NamePath = "form1[0].Page1[0].Name[0]";
    public const string CityPath = "form1[0].Page1[0].Address[0].City[0]";
    public const string CodePath = "form1[0].Page1[0].#subform[0].Code[0]";
    public const string AgreePath = "form1[0].Page1[0].Agree[0]";
    public const string SoloPath = "form1[0].Page1[0].Solo[0]";
    public const string SexPath = "form1[0].Page1[0].Sex[0]";
    public const string CountryPath = "form1[0].Page1[0].Country[0]";
    public const string UnboundPath = "form1[0].Page1[0].Unbound[0]";
    public const string MissingPath = "form1[0].Page1[0].Missing[0]";
    public const string TotalPath = "form1[0].Page1[0].Total[0]";
    public const string StrayPath = "form1[0].Page1[0].Stray[0]";

    public static string Template() =>
        $"<template xmlns=\"{TemplateNamespace}\"><subform name=\"form1\" layout=\"position\">"
        + "<pageSet><pageArea name=\"PageArea1\"><contentArea x=\"0in\" y=\"0in\" w=\"8.5in\" h=\"11in\"/>"
        + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/></pageArea></pageSet>"
        + "<subform name=\"Page1\" w=\"8.5in\" h=\"11in\"><bind match=\"none\"/>"
        + Field("Name", "<textEdit/>")
        + "<subform name=\"Address\" x=\"0in\" y=\"1in\" w=\"8in\" h=\"1in\">" + Field("City", "<textEdit/>") + "</subform>"
        + "<subform x=\"0in\" y=\"2in\" w=\"8in\" h=\"1in\">" + Field("Code", "<textEdit/>") + "</subform>"
        + Field("Agree", "<checkButton/>", "<items><text>Y</text><text>N</text></items>")
        + Field("Solo", "<checkButton/>", "<items><text>on</text></items>")
        + "<exclGroup name=\"Sex\">"
        + Field("M", "<checkButton shape=\"round\"/>", "<items><text>M</text></items>")
        + Field("F", "<checkButton shape=\"round\"/>", "<items><text>F</text></items>")
        + "</exclGroup>"
        + Field("Country", "<choiceList/>",
            "<items><text>Canada</text><text>France</text></items><items save=\"1\"><text>CA</text><text>FR</text></items>")
        + Field("Unbound", "<textEdit/>", "<bind match=\"none\"/>")
        + Field("Missing", "<textEdit/>")
        + Field("Total", "<textEdit/>", "<bind match=\"global\"/>")
        + "</subform></subform></template>";

    public static string DataXml() =>
        "<form1>"
        + $"<Name>{OriginalName}</Name>"
        + "<Address><City>OldCity</City><Total>1</Total></Address>"
        + "<Code/><Agree>N</Agree><Solo/><Sex/><Country/><Unbound>KEEPUNBOUND</Unbound>"
        + "</form1>";

    public static string Datasets() =>
        $"<xfa:datasets xmlns:xfa=\"{DataNamespace}\"><xfa:data>{DataXml()}</xfa:data></xfa:datasets>";

    private static string Field(string name, string ui, string extra = "")
        => $"<field name=\"{name}\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"0.3in\"><ui>{ui}</ui>{extra}</field>";

    /// <summary>Shape of the /XFA entry.</summary>
    public enum Shape
    {
        /// <summary>A packet array whose datasets packet is its own well-formed stream (the IRS shape).</summary>
        PacketArray,
        /// <summary>One stream holding the whole XDP.</summary>
        SingleStream,
        /// <summary>A packet array with no datasets packet.</summary>
        NoDatasets,
        /// <summary>A packet array whose datasets XML is split over two streams.</summary>
        SplitDatasets,
    }

    /// <summary>
    /// Build the form. <paramref name="formPacket"/> is the <c>form</c> packet
    /// body (null = none). <paramref name="needsRendering"/> makes it a
    /// DYNAMIC form with the same widgets, which the write-back must not touch.
    /// </summary>
    public static byte[] Build(
        Shape shape = Shape.PacketArray,
        string? formPacket = null,
        bool needsRendering = false,
        string version = "1.7")
    {
        var objects = new List<string>();
        int Add(string body)
        {
            objects.Add(body);
            return objects.Count;
        }

        var catalog = Add("");   // 1, filled in last
        var pages = Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        var page = Add("");      // 3
        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var zadb = Add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>");
        var empty = Add(RawStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", ""));
        var check = Add(RawStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << /Font << /ZaDb {zadb} 0 R >> >>",
            "q BT /ZaDb 12 Tf 4 5 Td (4) Tj ET Q"));

        var annots = new List<int>();
        var fields = new List<int>();
        double y = 740;

        int Text(string name, string? value = null)
        {
            var v = value == null ? "" : $" /V ({value})";
            var n = Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}){v} /Rect [100 {y - 20} 400 {y}] " +
                        $"/P {page} 0 R /DA (/Helv 10 Tf 0 g) /F 4 >>");
            y -= 30;
            annots.Add(n);
            fields.Add(n);
            return n;
        }

        int CheckBox(string name, string on)
        {
            var n = Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T ({name}) /V /Off /AS /Off " +
                        $"/Rect [100 {y - 20} 120 {y}] /P {page} 0 R /DA (/ZaDb 0 Tf 0 g) /F 4 " +
                        $"/AP << /N << /{on} {check} 0 R /Off {empty} 0 R >> >> >>");
            y -= 30;
            annots.Add(n);
            fields.Add(n);
            return n;
        }

        Text(XfaStaticFillFixtures.NamePath, OriginalName);
        Text(CityPath, "OldCity");
        Text(CodePath);
        CheckBox(AgreePath, "Y");
        CheckBox(SoloPath, "on");

        // Radio group: a parent field with two kid widgets (on states M and F).
        var radioParent = objects.Count + 1;
        var kidM = radioParent + 1;
        var kidF = radioParent + 2;
        Add($"<< /FT /Btn /Ff 49152 /T ({SexPath}) /V /Off /Kids [{kidM} 0 R {kidF} 0 R] >>");
        foreach (var (kid, on) in new[] { (kidM, "M"), (kidF, "F") })
        {
            Add($"<< /Type /Annot /Subtype /Widget /Parent {radioParent} 0 R /AS /Off " +
                $"/Rect [100 {y - 20} 120 {y}] /P {page} 0 R /F 4 " +
                $"/AP << /N << /{on} {check} 0 R /Off {empty} 0 R >> >> >>");
            y -= 30;
            annots.Add(kid);
        }
        fields.Add(radioParent);

        var country = Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T ({CountryPath}) " +
                          "/Opt [[(CA) (Canada)] [(FR) (France)]] " +
                          $"/Rect [100 {y - 20} 400 {y}] /P {page} 0 R /DA (/Helv 10 Tf 0 g) /F 4 >>");
        y -= 30;
        annots.Add(country);
        fields.Add(country);

        Text(UnboundPath);
        Text(MissingPath);
        Text(TotalPath, "1");
        Text(StrayPath);

        const string open = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">";
        const string close = "</xdp:xdp>";
        var form = formPacket ?? string.Empty;
        string xfa;
        switch (shape)
        {
            case Shape.SingleStream:
                xfa = $"{Add(RawStream("", open + Template() + Datasets() + form + close))} 0 R";
                break;
            default:
            {
                var parts = new List<(string Name, string Xml)> { ("preamble", open), ("template", Template()) };
                if (shape == Shape.PacketArray)
                    parts.Add(("datasets", Datasets()));
                else if (shape == Shape.SplitDatasets)
                {
                    var datasets = Datasets();
                    var cut = datasets.IndexOf("<Address>", StringComparison.Ordinal);
                    parts.Add(("datasets", datasets[..cut]));
                    parts.Add(("datasets2", datasets[cut..]));
                }
                if (formPacket != null)
                    parts.Add(("form", formPacket));
                parts.Add(("postamble", close));
                var sb = new StringBuilder("[");
                foreach (var (name, xml) in parts)
                    sb.Append('(').Append(name).Append(") ").Append(Add(RawStream("", xml))).Append(" 0 R ");
                xfa = sb.Append(']').ToString();
                break;
            }
        }

        var acroForm = Add($"<< /Fields [{string.Join(" ", fields.Select(f => $"{f} 0 R"))}] " +
                           $"/DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {font} 0 R /ZaDb {zadb} 0 R >> >> /XFA {xfa} >>");

        objects[catalog - 1] = $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm {acroForm} 0 R" +
                               (needsRendering ? " /NeedsRendering true" : "") + " >>";
        objects[page - 1] = $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
                            $"/Resources << /Font << /Helv {font} 0 R >> >> " +
                            $"/Annots [{string.Join(" ", annots.Select(a => $"{a} 0 R"))}] >>";

        var output = new StringBuilder($"%PDF-{version}\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(output.ToString()));
            output.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.UTF8.GetByteCount(output.ToString());
        output.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            output.Append(offset.ToString("D10")).Append(" 00000 n \n");
        output.Append("trailer\n<< /Size ").Append(objects.Count + 1)
              .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static string RawStream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.UTF8.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    // ------------------------------------------------------------ reading back

    /// <summary>The decoded bytes of every /XFA packet, by packet name (one entry "xdp" for a single stream).</summary>
    public static Dictionary<string, byte[]> Packets(PdfDocument document)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var acroForm = (PdfDictionary)document.Resolve(document.Catalog["AcroForm"]);
        switch (document.Resolve(acroForm["XFA"]))
        {
            case PdfStream single:
                result["xdp"] = single.DecodedData;
                break;
            case PdfArray array:
                for (int i = 0; i + 1 < array.Count; i += 2)
                    result[((PdfString)document.Resolve(array[i])).Value] = ((PdfStream)document.Resolve(array[i + 1])).DecodedData;
                break;
        }
        return result;
    }

    /// <summary>The record data group (first child of <c>xfa:data</c>) read from the datasets XML.</summary>
    public static XElement DataRoot(byte[] xml)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(xml), LoadOptions.PreserveWhitespace);
        XNamespace xfa = DataNamespace;
        return document.Descendants(xfa + "data").Single().Elements().First();
    }

    /// <summary>The record data group of a document, from whichever /XFA shape it has.</summary>
    public static XElement DataRoot(PdfDocument document)
    {
        var packets = Packets(document);
        return DataRoot(packets.TryGetValue("datasets", out var datasets) ? datasets : packets["xdp"]);
    }
}
