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
