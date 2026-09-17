using System.Collections.Generic;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1547 phase 1 — classify XFA forms as none / static / dynamic.
/// </summary>
public class PdfXfaDetectionTests
{
    private const string Xdp =
        "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template/></xdp:xdp>";

    private const string Placeholder = "BT /F1 12 Tf 72 600 Td (Please wait...) Tj ET";

    /// <summary>
    /// A one-page PDF. <paramref name="acroForm"/> is the AcroForm dictionary
    /// body (null for none); <paramref name="catalogExtra"/> is appended to the
    /// catalog. Object 5 is an XDP stream, object 6 a text widget on the page.
    /// </summary>
    internal static byte[] BuildPdf(string? acroForm, string catalogExtra = "", bool pageHasWidget = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (acroForm != null ? " /AcroForm 4 0 R" : "") + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                (pageHasWidget ? " /Annots [6 0 R]" : "") +
                " /Contents 7 0 R >>",
            acroForm ?? "<< >>",
            $"<< /Length {Xdp.Length} >>\nstream\n{Xdp}\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /P 3 0 R >>",
            $"<< /Length {Placeholder.Length} >>\nstream\n{Placeholder}\nendstream",
        };

        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static PdfXfaFormKind Detect(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.DetectXfaForm();
    }

    [Fact]
    public void NoAcroForm_IsNone()
        => Detect(BuildPdf(acroForm: null)).Should().Be(PdfXfaFormKind.None);

    [Fact]
    public void AcroFormWithoutXfa_IsNone()
        => Detect(BuildPdf("<< /Fields [6 0 R] >>", pageHasWidget: true)).Should().Be(PdfXfaFormKind.None);

    [Fact]
    public void XfaWithUsableAcroFormFields_IsStatic()
        => Detect(BuildPdf("<< /Fields [6 0 R] /XFA 5 0 R >>", pageHasWidget: true))
            .Should().Be(PdfXfaFormKind.Static);

    [Fact]
    public void XfaPacketArray_WithFields_IsStatic()
        => Detect(BuildPdf("<< /Fields [6 0 R] /XFA [(template) 5 0 R] >>", pageHasWidget: true))
            .Should().Be(PdfXfaFormKind.Static);

    [Fact]
    public void NeedsRenderingTrue_IsDynamic_EvenWithFields()
        => Detect(BuildPdf("<< /Fields [6 0 R] /XFA 5 0 R >>", " /NeedsRendering true", pageHasWidget: true))
            .Should().Be(PdfXfaFormKind.Dynamic);

    [Fact]
    public void NeedsRenderingFalse_WithFields_IsStatic()
        => Detect(BuildPdf("<< /Fields [6 0 R] /XFA 5 0 R >>", " /NeedsRendering false", pageHasWidget: true))
            .Should().Be(PdfXfaFormKind.Static);

    [Fact]
    public void NeedsRenderingInsideAcroForm_IsNotTheSignal()
        // §7.7.2 Table 28: /NeedsRendering is a CATALOG key. The same key in
        // the AcroForm dictionary means nothing; classification falls back to
        // the field check.
        => Detect(BuildPdf("<< /Fields [6 0 R] /XFA 5 0 R /NeedsRendering true >>", pageHasWidget: true))
            .Should().Be(PdfXfaFormKind.Static);

    [Fact]
    public void XfaWithNoFields_IsDynamic()
        => Detect(BuildPdf("<< /Fields [] /XFA 5 0 R >>")).Should().Be(PdfXfaFormKind.Dynamic);

    [Fact]
    public void XfaWithMissingFieldsArray_IsDynamic()
        => Detect(BuildPdf("<< /XFA 5 0 R >>")).Should().Be(PdfXfaFormKind.Dynamic);

    [Fact]
    public void EmptyOrNonStreamXfa_IsNone()
    {
        Detect(BuildPdf("<< /Fields [] /XFA [] >>")).Should().Be(PdfXfaFormKind.None);
        Detect(BuildPdf("<< /Fields [] /XFA (not a stream) >>")).Should().Be(PdfXfaFormKind.None);
    }
}
