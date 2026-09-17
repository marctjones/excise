using System.Text;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.TestSupport;

/// <summary>
/// Synthetic dynamic XFA forms (#1547). No corpus file carries datasets, so
/// binding, repeats and pagination are exercised with these. Each form is
/// written the way Designer writes one: a placeholder page, catalog
/// <c>/NeedsRendering true</c>, and an <c>/XFA</c> packet array whose pieces
/// concatenate into one XDP document.
/// </summary>
internal static class XfaTestForms
{
    public const string TemplateNamespace = "http://www.xfa.org/schema/xfa-template/3.3/";
    public const string Placeholder = "Please wait for the form to load";

    /// <summary>US Letter, one content area inset 0.25in (18pt) on every side.</summary>
    public const string LetterPageSet =
        "<pageSet><pageArea name=\"Page1\" id=\"Page1\">"
        + "<contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10.5in\"/>"
        + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/>"
        + "</pageArea></pageSet>";

    /// <summary>A template whose root subform has <paramref name="layout"/> and <paramref name="body"/>.</summary>
    public static string Template(string body, string layout = "tb", string pageSet = LetterPageSet)
        => $"<template xmlns=\"{TemplateNamespace}\"><subform name=\"form1\" layout=\"{layout}\">{pageSet}{body}</subform></template>";

    /// <summary>A datasets root <c>&lt;form1&gt;</c> holding <paramref name="body"/>.</summary>
    public static string Data(string body) => $"<form1>{body}</form1>";

    public static byte[] BuildPdf(string template, string? data = null, bool needsRendering = true, bool singleStream = false)
    {
        var document = PdfDocument.CreateNew();
        var page = document.Pages.AddBlank(612, 792);
        using (var graphics = page.GetGraphics())
            graphics.DrawString(Placeholder, PdfFont.Helvetica(12), PdfBrush.Black, 72, 700);

        if (needsRendering)
            document.Catalog["NeedsRendering"] = PdfBoolean.True;

        const string open = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">";
        const string close = "</xdp:xdp>";
        var datasets = data == null
            ? string.Empty
            : $"<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>{data}</xfa:data></xfa:datasets>";

        var acroForm = new PdfDictionary { ["Fields"] = new PdfArray() };
        if (singleStream)
        {
            acroForm["XFA"] = Stream(document, open + template + datasets + close);
        }
        else
        {
            var packets = new PdfArray();
            void Packet(string name, string xml)
            {
                packets.Add((PdfObject)new PdfString(name));
                packets.Add(Stream(document, xml));
            }

            Packet("preamble", open);
            Packet("template", template);
            if (data != null)
                Packet("datasets", datasets);
            Packet("postamble", close);
            acroForm["XFA"] = packets;
        }

        document.Catalog["AcroForm"] = document.AddIndirectObject(acroForm);
        return document.SaveToBytes();
    }

    /// <summary>A dynamic form whose <c>/XFA</c> is exactly <paramref name="xdp"/>, one stream.</summary>
    public static byte[] BuildPdfFromXdp(string xdp)
    {
        var document = PdfDocument.CreateNew();
        document.Pages.AddBlank(612, 792);
        document.Catalog["NeedsRendering"] = PdfBoolean.True;
        var acroForm = new PdfDictionary { ["Fields"] = new PdfArray(), ["XFA"] = Stream(document, xdp) };
        document.Catalog["AcroForm"] = document.AddIndirectObject(acroForm);
        return document.SaveToBytes();
    }

    /// <summary>
    /// A STATIC XFA form (#1574), the shape of the IRS forms: two AcroForm text
    /// widgets with appearance streams — FullName at [100 600 400 630] holding
    /// <paramref name="fullName"/>, City at [100 500 400 530] holding
    /// <paramref name="city"/> — a "Name:" label drawn in page content, no
    /// <c>/NeedsRendering</c>, and one <c>/XFA</c> stream whose datasets repeat
    /// both values plus <paramref name="datasetsOnly"/>, which no page shows.
    /// </summary>
    public static byte[] BuildStaticPdf(string fullName, string city, string datasetsOnly)
    {
        var xdp =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + Template(
                "<field name=\"FullName\" x=\"1.39in\" y=\"2.25in\" w=\"4.17in\" h=\"0.42in\"><ui><textEdit/></ui></field>"
                + "<field name=\"City\" x=\"1.39in\" y=\"3.64in\" w=\"4.17in\" h=\"0.42in\"><ui><textEdit/></ui></field>",
                layout: "position")
            + "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>"
            + Data($"<FullName>{fullName}</FullName><City>{city}</City><Notes>{datasetsOnly}</Notes>")
            + "</xfa:data></xfa:datasets></xdp:xdp>";

        static string Appearance(string value) => $"/Tx BMC BT /F1 12 Tf 2 8 Td ({value}) Tj ET EMC";
        var nameAp = Appearance(fullName);
        var cityAp = Appearance(city);
        const string content = "BT /F1 12 Tf 50 612 Td (Name:) Tj 0 -100 Td (City:) Tj ET";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 11 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 8 0 R] >>",
            RawStream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /V ({fullName}) /Rect [100 600 400 630] " +
                "/P 3 0 R /DA (/F1 12 Tf 0 g) /AP << /N 7 0 R >> >>",
            RawStream("/Type /XObject /Subtype /Form /BBox [0 0 300 30] /Resources << /Font << /F1 5 0 R >> >>", nameAp),
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (City) /V ({city}) /Rect [100 500 400 530] " +
                "/P 3 0 R /DA (/F1 12 Tf 0 g) /AP << /N 9 0 R >> >>",
            RawStream("/Type /XObject /Subtype /Form /BBox [0 0 300 30] /Resources << /Font << /F1 5 0 R >> >>", cityAp),
            RawStream("", xdp),
            "<< /Fields [6 0 R 8 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 5 0 R >> >> /XFA 10 0 R >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        var xref = Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string RawStream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.UTF8.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    private static PdfReference Stream(PdfDocument document, string xml)
        => document.AddIndirectObject(new PdfStream(Encoding.UTF8.GetBytes(xml)));

    /// <summary>
    /// A positioned form: a heading draw at (1in, 0.5in) and a text field
    /// "FullName" at (1in, 1in), 4in × 0.4in, with a 1in left caption "Name".
    /// All offsets are from the content area, which starts at (0.25in, 0.25in).
    /// </summary>
    public static string PositionedTemplate() => Template(
        "<draw name=\"Heading\" x=\"1in\" y=\"0.5in\" w=\"4in\" h=\"0.3in\">"
        + "<ui><textEdit/></ui><value><text>Applicant details</text></value>"
        + "<font typeface=\"Myriad Pro\" size=\"12pt\" weight=\"bold\"/></draw>"
        + "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\">"
        + "<ui><textEdit><border><edge/></border></textEdit></ui>"
        + "<font typeface=\"Myriad Pro\" size=\"10pt\"/>"
        + "<caption reserve=\"1in\"><value><text>Name</text></value></caption>"
        + "<value><text>template default</text></value></field>",
        layout: "position");
}
