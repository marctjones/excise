using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1898: a static-XFA form's AcroForm shadow fields may not carry the /Ff
/// bit-12 multiline flag even when the template's own &lt;textEdit&gt; marks
/// the field <c>multiLine="1"</c>. <see cref="PdfField.IsMultiline"/> must OR
/// in that XFA signal instead of trusting only the AcroForm bit.
/// </summary>
public class PdfFieldXfaMultilineTests
{
    [Fact]
    public void IsMultiline_TrueFromXfaTemplate_WhenAcroFormFlagAbsent()
    {
        var pdf = XfaTestForms.BuildStaticMultilineFieldPdf();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var notes = form!.FindField("Notes");
        notes.Should().NotBeNull();
        (notes!.Flags & 0x1000).Should().Be(0, "the AcroForm widget carries no /Ff at all in this fixture");
        notes.IsMultiline.Should().BeTrue(
            "the XFA template's <textEdit multiLine=\"1\"> must be read when the AcroForm flag is silent");
    }

    [Fact]
    public void IsMultiline_FalseFromXfaTemplate_WhenTextEditNotMultiline()
    {
        var pdf = XfaTestForms.BuildStaticMultilineFieldPdf();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var form = doc.GetAcroForm();
        var shortField = form!.FindField("Short");
        shortField.Should().NotBeNull();
        shortField!.IsMultiline.Should().BeFalse(
            "a sibling field whose template textEdit has no multiLine attribute must stay single-line");
    }

    [Fact]
    public void IsMultiline_AcroFormFlagAlone_StillWorks_WhenNoXfaTemplate()
    {
        // A minimal AcroForm-only form (no /XFA at all): the pre-existing
        // /Ff bit-12 path must be untouched.
        var pdf = BuildPlainAcroFormMultilinePdf();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var field = doc.GetAcroForm()!.FindField("Notes");
        field.Should().NotBeNull();
        field!.IsMultiline.Should().BeTrue("the plain /Ff bit-12 flag must still be honored with no XFA present");
    }

    private static byte[] BuildPlainAcroFormMultilinePdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>\nstream\nendstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Notes) /Ff 4096 /Rect [72 660 300 720] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        foreach (var o in new[] { o1, o2, o3, o4, o5 })
            sb.AppendLine($"{o:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
