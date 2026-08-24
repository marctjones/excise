using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1130 — AcroForm field names (<c>/T</c>) and tooltips (<c>/TU</c>) as
/// unscrubbed carriers. A passport form named a field "Your name as printed on
/// your most recent U..." and the redacted term survived there. The leaking
/// <c>/T</c> is on a NON-TERMINAL parent field (has <c>/Kids</c>), which
/// <c>GetAcroForm().Fields</c> does not enumerate — so the scrub walks the raw
/// <c>/AcroForm/Fields</c> tree recursively.
/// </summary>
public class FormFieldNameCarrierTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// An AcroForm whose PARENT field carries the secret in <c>/T</c> and
    /// <c>/TU</c>; the terminal child under <c>/Kids</c> has an innocent name.
    /// </summary>
    private static byte[] BuildPdf()
    {
        var sb = new StringBuilder();
        var offs = new System.Collections.Generic.List<int>();
        void Obj(string b) { offs.Add(sb.Length); sb.Append(b); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [7 0 R] >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 8 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        // 6: PARENT field, secret in /T and /TU, with a /Kids child.
        Obj($"6 0 obj\n<< /FT /Tx /T ({Secret} name field) /TU (Enter {Secret}'s name) /Kids [7 0 R] >>\nendobj\n");
        // 7: terminal widget child, innocent name.
        Obj("7 0 obj\n<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (line1) " +
            "/Rect [72 700 300 720] /P 3 0 R >>\nendobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Save(PdfDocument d)
    {
        using var ms = new MemoryStream();
        d.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Guard_TheSecretIsInAFieldNameToBeginWith()
    {
        SavedPdfLeakScanner.FindTerm(BuildPdf(), Secret).Should().NotBeEmpty(
            "the fixture must place the secret in /T /TU or this proves nothing");
    }

    [Fact]
    public void ParentFieldNameAndTooltip_AreScrubbed()
    {
        using var doc = PdfDocument.Open(BuildPdf());
        PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret });

        SavedPdfLeakScanner.FindTerm(Save(doc), Secret).Should().BeEmpty(
            "the term must be cut from /T and /TU on the parent field, which " +
            "GetAcroForm().Fields does not enumerate — the scrub walks /Kids (#1130)");
    }
}
