using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using System;
using System.IO;
using System.Text;
using System.Linq;
using Xunit;

namespace Excise.App.Tests.Unit;

public class RedactedCopySafetyPolicyTests : IDisposable
{
    private readonly RedactedCopyDialogFormatter _formatter = new();
    private readonly RedactionService _redactionService =
        new(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance);
    private readonly string _tempDir;

    public RedactedCopySafetyPolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"excise-redacted-copy-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// #916/#905 — the audit must reach the DIALOG, not just the report object.
    ///
    /// The decided policy is "surface it, don't guess". A warning computed
    /// correctly and never shown is the same outcome for the user as not
    /// computing it, so this asserts the rendered dialog text.
    /// </summary>
    [Fact]
    public void PrepareRedactedCopy_WithBookmarksAndOffBoxAnnotations_SaysTheyWereNotExamined()
    {
        var inputPath = Path.Combine(_tempDir, "carriers.pdf");
        WriteBookmarkedFixture(inputPath);

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = document.GetPage(1);
        _redactionService.RedactArea(
            page, PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 675, 560, 750)));

        var report = PrepareRedactedCopy(document, Array.Empty<PendingRedaction>());
        var dialog = _formatter.Format(Path.Combine(_tempDir, "out.pdf"), report);

        report.HasWarnings.Should().BeTrue(
            "an area redaction cannot examine bookmark titles or annotations away from the box");
        dialog.Should().Contain("bookmark title",
            "the user must be told which carriers were left unexamined — this is the whole " +
            "point of choosing 'surface it' over silently stripping or silently skipping");
        dialog.Should().Contain("not examined");
        dialog.Should().NotContain("may contain",
            "excise has no evidence a surviving bookmark relates to the redacted content, and " +
            "overstating trains people to dismiss the warning");
    }

    /// <summary>
    /// A document with nothing unexaminable must produce no carrier warning. A
    /// warning that always fires is one people stop reading.
    /// </summary>
    [Fact]
    public void PrepareRedactedCopy_WithNoBookmarksOrAnnotations_AddsNoCarrierWarning()
    {
        var inputPath = Path.Combine(_tempDir, "plain.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PLAIN CONTENT");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = document.GetPage(1);
        _redactionService.RedactArea(
            page, PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, page.Width, page.Height)));

        var dialog = _formatter.Format(
            Path.Combine(_tempDir, "out.pdf"),
            PrepareRedactedCopy(document, Array.Empty<PendingRedaction>()));

        dialog.Should().NotContain("not examined",
            "nothing was left unexamined, so nothing should be reported");
    }

    [Fact]
    public void PrepareRedactedCopy_WithCapturedText_SurgicallyScrubsRemoteCarriers()
    {
        var inputPath = Path.Combine(_tempDir, "captured-carriers.pdf");
        WriteBookmarkedFixture(inputPath);

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        SetBookmarkAndAnnotationText(document, "CARRIERSECRET bookmark", "CARRIERSECRET comment");
        AddXfa(document,
            "<template><text>CARRIERSECRET form value</text><text>public XFA value</text></template>");

        var area = new PdfRectangle(40, 675, 560, 750);
        _redactionService.RedactArea(
            document.GetPage(1), PdfPageRect.FromContentPoints(1, area));
        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, area),
                PreviewText = "CARRIERSECRET",
            },
        };

        var report = PrepareRedactedCopy(document, pending);
        var output = Path.Combine(_tempDir, "captured-carriers-out.pdf");
        document.Save(output);
        var combined = Encoding.Latin1.GetString(File.ReadAllBytes(output)) +
                       Encoding.BigEndianUnicode.GetString(File.ReadAllBytes(output));

        combined.Should().NotContain("CARRIERSECRET",
            "captured selection text is an exact term, so matching bookmarks, remote comments, " +
            "and XFA values can be scrubbed without guessing from geometry");
        combined.Should().Contain("bookmark").And.Contain("comment").And.Contain("public XFA value",
            "carrier cleanup must remove only the captured term, not destroy unrelated content");
        report.Warnings.Should().NotContain(line => line.Contains("not examined"),
            "successfully checked carriers must not leave a standing false warning");
    }

    [Fact]
    public void PrepareRedactedCopy_WithMalformedMatchingXfa_SurfacesTheUnexaminedPacket()
    {
        var inputPath = Path.Combine(_tempDir, "malformed-xfa.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PUBLIC CARRIERSECRET");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        AddXfa(document, "<template><text>CARRIERSECRET</template>");
        var page = document.GetPage(1);
        var area = new PdfRectangle(0, 0, page.Width, page.Height);
        _redactionService.RedactArea(page, PdfPageRect.FromContentPoints(1, area));

        var report = PrepareRedactedCopy(document, new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, area),
                PreviewText = "CARRIERSECRET",
            },
        });
        var dialog = _formatter.Format("out.pdf", report);

        dialog.Should().Contain("XFA").And.Contain("not examined",
            "unsafe XML must never disappear behind an unqualified success dialog");
        dialog.Should().NotContain("CARRIERSECRET",
            "the warning must identify the carrier without echoing removed sensitive text");
    }

    private static void WriteBookmarkedFixture(string path)
    {
        const string page1 = "BT /F1 24 Tf 60 700 Td (CARRIERSECRET on page one) Tj ET";
        var objs = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [8 0 R] "
                + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {page1.Length} >>\nstream\n{page1}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "6 0 obj\n<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>\nendobj\n",
            "7 0 obj\n<< /Title (Chapter One) /Parent 6 0 R >>\nendobj\n",
            // Well away from the redaction box, so the positional scrubber never visits it.
            "8 0 obj\n<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] "
                + "/Contents (a reviewer comment) >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objs) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
    }

    private static void SetBookmarkAndAnnotationText(
        PdfDocument document,
        string bookmark,
        string annotation)
    {
        var outlines = document.Resolve(document.Catalog.GetOptional("Outlines")!)
            .Should().BeOfType<PdfDictionary>().Subject;
        var first = document.Resolve(outlines.GetOptional("First")!)
            .Should().BeOfType<PdfDictionary>().Subject;
        first["Title"] = new PdfString(bookmark);

        var annotations = document.Resolve(document.GetPage(1).Dictionary.GetOptional("Annots")!)
            .Should().BeOfType<PdfArray>().Subject;
        var annot = document.Resolve(annotations[0]).Should().BeOfType<PdfDictionary>().Subject;
        annot["Contents"] = new PdfString(annotation);
    }

    private static void AddXfa(PdfDocument document, string xml)
    {
        var acroForm = new PdfDictionary
        {
            ["XFA"] = document.AddIndirectObject(
                new PdfStream(new UTF8Encoding(false).GetBytes(xml))),
        };
        document.Catalog["AcroForm"] = document.AddIndirectObject(acroForm);
    }

    [Fact]
    public void PrepareRedactedCopy_AfterGlyphRedaction_VerifiesContentWithoutEchoingPreviewText()
    {
        var inputPath = Path.Combine(_tempDir, "redact.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PUBLIC SECRET");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = document.GetPage(1);
        _redactionService.RedactArea(
            page,
            PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, page.Width, page.Height)));

        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, page.Width, page.Height)),
                PreviewText = "SECRET"
            }
        };

        var report = PrepareRedactedCopy(document, pending);
        var dialog = _formatter.Format(Path.Combine(_tempDir, "redacted.pdf"), report);

        report.ContentVerificationStatus.Should().Be(RedactedContentVerificationStatus.Verified);
        report.RemainingTermCount.Should().Be(0);
        report.HiddenTextAuditStatus.Should().Be(RedactedContentVerificationStatus.Verified);
        page.Text.Should().NotContain("SECRET");
        dialog.Should().NotContain("SECRET");
    }

    [Fact]
    public void PrepareRedactedCopy_WhenPreviewTextStillExtracts_ReturnsWarningWithoutEchoingPreviewText()
    {
        var inputPath = Path.Combine(_tempDir, "warning.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PUBLIC SECRET");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 1, 1)),
                PreviewText = "SECRET"
            }
        };

        var report = PrepareRedactedCopy(document, pending);
        var dialog = _formatter.Format(Path.Combine(_tempDir, "redacted.pdf"), report);

        report.ContentVerificationStatus.Should().Be(RedactedContentVerificationStatus.Warning);
        report.RemainingTermCount.Should().Be(1);
        report.Warnings.Should().Contain(w => w.Contains("requested redaction term"));
        dialog.Should().NotContain("SECRET");
    }

    [Fact]
    public void DialogFormatter_PartialMetadataFailure_IsNotReportedAsNotRequested()
    {
        var report = new RedactedCopySafetyReport(
            RedactionAreaCount: 1,
            SkippedRedactionAreaCount: 0,
            RequestedTermCount: 1,
            CheckedTermCount: 0,
            RemainingTermCount: 0,
            SkippedShortTermCount: 0,
            ContentVerificationStatus: RedactedContentVerificationStatus.Warning,
            MetadataScrubbed: false,
            InfoFieldsScrubbed: 0,
            HadXmpMetadata: false,
            AttachmentsScrubbed: false,
            EmbeddedFileCountBefore: 0,
            HiddenTextAuditStatus: RedactedContentVerificationStatus.NotChecked,
            HiddenTextFindingCount: 0,
            RasterRedactionAuditStatus: RedactedContentVerificationStatus.NotChecked,
            RemainingRasterOverlapCount: 0,
            FailedStages: new[] { RedactedCopySafetyFailureStage.MetadataScrub },
            Warnings: new[] { "Metadata scrub could not be completed." });

        var dialog = _formatter.Format("out.pdf", report);

        report.HasWarnings.Should().BeTrue();
        dialog.Should().Contain("Metadata scrub: failed; see warnings");
        dialog.Should().NotContain("Metadata scrub: not requested",
            "a partial failure must never be presented as an intentional opt-out");
    }

    [Fact]
    public void PrepareRedactedCopy_ScrubsInfoXmpAndEmbeddedFilesBeforeSave()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataXmpAndEmbeddedFile(
            title: "SECRET title",
            xmpBody: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">SECRET</x:xmpmeta>",
            embeddedFileName: "secret.xml",
            embeddedContent: "<secret>SECRET</secret>"));

        document.Title.Should().Contain("SECRET");
        document.GetXmpMetadata().Should().NotBeNull();
        document.GetEmbeddedFiles().Should().ContainSingle();

        var report = PrepareRedactedCopy(document, Array.Empty<PendingRedaction>());
        var outputPath = Path.Combine(_tempDir, "scrubbed.pdf");
        document.Save(outputPath);

        report.MetadataScrubbed.Should().BeTrue();
        report.InfoFieldsScrubbed.Should().Be(1);
        report.HadXmpMetadata.Should().BeTrue();
        report.AttachmentsScrubbed.Should().BeTrue();
        report.EmbeddedFileCountBefore.Should().Be(1);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.Title.Should().BeNull();
        reopened.GetXmpMetadata().Should().BeNull();
        reopened.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void PrepareRedactedCopy_WhenRasterStillOverlapsRedactionArea_WarnsForManualReview()
    {
        using var document = PdfDocument.Open(BuildPdfWithImageOnlyXObject("SCANNEDIMAGESECRET"));
        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(110, 650, 150, 680)),
                PreviewText = "SECRET"
            }
        };

        var report = PrepareRedactedCopy(document, pending);
        var dialog = _formatter.Format(Path.Combine(_tempDir, "redacted.pdf"), report);

        report.RasterRedactionAuditStatus.Should().Be(RedactedContentVerificationStatus.Warning);
        report.RemainingRasterOverlapCount.Should().Be(1);
        report.Warnings.Should().Contain(w => w.Contains("raster image invocation"));
        dialog.Should().NotContain("SECRET");
    }

    [Fact]
    public void PrepareRedactedCopy_AfterImageOnlyAreaRedaction_WarnsUntilPixelCoverageIsIndependentlyAudited()
    {
        const string marker = "SCANNEDIMAGESECRET";
        var area = new PdfRectangle(110, 650, 150, 680);
        using var document = PdfDocument.Open(BuildPdfWithImageOnlyXObject(marker));

        document.GetPage(1).RedactArea(area);

        var report = PrepareRedactedCopy(document, new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, area),
                PreviewText = "SECRET"
            }
        });

        // #1195 preserves non-secret pixels by replacing the image with a
        // region-redacted clone. The current audit intentionally sees that
        // retained image invocation and warns: it does not yet independently
        // inspect the altered pixel region, so calling it verified would be a
        // false assurance.
        report.RasterRedactionAuditStatus.Should().Be(RedactedContentVerificationStatus.Warning);
        report.RemainingRasterOverlapCount.Should().Be(1);
        report.Warnings.Should().Contain(w => w.Contains("raster image invocation"));
        Encoding.Latin1.GetString(document.SaveToBytes()).Should().NotContain(marker);
    }

    void IDisposable.Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static RedactedCopySafetyReport PrepareRedactedCopy(
        PdfDocument document,
        IReadOnlyCollection<PendingRedaction> redactions,
        int skippedRedactionAreaCount = 0,
        RedactedCopySafetyOptions? options = null) =>
        RedactedCopySafetyPolicy.Evaluate(
            document,
            RedactedCopySafetyRequest.ForAreas(
                redactions
                    .Select(redaction => new RedactedCopySafetyArea(
                        redaction.PageNumber,
                        redaction.PageArea,
                        redaction.PreviewText))
                    .ToArray(),
                skippedRedactionAreaCount,
                options));

    private static byte[] BuildPdfWithMetadataXmpAndEmbeddedFile(
        string title,
        string xmpBody,
        string embeddedFileName,
        string embeddedContent)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R/Metadata 9 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append($"5 0 obj <</Names[({embeddedFileName}) 6 0 R]>> endobj\n");

        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({embeddedFileName})/EF<</F 7 0 R>>>> endobj\n");

        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(embeddedContent);
        sb.Append($"7 0 obj <</Type/EmbeddedFile/Length {fileBytes.Length}>>\nstream\n");
        sb.Append(embeddedContent);
        sb.Append("\nendstream endobj\n");

        Mark(8);
        sb.Append($"8 0 obj <</Title({title})>> endobj\n");

        Mark(9);
        var xmpBytes = Encoding.UTF8.GetBytes(xmpBody);
        sb.Append($"9 0 obj <</Type/Metadata/Subtype/XML/Length {xmpBytes.Length}>>\nstream\n");
        sb.Append(xmpBody);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        for (var i = 1; i <= 9; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 10/Root 1 0 R/Info 8 0 R>>\nstartxref\n")
            .Append(xrefPos)
            .Append("\n%%EOF\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithImageOnlyXObject(string imageMarker)
    {
        var contentBytes = Encoding.Latin1.GetBytes("q 160 0 0 80 100 640 cm /Im0 Do Q\n");
        var imageBytes = Encoding.Latin1.GetBytes(imageMarker);

        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[6];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        ms.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /XObject /Subtype /Image " +
              $"/Width {imageBytes.Length} /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 " +
              $"/Length {imageBytes.Length} >>\nstream\n");
        ms.Write(imageBytes, 0, imageBytes.Length);
        Write("\nendstream\nendobj\n");

        var xref = ms.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
