using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1042 — PDFBox as a SECOND, independent reference redactor, and the
/// one-directional comparison it enables. The assertion is deliberately
/// asymmetric: excise may remove LESS collateral than PDFBox, never more.
/// "excise must match PDFBox" would elect a renderer (#1015/#932).
/// </summary>
public class PdfBoxReferenceRedactorTests
{
    private const string Secret = "Farrar";

    // "Name: Louise Farrar" in one Tj — so whole-operator removal (PDFBox) takes
    // the whole line, while glyph-level removal (excise) keeps "Name: Louise".
    private static byte[] BuildFixture()
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 24 Tf 72 700 Td (Name: Louise {Secret}) Tj ET\n");
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); ms.Write(content); W("\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void PdfBox_RemovesTheTerm_AndFindsANonZeroHitCount()
    {
        Assert.SkipUnless(PdfBoxReferenceRedactor.IsAvailable, "java/pdfbox not available");

        var input = Path.Combine(Path.GetTempPath(), $"pb-in-{System.Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"pb-out-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, BuildFixture());
            var result = PdfBoxReferenceRedactor.Redact(input, Secret, output);

            result.Succeeded.Should().BeTrue($"PDFBox should run: {result.Failure}");
            result.HitsFound.Should().BeGreaterThan(0,
                "0 hits for a term an extractor plainly shows is a broken run, not a clean baseline (#1041)");

            MutoolTextExtractor.ExtractPage(output, 1).Should().NotContain(Secret,
                "the independent PDFBox reference must actually remove the term");
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public void ExciseRemovesLessCollateralThanPdfBox_NeverMore()
    {
        Assert.SkipUnless(PdfBoxReferenceRedactor.IsAvailable, "java/pdfbox not available");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not available");

        var pdf = BuildFixture();

        // PDFBox: whole-operator removal takes "Name: Louise Farrar" entirely.
        var pbIn = Path.Combine(Path.GetTempPath(), $"pb2-in-{System.Guid.NewGuid():N}.pdf");
        var pbOut = Path.Combine(Path.GetTempPath(), $"pb2-out-{System.Guid.NewGuid():N}.pdf");
        // excise: glyph-level removal keeps "Name: Louise".
        var exOut = Path.Combine(Path.GetTempPath(), $"ex-out-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(pbIn, pdf);
            PdfBoxReferenceRedactor.Redact(pbIn, Secret, pbOut).Succeeded.Should().BeTrue();

            using (var doc = PdfDocument.Open(pdf))
            {
                doc.RedactText(Secret);
                using var fs = File.Create(exOut);
                doc.Save(fs);
            }

            var before = MutoolTextExtractor.ExtractPage(pbIn, 1) ?? "";
            var afterPdfBox = MutoolTextExtractor.ExtractPage(pbOut, 1) ?? "";
            var afterExcise = MutoolTextExtractor.ExtractPage(exOut, 1) ?? "";

            // Both remove the secret.
            afterPdfBox.Should().NotContain(Secret);
            afterExcise.Should().NotContain(Secret);

            // The one-directional invariant: collateral (non-secret chars removed)
            // by excise must be <= collateral by PDFBox. Never assert equality.
            int collateralExcise = CountRemoved(before, afterExcise);
            int collateralPdfBox = CountRemoved(before, afterPdfBox);

            collateralExcise.Should().BeLessThanOrEqualTo(collateralPdfBox,
                "excise's glyph-level removal must destroy no MORE untargeted text than PDFBox's " +
                "whole-operator removal — excise keeps \"Name: Louise\", PDFBox does not");
        }
        finally { File.Delete(pbIn); File.Delete(pbOut); File.Delete(exOut); }
    }

    // Non-secret, non-space characters present before but gone after.
    private static int CountRemoved(string before, string after)
    {
        var secretless = before.Replace(Secret, "");
        var removed = 0;
        foreach (var c in secretless)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (!after.Contains(c)) removed++;
        }
        return removed;
    }
}
