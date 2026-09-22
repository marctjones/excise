using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
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
    // height-200. The Rect overload takes RENDERED-page coordinates (top-left
    // origin) at the given DPI, so at the default 72 DPI the SECOND line sits
    // around visual y 165-225 and the first is well above it.
    private const string TargetLine = "Secret on Page 1";
    private const string SurvivorLine = "Page 1 Content";
    private static readonly Rect TargetLineVisualArea = new(40, 165, 460, 60);

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
        _service.RedactArea(doc.GetPage(1), TargetLineVisualArea);

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
        var area = new Rect(0, 0, page.Width, page.Height);

        // Act
        _service.RedactArea(page, area);

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

        _service.RedactArea(page, new Rect(0, 0, page.Width, page.Height));

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
        _service.RedactArea(doc.GetPage(1), new Rect(480, 600, 10, 10));

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
        var area = new Rect(10000, 10000, 100, 100);

        // Act & Assert
        var action = () => _service.RedactArea(page, area);
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
        _service.RedactArea(page, new Rect(0, 0, 300, 100));
        _service.RedactArea(page, new Rect(0, 100, 300, 100));

        // Assert - both regions lost their glyphs, read from the SAVED BYTES.
        // The original version compared two counts of a harvested-word list
        // with >=, which a build that redacted nothing also satisfies; its
        // replacement read page.Text, which is excise grading excise (#1769).
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "Line One").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "Line Two").Should().BeEmpty();
    }

    #endregion

    #region RedactAreas Tests

    /// <summary>
    /// #1769: "RedactsAll" asserted only that the call did not throw. Every
    /// line the rectangles cover must actually be gone from the saved file.
    /// </summary>
    [Fact]
    public void RedactAreas_WithMultipleAreas_RedactsAll()
    {
        // Arrange
        string[] lines = ["Line One", "Line Two", "Line Three"];
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateTextOnlyPdf(path, lines));
        var sourceBytes = File.ReadAllBytes(filePath);
        foreach (var line in lines)
            SavedPdfLeakScanner.FindTerm(sourceBytes, line).Should().NotBeEmpty(
                $"input-side control: '{line}' must be findable before the redaction");

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);

        var areas = new List<Rect>
        {
            new Rect(0, 0, 300, 100),
            new Rect(0, 100, 300, 100),
            new Rect(0, 200, 300, 100)
        };

        // Act
        _service.RedactAreas(page, areas);

        // Assert
        var saved = doc.SaveToBytes();
        foreach (var line in lines)
            SavedPdfLeakScanner.FindTerm(saved, line).Should().BeEmpty(
                $"'{line}' was inside one of the redacted rectangles");
    }

    /// <summary>
    /// The empty-input crash guard, plus the property that makes it meaningful:
    /// redacting NO areas must change nothing (#1769). "Did not throw" is also
    /// true of a call that wipes the page.
    /// </summary>
    [Fact]
    public void RedactAreas_WithEmptyList_DoesNothingAndDoesNotThrow()
    {
        // Arrange
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Act & Assert
        var action = () => _service.RedactAreas(page, Array.Empty<Rect>());
        action.Should().NotThrow();

        var saved = SavedPdfLeakScanner.AllCarriersText(doc.SaveToBytes());
        saved.Should().Contain(TargetLine, "no area was given, so nothing may be removed");
        saved.Should().Contain(SurvivorLine, "no area was given, so nothing may be removed");
    }

    [Fact]
    public void RedactAreas_WithSingleArea_RedactsThatArea()
    {
        // Arrange. The token is deliberately NOT the word "Content": a
        // carrier-agnostic byte scan for that would match the /Contents key in
        // every page dictionary and could never fail.
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "PageContentToken"));
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "PageContentToken").Should().NotBeEmpty(
            "input-side control: the token must be findable before the redaction");

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);

        // The rectangle must actually contain the text, or the test's name is a
        // lie. It previously used Rect(50, 50, 100, 100) — which does not cover
        // where CreateSimpleTextPdf puts the text — under an assertion of
        // `Count >= 0` that no build can fail, so nothing noticed.
        var areas = new[] { new Rect(0, 0, page.Width, page.Height) };

        // Act
        _service.RedactAreas(page, areas);

        // Assert — saved bytes, not page.Text (#1769: excise must not be its
        // own oracle for removal).
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "PageContentToken").Should().BeEmpty(
            "RedactAreas must remove the glyphs inside the rectangles it is given");
    }

    /// <summary>
    /// #1769: "RedactsUnion" asserted only that the call did not throw — it
    /// never checked that the union was redacted. Both lines lie inside the two
    /// overlapping rectangles and must both be gone.
    /// </summary>
    [Fact]
    public void RedactAreas_WithOverlappingAreas_RedactsUnion()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateTextOnlyPdf(path, new[] { "Line One", "Line Two" }));
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Line One").Should().NotBeEmpty(
            "input-side control: the lines must be findable before the redaction");
        SavedPdfLeakScanner.FindTerm(sourceBytes, "Line Two").Should().NotBeEmpty();

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);

        // Overlapping areas, in 72-DPI rendered-page coordinates. The two lines
        // sit around visual y 91-100 and 111-120; these rectangles cover one
        // each and overlap in x 100-200, y 90-105. (The default 150 DPI made
        // the old rectangles cover only the FIRST line, which nothing noticed
        // because the test asserted only that the call returned.)
        var areas = new List<Rect>
        {
            new Rect(0, 60, 200, 45),
            new Rect(100, 90, 200, 45)
        };

        // Act
        _service.RedactAreas(page, areas, renderDpi: 72);

        // Assert
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
        var result = _service.RedactText(inputPath, outputPath, "RedactMe");

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
        _service.RedactText(inputPath, outputPath, "SecretTerm");

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
        var result = _service.RedactText(inputPath, outputPath, "NonExistentTerm");

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
        _service.RedactText(inputPath, outputPath, "TestContent");

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
            inputPath, sensitivePath, "testcontenttoken", caseSensitive: true);
        var resultInsensitive = _service.RedactText(
            inputPath, insensitivePath, "testcontenttoken", caseSensitive: false);

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
        var result = _service.RedactText("/nonexistent/path.pdf", outputPath, "Term");

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

        var result = _service.RedactText(inputPath, outputPath, "RedactMe");

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

        var result = _service.RedactText(inputPath, outputPath, "RedactMe", allowLowConfidence: true);

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
        var result = _service.RedactText(inputPath, outputPath, "Secret");

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

    #region RedactWithOptions Tests

    /// <summary>
    /// #1769: this asserted only "does not throw". A redaction with metadata
    /// sanitization must remove BOTH the covered glyphs and the positionless
    /// document carriers the engine strips by default (#897) — a name sitting
    /// in <c>/Title</c> is shown by a reader before the page is ever opened.
    /// </summary>
    [Fact]
    public void RedactWithOptions_WithSanitizeMetadataTrue_RemovesGlyphsAndDocumentCarriers()
    {
        // Arrange
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("TitleCarrierToken in the title");
        var page = doc.GetPage(1);

        var options = new RedactionOptions { SanitizeMetadata = true };

        // Act
        _service.RedactWithOptions(doc, page, new[] { TargetLineVisualArea }, options, renderDpi: 72);

        // Assert
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, TargetLine).Should().BeEmpty(
            "the covered line must be gone from the saved file");
        SavedPdfLeakScanner.FindTerm(saved, "TitleCarrierToken").Should().BeEmpty(
            "the document title is a carrier with no position; sanitization must take it too");
    }

    /// <summary>
    /// #1769: "does not throw" replaced by the removal <c>RemoveAllMetadata</c>
    /// names — asserted unconditionally on a fixture that definitely HAS an
    /// <c>/Info</c> dictionary, so the check cannot be skipped by a document
    /// that never had one.
    /// </summary>
    [Fact]
    public void RedactWithOptions_WithRemoveAllMetadataTrue_RemovesTheInfoDictionary()
    {
        // Arrange
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("InfoDictTitleToken");
        doc.Trailer.ContainsKey("Info").Should().BeTrue("precondition: the fixture has an /Info");
        var page = doc.GetPage(1);

        var options = new RedactionOptions { RemoveAllMetadata = true };

        // Act
        _service.RedactWithOptions(doc, page, new[] { TargetLineVisualArea }, options, renderDpi: 72);

        // Assert
        doc.Trailer.ContainsKey("Info").Should().BeFalse("RemoveAllMetadata drops /Info entirely");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "InfoDictTitleToken").Should().BeEmpty();
    }

    [Fact]
    public void RedactWithOptions_WithTypedPageAreas_DoesNotRequireLegacyRectConversion()
    {
        var filePath = CreateTestFile("typed-redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "TypedAreaToken"));
        var sourceBytes = File.ReadAllBytes(filePath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, "TypedAreaToken").Should().NotBeEmpty(
            "input-side control: the token must be findable before the redaction");

        using var doc = PdfDocument.Open(sourceBytes);
        var page = doc.GetPage(1);
        var options = new RedactionOptions { SanitizeMetadata = true };
        var areas = new[]
        {
            PdfPageRect.FromContentPoints(
                page.PageNumber,
                new PdfRectangle(0, 0, page.Width, page.Height))
        };

        _service.RedactWithOptions(doc, page, areas, options);

        // #1769: the typed overload must not merely refrain from throwing — it
        // has to redact, or a build that ignores typed areas passes.
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "TypedAreaToken").Should().BeEmpty(
            "the typed-area overload must remove the glyphs inside the rectangle");
    }

    [Fact]
    public void RedactWithOptions_CalledTwice_IsIdempotent()
    {
        // Arrange
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var options = new RedactionOptions();
        var areas = new[] { TargetLineVisualArea };

        _service.RedactWithOptions(doc, page, areas, options, renderDpi: 72);
        var afterFirst = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(afterFirst, TargetLine).Should().BeEmpty(
            "precondition: the first pass removed the covered line");

        // Act - Call again. The document-carrier strip removes keys that are
        // already gone the second time round, so this must stay a no-op.
        var again = () => _service.RedactWithOptions(doc, page, areas, options, renderDpi: 72);

        // Assert
        again.Should().NotThrow();
        var afterSecond = SavedPdfLeakScanner.AllCarriersText(doc.SaveToBytes());
        afterSecond.Should().Contain(SurvivorLine,
            "a second pass over the same rectangle must not start eating content outside it");
    }

    /// <summary>
    /// #1769: "does not throw" says nothing about precedence. With both flags
    /// set, the wholesale removal must win — the titled <c>/Info</c> is gone
    /// and its text is in no carrier of the saved file.
    /// </summary>
    [Fact]
    public void RedactWithOptions_WithBothMetadataOptions_RemoveAllTakesPrecedence()
    {
        // Arrange
        var filePath = CreateTwoLinePdf();

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("BothOptionsTitleToken");
        var page = doc.GetPage(1);

        var options = new RedactionOptions
        {
            SanitizeMetadata = true,
            RemoveAllMetadata = true
        };

        // Act
        _service.RedactWithOptions(doc, page, new[] { TargetLineVisualArea }, options, renderDpi: 72);

        // Assert
        doc.Trailer.ContainsKey("Info").Should().BeFalse(
            "RemoveAllMetadata is the stronger of the two and must take effect");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "BothOptionsTitleToken").Should().BeEmpty();
    }

    #endregion

    #region SanitizeMetadata Tests

    /// <summary>
    /// #1769: "does not throw" replaced by the scrub itself. The terms are
    /// planted in <c>/Title</c> first, so absence afterwards is a removal and
    /// not an empty document.
    /// </summary>
    [Fact]
    public void SanitizeMetadata_WithValidDocument_RemovesTheTermsFromTheDocumentCarriers()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("SecretToken and PrivateToken");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "SecretToken").Should().NotBeEmpty(
            "input-side control: the planted title must be findable before the scrub");

        // Act
        _service.SanitizeMetadata(doc, new[] { "SecretToken", "PrivateToken" });

        // Assert
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "SecretToken").Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "PrivateToken").Should().BeEmpty();
    }

    /// <summary>
    /// The empty-input guard, with the property that makes it meaningful: no
    /// terms means no change. #1769 — "does not throw" is also true of a call
    /// that wipes every carrier.
    /// </summary>
    [Fact]
    public void SanitizeMetadata_WithEmptyTermsList_LeavesTheCarriersAlone()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("KeptTitleToken");

        // Act & Assert
        var action = () => _service.SanitizeMetadata(doc, Array.Empty<string>());
        action.Should().NotThrow();

        doc.Title.Should().Be("KeptTitleToken", "an empty term list must scrub nothing");
        SavedPdfLeakScanner.AllCarriersText(doc.SaveToBytes()).Should().Contain("MetaBodyToken");
    }

    /// <summary>
    /// #1769: "ProcessesAll" asserted only that the call returned. Each term is
    /// planted in a DIFFERENT carrier, so a scrub that handles only the first
    /// one reddens.
    /// </summary>
    [Fact]
    public void SanitizeMetadata_WithMultipleTerms_ProcessesAll()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("SecretToken");
        doc.SetAuthor("PrivateToken");
        doc.SetSubject("ConfidentialToken");

        var terms = new[] { "SecretToken", "PrivateToken", "ConfidentialToken" };
        var before = doc.SaveToBytes();
        foreach (var term in terms)
            SavedPdfLeakScanner.FindTerm(before, term).Should().NotBeEmpty(
                $"input-side control: '{term}' must be findable before the scrub");

        // Act
        _service.SanitizeMetadata(doc, terms);

        // Assert
        var saved = doc.SaveToBytes();
        foreach (var term in terms)
            SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty(
                $"'{term}' must be scrubbed from its carrier");
    }

    #endregion

    #region StripAllMetadata Tests

    /// <summary>
    /// #1769: "does not throw" replaced by the removal itself, on a document
    /// that provably has an <c>/Info</c> to remove.
    /// </summary>
    [Fact]
    public void StripAllMetadata_WithValidDocument_RemovesTheInfoText()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("StripAllTitleToken");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "StripAllTitleToken").Should().NotBeEmpty(
            "input-side control: the planted title must be findable before the strip");

        // Act
        _service.StripAllMetadata(doc);

        // Assert
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "StripAllTitleToken").Should().BeEmpty();
    }

    [Fact]
    public void StripAllMetadata_RemovesInfoDictionary()
    {
        // Arrange. The title is set so the fixture DEFINITELY has an /Info —
        // the assertion below used to hide behind `if (hasInfoBefore)`, which
        // makes it vacuous on any document without one (#1769).
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("InfoPresenceToken");
        doc.Trailer.ContainsKey("Info").Should().BeTrue("precondition: there is an /Info to remove");

        // Act
        _service.StripAllMetadata(doc);

        // Assert — unconditional.
        doc.Trailer.ContainsKey("Info").Should().BeFalse();
    }

    [Fact]
    public void StripAllMetadata_CalledMultipleTimes_IsIdempotent()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "MetaBodyToken"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        doc.SetTitle("IdempotentTitleToken");

        // Act
        _service.StripAllMetadata(doc);
        var afterFirstCall = doc.Trailer.ContainsKey("Info");

        var again = () => _service.StripAllMetadata(doc);

        // Assert — BOTH false. `afterFirst.Should().Be(afterSecond)` was also
        // satisfied by /Info surviving both calls (#1769).
        again.Should().NotThrow();
        afterFirstCall.Should().BeFalse("the first call removes /Info");
        doc.Trailer.ContainsKey("Info").Should().BeFalse("the second call leaves it removed");
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

        _service.RedactArea(page, TargetLineVisualArea);
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
            _service.RedactArea(doc.GetPage(i), TargetLineVisualArea);

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
        var textResult = _service.RedactText(inputPath, intermediatePath, "Secret");
        textResult.Success.Should().BeTrue();

        // Then do area redaction on the result
        using var doc = PdfDocument.Open(File.ReadAllBytes(intermediatePath));
        var page = doc.GetPage(1);
        _service.RedactArea(page, new Rect(0, 60, 612, 80));
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
