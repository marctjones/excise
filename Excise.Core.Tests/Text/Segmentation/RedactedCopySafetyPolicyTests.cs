using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public sealed class RedactedCopySafetyPolicyTests
{
    [Fact]
    public void Evaluate_DefaultSafeSharePolicy_ScrubsMetadataAndAttachments()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataAndEmbeddedFile(
            title: "Private title",
            embeddedFileName: "source.xml",
            embeddedContent: "<private/>"));

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForAreas(
                Array.Empty<RedactedCopySafetyArea>()));

        report.MetadataScrubbed.Should().BeTrue();
        report.InfoFieldsScrubbed.Should().Be(1);
        report.AttachmentsScrubbed.Should().BeTrue();
        report.EmbeddedFileCountBefore.Should().Be(1);
        report.FailedStages.Should().BeEmpty();
        document.Title.Should().BeNull();
        document.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_AuditOnlyTermPolicy_PreservesUnrelatedDocumentData()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataAndEmbeddedFile(
            title: "Public title",
            embeddedFileName: "public.xml",
            embeddedContent: "<public/>"));
        var auditOnly = new RedactedCopySafetyOptions
        {
            ScrubMetadata = false,
            ScrubAttachments = false,
            ScrubRequestedTerms = false,
            RunCarrierAudit = true,
            VerifyRequestedTerms = false,
            RunHiddenTextAudit = false,
            RunRasterRedactionAudit = false,
        };

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForTerms(new[] { "Ng" }, auditOnly));

        report.MetadataScrubbed.Should().BeFalse();
        report.AttachmentsScrubbed.Should().BeFalse();
        report.ContentVerificationStatus.Should().Be(
            RedactedContentVerificationStatus.NotChecked);
        report.Warnings.Should().ContainSingle(warning =>
            warning.Contains("shorter than 3 characters", StringComparison.Ordinal));
        document.Title.Should().Be("Public title");
        document.GetEmbeddedFiles().Should().ContainSingle();
    }

    [Fact]
    public void Evaluate_AttachmentOnlyPolicy_DoesNotImplicitlyScrubMetadata()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataAndEmbeddedFile(
            title: "Public title",
            embeddedFileName: "private-source.xml",
            embeddedContent: "<private/>"));
        var attachmentOnly = RedactedCopySafetyOptions.Default with
        {
            ScrubMetadata = false,
            ScrubAttachments = true,
            ScrubRequestedTerms = false,
            RunCarrierAudit = false,
            VerifyRequestedTerms = false,
            RunHiddenTextAudit = false,
            RunRasterRedactionAudit = false,
        };

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForTerms(Array.Empty<string>(), attachmentOnly));

        report.MetadataScrubbed.Should().BeFalse();
        report.AttachmentsScrubbed.Should().BeTrue();
        document.Title.Should().Be("Public title");
        document.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_PartialRasterAuditFailure_IsTypedAndFailsClosedToWarning()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataAndEmbeddedFile(
            title: "Public title",
            embeddedFileName: "public.xml",
            embeddedContent: "<public/>"));
        var mismatchedPageArea = PdfPageRect.FromContentPoints(
            2,
            new PdfRectangle(0, 0, 10, 10));
        var rasterOnly = RedactedCopySafetyOptions.Default with
        {
            ScrubMetadata = false,
            ScrubAttachments = false,
            ScrubRequestedTerms = false,
            RunCarrierAudit = false,
            VerifyRequestedTerms = false,
            RunHiddenTextAudit = false,
            RunRasterRedactionAudit = true,
        };

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForAreas(
                new[] { new RedactedCopySafetyArea(1, mismatchedPageArea) },
                options: rasterOnly));

        report.RasterRedactionAuditStatus.Should().Be(
            RedactedContentVerificationStatus.Warning);
        report.FailedStages.Should().ContainSingle()
            .Which.Should().Be(RedactedCopySafetyFailureStage.RasterRedactionAudit);
        report.HasWarnings.Should().BeTrue();
        report.Warnings.Should().ContainSingle(warning =>
            warning.Contains("could not be completed", StringComparison.Ordinal));
    }

    private static byte[] BuildPdfWithMetadataAndEmbeddedFile(
        string title,
        string embeddedFileName,
        string embeddedContent)
    {
        var builder = new StringBuilder();
        var offsets = new long[9];
        void Mark(int number) => offsets[number] = builder.Length;

        builder.Append("%PDF-1.7\n");
        Mark(1);
        builder.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");
        Mark(2);
        builder.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        builder.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");
        Mark(4);
        builder.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");
        Mark(5);
        builder.Append($"5 0 obj <</Names[({embeddedFileName}) 6 0 R]>> endobj\n");
        Mark(6);
        builder.Append($"6 0 obj <</Type/Filespec/F({embeddedFileName})/EF<</F 7 0 R>>>> endobj\n");
        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(embeddedContent);
        builder.Append("7 0 obj <</Type/EmbeddedFile/Length ")
            .Append(fileBytes.Length)
            .Append(">>\nstream\n")
            .Append(embeddedContent)
            .Append("\nendstream endobj\n");
        Mark(8);
        builder.Append($"8 0 obj <</Title({title})>> endobj\n");

        var xref = builder.Length;
        builder.Append("xref\n0 9\n0000000000 65535 f \n");
        for (var i = 1; i <= 8; i++)
            builder.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer <</Size 9/Root 1 0 R/Info 8 0 R>>\nstartxref\n")
            .Append(xref)
            .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
