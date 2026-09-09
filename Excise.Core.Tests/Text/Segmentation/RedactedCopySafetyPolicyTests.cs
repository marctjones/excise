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

    // ── #1430: refuse a safe-redacted copy over unresolved /Redact marks ─────

    private const string MarkedSecret = "Wollstonecraft";

    [Fact]
    public void Evaluate_WithUnresolvedRedactAnnotations_RefusesToProduceASafeCopy()
    {
        using var document = PdfDocument.Open(BuildPdfWithRedactAnnotation(MarkedSecret));

        var refusal = Assert.Throws<UnresolvedRedactAnnotationsException>(() =>
            RedactedCopySafetyPolicy.Evaluate(
                document,
                RedactedCopySafetyRequest.ForAreas(Array.Empty<RedactedCopySafetyArea>())));

        refusal.AnnotationCount.Should().Be(1);
        refusal.Message.Should().Contain("has NOT been removed",
            "the refusal has to say WHY — a reviewer who reads 'failed' and not " +
            "'the marked content is still present' may just try another export path");
    }

    [Fact]
    public void Evaluate_WhenItRefuses_HasNotAlreadyModifiedTheDocument()
    {
        // The ordering guarantee, and the reason the check is the first thing
        // Evaluate does. Evaluate scrubs metadata and embedded files IN PLACE;
        // a refusal raised after that would leave the caller with a document
        // both "refused" and already stripped. The product policy says refuse
        // RATHER THAN silently applying or deleting, so nothing may have
        // happened yet.
        using var document = PdfDocument.Open(
            BuildPdfWithRedactAnnotation(MarkedSecret, title: "Private title"));

        Assert.Throws<UnresolvedRedactAnnotationsException>(() =>
            RedactedCopySafetyPolicy.Evaluate(
                document,
                RedactedCopySafetyRequest.ForAreas(Array.Empty<RedactedCopySafetyArea>())));

        document.Title.Should().Be("Private title",
            "a refused evaluation must not have scrubbed anything — refusing and " +
            "mutating at the same time is the worst of both outcomes");
    }

    [Fact]
    public void AnUnresolvedRedactMark_LeavesItsMarkedTextFullyIntact()
    {
        // The conservation half, and the point of refusing at all. This proves
        // the annotation genuinely was NOT applied — that the refusal is
        // protecting real, still-present content rather than firing a warning
        // about a mark that had already taken effect. If excise ever silently
        // applied incoming marks, this test goes red and the refusal above
        // becomes pointless rather than wrong.
        using var document = PdfDocument.Open(BuildPdfWithRedactAnnotation(MarkedSecret));

        document.GetPage(1).Text.Should().Contain(MarkedSecret,
            "a /Redact annotation is a PROPOSAL: until applied, the marked content " +
            "is still entirely present, which is exactly why a copy made from this " +
            "document must not be called safely redacted");

        document.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Redact,
                "and the mark itself must survive — deleting it would destroy the " +
                "reviewer's work just as surely as applying it would destroy content");
    }

    [Fact]
    public void Evaluate_WithoutRedactAnnotations_IsUnaffected()
    {
        // Anti-vacuity for the three above: the refusal must be caused by the
        // ANNOTATION, not by anything else in the fixture shape.
        using var document = PdfDocument.Open(BuildPdfWithMetadataAndEmbeddedFile(
            title: "Private title",
            embeddedFileName: "source.xml",
            embeddedContent: "<private/>"));

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForAreas(Array.Empty<RedactedCopySafetyArea>()));

        report.UnresolvedRedactAnnotationCount.Should().Be(0);
        report.FailedStages.Should().NotContain(
            RedactedCopySafetyFailureStage.UnresolvedRedactAnnotationInspection);
        report.MetadataScrubbed.Should().BeTrue(
            "an ordinary document must still go through the normal scrub path");
    }

    [Fact]
    public void Evaluate_WithTheRefusalDisabled_ReportsTheCountInsteadOfThrowing()
    {
        // The deliberate opt-out, for a caller that is NOT claiming its output
        // is safely redacted. It must still SAY how many marks it saw — an
        // opt-out that also hides the count would make the marks invisible.
        using var document = PdfDocument.Open(BuildPdfWithRedactAnnotation(MarkedSecret));

        var report = RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForAreas(
                Array.Empty<RedactedCopySafetyArea>(),
                options: new RedactedCopySafetyOptions
                {
                    RefuseOnUnresolvedRedactAnnotations = false,
                }));

        report.UnresolvedRedactAnnotationCount.Should().Be(1);
    }

    /// <summary>
    /// One page carrying <paramref name="markedText"/> and a single
    /// <c>/Redact</c> annotation (§12.5.6.23) over it — a reviewer's unapplied
    /// proposal, with the marked content still fully present.
    /// </summary>
    private static byte[] BuildPdfWithRedactAnnotation(
        string markedText,
        string title = "Draft under review")
    {
        var content = $"BT /F1 12 Tf 72 700 Td ({markedText}) Tj ET";
        var builder = new StringBuilder();
        var offsets = new long[8];
        void Mark(int number) => offsets[number] = builder.Length;

        builder.Append("%PDF-1.7\n");
        Mark(1);
        builder.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2);
        builder.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        builder.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]")
            .Append("/Resources<</Font<</F1 6 0 R>>>>/Contents 4 0 R/Annots[5 0 R]>> endobj\n");
        Mark(4);
        builder.Append("4 0 obj <</Length ").Append(content.Length).Append(">>\nstream\n")
            .Append(content).Append("\nendstream endobj\n");
        Mark(5);
        builder.Append("5 0 obj <</Type/Annot/Subtype/Redact/Rect[70 695 200 715]")
            .Append("/F 4/QuadPoints[70 715 200 715 70 695 200 695]>> endobj\n");
        Mark(6);
        builder.Append("6 0 obj <</Type/Font/Subtype/Type1/BaseFont/Helvetica>> endobj\n");
        Mark(7);
        builder.Append($"7 0 obj <</Title({title})>> endobj\n");

        var xref = builder.Length;
        builder.Append("xref\n0 8\n0000000000 65535 f \n");
        for (var i = 1; i <= 7; i++)
            builder.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer <</Size 8/Root 1 0 R/Info 7 0 R>>\nstartxref\n")
            .Append(xref)
            .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
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
