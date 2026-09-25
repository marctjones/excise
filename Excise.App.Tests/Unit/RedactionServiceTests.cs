using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.Unit;

public class RedactionServiceTests : IDisposable
{
    private readonly RedactionService _service;
    private readonly string _tempDir;

    public RedactionServiceTests()
    {
        _service = new RedactionService(NullLogger<RedactionService>.Instance, new NullLoggerFactory());
        _tempDir = Path.Combine(Path.GetTempPath(), $"excise-redaction-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private string CreateTestFile(string filename, Action<string> creator)
    {
        var path = Path.Combine(_tempDir, filename);
        creator(path);
        return path;
    }

    void IDisposable.Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }


    #region RedactArea Tests

    // Geometry these tests aim at: TestPdfGenerator.CreateMultiPagePdf draws
    // "Page N Content" at PDF y = height-100 and "Secret on Page N" at
    // height-200. The areas take RENDERED-page coordinates (top-left origin);
    // at 72 DPI the SECOND line sits around visual y 165-225 and the first is
    // well above it.
    private const string TargetLine = "Secret on Page 1";
    private const string SurvivorLine = "Page 1 Content";

    private static PdfPageRect VisualArea(PdfPage page, double x, double y, double width, double height) =>
        PdfPageRect.ViewerDips(page.PageNumber, x, y, width, height, renderDpi: 72);

    private static PdfPageRect TargetLineArea(PdfPage page) => VisualArea(page, 40, 165, 460, 60);

    private string CreateTwoLinePdf(string name = "redact.pdf") =>
        CreateTestFile(name, path => TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1));

    /// <summary>
    /// #1769: this asserted only "does not throw" — true of a build that
    /// removes nothing at all. A normal area redaction must remove the glyphs
    /// the rectangle covers, proven on the SAVED BYTES (every carrier,
    /// compressed streams included) rather than on excise's own extractor, and
    /// must leave everything else alone.
    /// </summary>
    [Fact]
    public void RedactArea_WithValidArea_RemovesTheCoveredLineFromTheSavedFile()
    {
        var filePath = CreateTwoLinePdf();
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, TargetLine).Should().NotBeEmpty(
            "input-side control: the carrier scan must find the line BEFORE the redaction, " +
            "or its absence afterwards proves nothing");

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);
        _service.RedactArea(page, TargetLineArea(page), RedactionOptions.Default);

        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, TargetLine).Should().BeEmpty(
            "the covered line must be gone from every carrier of the saved file");
        SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain(SurvivorLine,
            "content outside the rectangle must survive — the #942 collateral property");
    }

    [Fact]
    public void RedactArea_WithLargeArea_CoversPageContent()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Secret Content"));
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Secret Content").Should().NotBeEmpty(
            "input-side control: the scan must find the text before the redaction");

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);

        // Large area covering page
        var area = VisualArea(page, 0, 0, page.Width, page.Height);

        // Act
        _service.RedactArea(page, area, RedactionOptions.Default);

        // Assert — on the SAVED BYTES. This previously asserted that a list of
        // harvested words was non-empty (true of a build that removes nothing,
        // #897 deleted that list), and then that page.Text no longer held the
        // string — excise vouching for excise (#1769).
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "Secret Content").Should().BeEmpty(
            "a full-page redaction must remove the glyphs from every carrier of the saved file");
    }

    /// <summary>
    /// #897 at the GUI layer: an area redaction through this service must also
    /// clear the document-level carriers that have no position.
    ///
    /// The engine does the strip; this pins that the GUI path actually gets it,
    /// because the GUI path is the one a person uses on a real document. It
    /// replaces a test that asserted a now-deleted list of harvested words was
    /// non-empty — which said nothing about whether anything was protected.
    /// </summary>
    [Fact]
    public void RedactArea_StripsDocumentLevelCarriers()
    {
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "SecretWord"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("SecretWord in the title");
        var page = doc.GetPage(1);

        _service.RedactArea(page, VisualArea(page, 0, 0, page.Width, page.Height), RedactionOptions.Default);

        var outputPath = Path.Combine(_tempDir, "carriers.pdf");
        doc.Save(outputPath);
        var saved = SavedPdfLeakScanner.AllCarriersText(File.ReadAllBytes(outputPath));

        saved.Should().NotContain("SecretWord in the title",
            "drawing a box over a name and saving must not leave the name in the document " +
            "title — the carrier a reader shows before the page is even opened (#897)");
    }

    /// <summary>
    /// A small rectangle over blank page must remove NOTHING. #1769: this
    /// asserted only "does not throw"; conservation is the property that
    /// matters here, and it is the one #942 broke — a redaction that destroyed
    /// 5-36% of a document per term also did not throw.
    /// </summary>
    [Fact]
    public void RedactArea_WithSmallAreaOverBlankSpace_RemovesNothing()
    {
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);
        _service.RedactArea(page, VisualArea(page, 480, 600, 10, 10), RedactionOptions.Default);

        var saved = SavedPdfLeakScanner.AllCarriersText(doc.SaveToBytes());
        saved.Should().Contain(TargetLine, "nothing was inside the rectangle");
        saved.Should().Contain(SurvivorLine, "nothing was inside the rectangle");
    }

    [Fact]
    public void RedactArea_WithOutOfBoundsArea_LogsWarningButDoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Text"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Area way outside page bounds
        var area = VisualArea(page, 10000, 10000, 100, 100);

        // Act & Assert
        var action = () => _service.RedactArea(page, area, RedactionOptions.Default);
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactArea_MultipleTimes_RemovesTextFromEachRegion()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateTextOnlyPdf(path, new[] { "Line One", "Line Two" }));
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Line One").Should().NotBeEmpty(
            "input-side control: both lines must be findable before the redaction");
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Line Two").Should().NotBeEmpty();

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);

        // Act - Redact twice, covering both lines between them
        _service.RedactArea(page, VisualArea(page, 0, 0, 300, 100), RedactionOptions.Default);
        _service.RedactArea(page, VisualArea(page, 0, 100, 300, 100), RedactionOptions.Default);

        // Assert - both regions lost their glyphs, read from the SAVED BYTES.
        // The original version compared two counts of a harvested-word list
        // with >=, which a build that redacted nothing also satisfies; its
        // replacement read page.Text, which is excise grading excise (#1769).
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "Line One").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "Line Two").Should().BeEmpty();
    }

    #endregion

    #region RedactText Tests

    [Fact]
    public void RedactText_WithValidInput_CreatesOutputFile()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "RedactMe"));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText(inputPath, outputPath, "RedactMe", RedactionOptions.Default);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
        result.Success.Should().BeTrue();
        // #1769: "a file was produced and the service says it worked" is true
        // of a copy operation. The file must also not hold the term.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "RedactMe").Should().BeEmpty(
            "the output the service reports as a success must not still carry the term");
    }

    [Fact]
    public void RedactText_WithMatchingTerm_RemovesItFromTheOutput()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "SecretTerm"));
        // #1769: the missing input-side control. Without it "not found" is
        // equally consistent with a scan that cannot see this file's text.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), "SecretTerm").Should().NotBeEmpty(
            "input-side control: the term must be findable in the file the user handed us");

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        _service.RedactText(inputPath, outputPath, "SecretTerm", RedactionOptions.Default);

        // Assert — on the saved bytes, not on a list of what the service says it
        // did. A service can record a term it failed to remove.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "SecretTerm").Should().BeEmpty();
    }

    /// <summary>
    /// A term that is not in the document must leave the document ALONE.
    /// #1769: this asserted only <c>Success</c>, which a build that wipes every
    /// page also reports.
    /// </summary>
    [Fact]
    public void RedactText_WithNonMatchingTerm_SucceedsAndChangesNothing()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "SomeText"));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText(inputPath, outputPath, "NonExistentTerm", RedactionOptions.Default);

        // Assert
        result.Success.Should().BeTrue();
        SavedPdfLeakScanner.AllCarriersText(File.ReadAllBytes(outputPath))
            .Should().Contain("SomeText",
                "redacting a term the document does not contain must not remove the text it does");
    }

    [Fact]
    public void RedactText_OutputFileIsValidPdf()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "TestContent"));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        _service.RedactText(inputPath, outputPath, "TestContent", RedactionOptions.Default);

        // Assert - Verify we can open the output
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        doc.PageCount.Should().Be(1);
    }

    /// <summary>
    /// #1769: this ran both modes and asserted only that each reported
    /// <c>Success</c> — which is what a build that ignores the flag entirely
    /// reports too. The flag is now asserted two-sided on the SAME wrong-case
    /// term: case-sensitive must MISS it, case-insensitive must HIT it.
    /// </summary>
    [Fact]
    public void RedactText_WithCaseSensitiveFlag_RespectsCaseSensitivity()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "TestContentToken"));
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), "TestContentToken")
            .Should().NotBeEmpty("input-side control: the term must be findable before either run");

        var sensitivePath = Path.Combine(_tempDir, "output-sensitive.pdf");
        var insensitivePath = Path.Combine(_tempDir, "output-insensitive.pdf");

        // Act — the SAME wrong-case needle through both modes.
        var resultSensitive = _service.RedactText(
            inputPath, sensitivePath, "testcontenttoken", RedactionOptions.Default with { CaseSensitive = true });
        var resultInsensitive = _service.RedactText(
            inputPath, insensitivePath, "testcontenttoken", RedactionOptions.Default);

        // Assert
        resultSensitive.Success.Should().BeTrue();
        resultInsensitive.Success.Should().BeTrue();

        SavedPdfLeakScanner.AllCarriersText(File.ReadAllBytes(sensitivePath))
            .Should().Contain("TestContentToken",
                "a case-SENSITIVE search for the lower-case spelling must not match the document's text");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(insensitivePath), "TestContentToken")
            .Should().BeEmpty(
                "a case-INSENSITIVE search for the same needle must match and remove it");
    }

    [Fact]
    public void RedactText_WithInvalidInputPath_ReturnsFailed()
    {
        // Arrange
        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText("/nonexistent/path.pdf", outputPath, "Term", RedactionOptions.Default);

        // Assert
        result.Success.Should().BeFalse();
    }

    /// <summary>#650: every result carries a Warnings collection — never null, even when empty.</summary>
    [Fact]
    public void RedactText_ResultAlwaysHasNonNullWarnings()
    {
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "RedactMe"));
        var outputPath = Path.Combine(_tempDir, "output.pdf");

        var result = _service.RedactText(inputPath, outputPath, "RedactMe", RedactionOptions.Default);

        result.Success.Should().BeTrue();
        result.Warnings.Should().NotBeNull();
    }

    /// <summary>#650: allowLowConfidence must not change behavior on a healthy document — it only matters when the confidence check refuses.</summary>
    [Fact]
    public void RedactText_WithAllowLowConfidenceTrue_HealthyDocument_StillSucceeds()
    {
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "RedactMe"));
        var outputPath = Path.Combine(_tempDir, "output.pdf");

        var result = _service.RedactText(inputPath, outputPath, "RedactMe", RedactionOptions.Default, allowLowConfidence: true);

        result.Success.Should().BeTrue();
        result.RedactionCount.Should().BeGreaterThan(0);
        // A count the service reports is not removal; the saved bytes are.
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "RedactMe").Should().BeEmpty();
    }

    /// <summary>
    /// #1769: this asserted only <c>Success</c> and a page count — nothing
    /// about removal, on any page. The term is now "Secret" rather than "Page":
    /// a carrier-agnostic scan for "Page" would match the <c>/Page</c> and
    /// <c>/Pages</c> dictionary keys of every PDF ever written and could never
    /// come back empty.
    /// </summary>
    [Fact]
    public void RedactText_MultiplePages_RedactsAllPages()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), "Secret").Should().NotBeEmpty(
            "input-side control: the seeded term must be findable before the redaction");

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText(inputPath, outputPath, "Secret", RedactionOptions.Default);

        // Assert
        result.Success.Should().BeTrue();

        var savedBytes = File.ReadAllBytes(outputPath);
        SavedPdfLeakScanner.FindTerm(savedBytes, "Secret").Should().BeEmpty(
            "every page's copy of the term must be gone, in every carrier");

        var savedCarriers = SavedPdfLeakScanner.AllCarriersText(savedBytes);
        for (var p = 1; p <= 3; p++)
            savedCarriers.Should().Contain($"Page {p} Content",
                $"page {p}'s unredacted line must survive");

        // Verify output still has 3 pages
        using var doc = PdfDocument.Open(savedBytes);
        doc.PageCount.Should().Be(3);
    }

    [Fact]
    public void RedactTextFlattenOcr_WritesImageOnlyOutputWithoutTheTargetTextLayer()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        var inputPath = CreateTestFile("flatten-input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "GUIFLATTENSECRET"));
        var outputPath = Path.Combine(_tempDir, "flatten-output.pdf");

        var result = _service.RedactTextFlattenOcr(inputPath, outputPath, "GUIFLATTENSECRET");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.RedactionCount.Should().BeGreaterThan(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "GUIFLATTENSECRET")
            .Should().BeEmpty("the image-only output must not retain the target in any saved text carrier");
        using var output = PdfDocument.Open(File.ReadAllBytes(outputPath));
        output.GetPage(1).Text.Should().BeEmpty("the GUI image-only path must not recreate an OCR text layer");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("intentionally removed selectable text");
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullRedactionWorkflow_LoadRedactSave_Works()
    {
        // Arrange
        var inputPath = CreateTestFile("workflow.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), TargetLine).Should().NotBeEmpty(
            "input-side control: the line must be findable before the redaction");

        var outputPath = Path.Combine(_tempDir, "redacted.pdf");

        // Act
        using var doc = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = doc.GetPage(1);

        _service.RedactArea(page, TargetLineArea(page), RedactionOptions.Default);
        doc.Save(outputPath);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
        var savedBytes = File.ReadAllBytes(outputPath);
        // #1769: "a file exists and still has 2 pages" is true of a copy.
        SavedPdfLeakScanner.FindTerm(savedBytes, TargetLine).Should().BeEmpty(
            "the load → redact → save round trip must persist the removal to disk");
        SavedPdfLeakScanner.AllCarriersText(savedBytes).Should().Contain("Secret on Page 2",
            "page 2 was never redacted and must survive");
        using var savedDoc = PdfDocument.Open(savedBytes);
        savedDoc.PageCount.Should().Be(2);
    }

    [Fact]
    public void RedactMultiplePagesInSingleDocument_Works()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        // Act — the same rectangle on every page, over each page's own
        // "Secret on Page N" line.
        for (int i = 1; i <= 3; i++)
        {
            var page = doc.GetPage(i);
            _service.RedactArea(page, TargetLineArea(page), RedactionOptions.Default);
        }

        // Assert — #1769: this asserted only that the page count was unchanged,
        // which no redaction defect can falsify.
        var savedBytes = doc.SaveToBytes();
        var savedCarriers = SavedPdfLeakScanner.AllCarriersText(savedBytes);
        for (int i = 1; i <= 3; i++)
        {
            SavedPdfLeakScanner.FindTerm(savedBytes, $"Secret on Page {i}").Should().BeEmpty(
                $"page {i}'s covered line must be removed");
            savedCarriers.Should().Contain($"Page {i} Content",
                $"page {i}'s uncovered line must survive");
        }

        doc.PageCount.Should().Be(3);
    }

    [Fact]
    public void CombinedRedaction_TextAndAreaRedaction_Works()
    {
        // Arrange
        var inputPath = CreateTestFile("combined.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), "Secret").Should().NotBeEmpty(
            "input-side control: the term must be findable before the first pass");

        var intermediatePath = Path.Combine(_tempDir, "intermediate.pdf");
        var finalPath = Path.Combine(_tempDir, "final.pdf");

        // Act - First do text redaction
        var textResult = _service.RedactText(inputPath, intermediatePath, "Secret", RedactionOptions.Default);
        textResult.Success.Should().BeTrue();

        // Then do area redaction on the result
        using var doc = PdfDocument.Open(File.ReadAllBytes(intermediatePath));
        var page = doc.GetPage(1);
        _service.RedactArea(page, VisualArea(page, 0, 60, 612, 80), RedactionOptions.Default);
        doc.Save(finalPath);

        // Assert — #1769: the page count was the only thing checked, so a build
        // that removed nothing in either pass passed.
        File.Exists(finalPath).Should().BeTrue();
        var finalBytes = File.ReadAllBytes(finalPath);
        SavedPdfLeakScanner.FindTerm(finalBytes, "Secret").Should().BeEmpty(
            "the text pass must have removed the term and the area pass must not have brought it back");
        SavedPdfLeakScanner.FindTerm(finalBytes, "Page 1 Content").Should().BeEmpty(
            "the area pass covers page 1's first line");
        SavedPdfLeakScanner.AllCarriersText(finalBytes).Should().Contain("Page 2 Content",
            "page 2 was never touched by either pass");
        using var final = PdfDocument.Open(finalBytes);
        final.PageCount.Should().Be(2);
    }

    #endregion
}
