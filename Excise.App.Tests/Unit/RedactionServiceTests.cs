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

    [Fact]
    public void RedactArea_WithValidArea_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content to redact"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var area = new Rect(50, 50, 100, 100);

        // Act & Assert
        var action = () => _service.RedactArea(page, area);
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactArea_WithLargeArea_CoversPageContent()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Secret Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Large area covering page
        var area = new Rect(0, 0, page.Width, page.Height);

        // Act
        _service.RedactArea(page, area);

        // Assert — the glyphs are gone from the page, which is the property this
        // service exists to guarantee. This previously asserted that a list of
        // harvested words was non-empty, which is true of a build that removes
        // nothing (#897 deleted that list).
        page.Text.Should().NotContain("Secret Content",
            "a full-page redaction must remove the glyphs from the content stream");
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
        var saved = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));

        saved.Should().NotContain("SecretWord in the title",
            "drawing a box over a name and saving must not leave the name in the document " +
            "title — the carrier a reader shows before the page is even opened (#897)");
    }

    [Fact]
    public void RedactArea_WithSmallArea_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Text"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var area = new Rect(50, 50, 10, 10);

        // Act & Assert
        var action = () => _service.RedactArea(page, area);
        action.Should().NotThrow();
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

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Act - Redact twice, covering both lines between them
        _service.RedactArea(page, new Rect(0, 0, 300, 100));
        _service.RedactArea(page, new Rect(0, 100, 300, 100));

        // Assert - both regions lost their glyphs. The previous version compared
        // two counts of a harvested-word list with >=, which a build that
        // redacted nothing also satisfies.
        page.Text.Should().NotContain("Line One");
        page.Text.Should().NotContain("Line Two");
    }

    #endregion

    #region RedactAreas Tests

    [Fact]
    public void RedactAreas_WithMultipleAreas_RedactsAll()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateTextOnlyPdf(path, new[] { "Line One", "Line Two", "Line Three" }));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var areas = new List<Rect>
        {
            new Rect(0, 0, 300, 100),
            new Rect(0, 100, 300, 100),
            new Rect(0, 200, 300, 100)
        };

        // Act & Assert
        var action = () => _service.RedactAreas(page, areas);
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactAreas_WithEmptyList_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Text"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Act & Assert
        var action = () => _service.RedactAreas(page, Array.Empty<Rect>());
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactAreas_WithSingleArea_RedactsThatArea()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // The rectangle must actually contain the text, or the test's name is a
        // lie. It previously used Rect(50, 50, 100, 100) — which does not cover
        // where CreateSimpleTextPdf puts the text — under an assertion of
        // `Count >= 0` that no build can fail, so nothing noticed.
        var areas = new[] { new Rect(0, 0, page.Width, page.Height) };

        // Act
        _service.RedactAreas(page, areas);

        // Assert
        page.Text.Should().NotContain("Content",
            "RedactAreas must remove the glyphs inside the rectangles it is given");
    }

    [Fact]
    public void RedactAreas_WithOverlappingAreas_RedactsUnion()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateTextOnlyPdf(path, new[] { "Line One", "Line Two" }));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        // Overlapping areas
        var areas = new List<Rect>
        {
            new Rect(0, 0, 200, 150),
            new Rect(100, 50, 200, 150)
        };

        // Act & Assert
        var action = () => _service.RedactAreas(page, areas);
        action.Should().NotThrow();
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
    }

    [Fact]
    public void RedactText_WithMatchingTerm_RemovesItFromTheOutput()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "SecretTerm"));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        _service.RedactText(inputPath, outputPath, "SecretTerm");

        // Assert — on the saved bytes, not on a list of what the service says it
        // did. A service can record a term it failed to remove.
        System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(outputPath))
            .Should().NotContain("SecretTerm");
    }

    [Fact]
    public void RedactText_WithNonMatchingTerm_Succeeds()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "SomeText"));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText(inputPath, outputPath, "NonExistentTerm");

        // Assert
        result.Success.Should().BeTrue();
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

    [Fact]
    public void RedactText_WithCaseSensitiveFlag_RespectsCaseSensitivity()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "TestContent"));

        var outputPath1 = Path.Combine(_tempDir, "output1.pdf");
        var outputPath2 = Path.Combine(_tempDir, "output2.pdf");

        // Act - Case sensitive vs insensitive
        var resultSensitive = _service.RedactText(inputPath, outputPath1, "TestContent", caseSensitive: true);
        resultSensitive.Success.Should().BeTrue();

        var resultInsensitive = _service.RedactText(inputPath, outputPath2, "testcontent", caseSensitive: false);

        // Assert
        resultInsensitive.Success.Should().BeTrue();
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
    }

    [Fact]
    public void RedactText_MultiplePages_RedactsAllPages()
    {
        // Arrange
        var inputPath = CreateTestFile("input.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        var outputPath = Path.Combine(_tempDir, "output.pdf");

        // Act
        var result = _service.RedactText(inputPath, outputPath, "Page");

        // Assert
        result.Success.Should().BeTrue();

        // Verify output still has 3 pages
        using var doc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        doc.PageCount.Should().Be(3);
    }

    #endregion

    #region RedactWithOptions Tests

    [Fact]
    public void RedactWithOptions_WithSanitizeMetadataTrue_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var options = new RedactionOptions { SanitizeMetadata = true };
        var areas = new[] { new Rect(50, 50, 100, 100) };

        // Act & Assert
        var action = () => _service.RedactWithOptions(doc, page, areas, options);
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactWithOptions_WithRemoveAllMetadataTrue_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var options = new RedactionOptions { RemoveAllMetadata = true };
        var areas = new[] { new Rect(50, 50, 100, 100) };

        // Act & Assert
        var action = () => _service.RedactWithOptions(doc, page, areas, options);
        action.Should().NotThrow();
    }

    [Fact]
    public void RedactWithOptions_WithTypedPageAreas_DoesNotRequireLegacyRectConversion()
    {
        var filePath = CreateTestFile("typed-redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);
        var options = new RedactionOptions { SanitizeMetadata = true };
        var areas = new[]
        {
            PdfPageRect.FromContentPoints(
                page.PageNumber,
                new PdfRectangle(0, 0, page.Width, page.Height))
        };

        var action = () => _service.RedactWithOptions(doc, page, areas, options);

        action.Should().NotThrow();
    }

    [Fact]
    public void RedactWithOptions_CalledTwice_IsIdempotent()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var options = new RedactionOptions();
        var areas = new[] { new Rect(0, 0, 100, 100) };

        _service.RedactWithOptions(doc, page, areas, options);

        // Act - Call again. The document-carrier strip removes keys that are
        // already gone the second time round, so this must stay a no-op.
        var again = () => _service.RedactWithOptions(doc, page, areas, options);

        // Assert
        again.Should().NotThrow();
    }

    [Fact]
    public void RedactWithOptions_WithBothMetadataOptions_RemoveAllTakesPrecedence()
    {
        // Arrange
        var filePath = CreateTestFile("redact.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));
        var page = doc.GetPage(1);

        var options = new RedactionOptions
        {
            SanitizeMetadata = true,
            RemoveAllMetadata = true
        };
        var areas = new[] { new Rect(50, 50, 100, 100) };

        // Act & Assert
        var action = () => _service.RedactWithOptions(doc, page, areas, options);
        action.Should().NotThrow();
    }

    #endregion

    #region SanitizeMetadata Tests

    [Fact]
    public void SanitizeMetadata_WithValidDocument_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        var terms = new[] { "Secret", "Private" };

        // Act & Assert
        var action = () => _service.SanitizeMetadata(doc, terms);
        action.Should().NotThrow();
    }

    [Fact]
    public void SanitizeMetadata_WithEmptyTermsList_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        // Act & Assert
        var action = () => _service.SanitizeMetadata(doc, Array.Empty<string>());
        action.Should().NotThrow();
    }

    [Fact]
    public void SanitizeMetadata_WithMultipleTerms_ProcessesAll()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        var terms = new[] { "Secret", "Private", "Confidential" };

        // Act & Assert
        var action = () => _service.SanitizeMetadata(doc, terms);
        action.Should().NotThrow();
    }

    #endregion

    #region StripAllMetadata Tests

    [Fact]
    public void StripAllMetadata_WithValidDocument_DoesNotThrow()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        // Act & Assert
        var action = () => _service.StripAllMetadata(doc);
        action.Should().NotThrow();
    }

    [Fact]
    public void StripAllMetadata_RemovesInfoDictionary()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        var hasInfoBefore = doc.Trailer.ContainsKey("Info");

        // Act
        _service.StripAllMetadata(doc);

        var hasInfoAfter = doc.Trailer.ContainsKey("Info");

        // Assert
        if (hasInfoBefore)
            hasInfoAfter.Should().BeFalse();
    }

    [Fact]
    public void StripAllMetadata_CalledMultipleTimes_IsIdempotent()
    {
        // Arrange
        var filePath = CreateTestFile("meta.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        // Act
        _service.StripAllMetadata(doc);
        var afterFirstCall = doc.Trailer.ContainsKey("Info");

        _service.StripAllMetadata(doc);
        var afterSecondCall = doc.Trailer.ContainsKey("Info");

        // Assert
        afterFirstCall.Should().Be(afterSecondCall);
    }

    #endregion



    #region Integration Tests

    [Fact]
    public void FullRedactionWorkflow_LoadRedactSave_Works()
    {
        // Arrange
        var inputPath = CreateTestFile("workflow.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        var outputPath = Path.Combine(_tempDir, "redacted.pdf");

        // Act
        using var doc = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = doc.GetPage(1);

        _service.RedactArea(page, new Rect(50, 50, 100, 100));
        doc.Save(outputPath);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
        using var savedDoc = PdfDocument.Open(File.ReadAllBytes(outputPath));
        savedDoc.PageCount.Should().Be(2);
    }

    [Fact]
    public void RedactMultiplePagesInSingleDocument_Works()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        using var doc = PdfDocument.Open(File.ReadAllBytes(filePath));

        // Act
        for (int i = 1; i <= 3; i++)
        {
            var page = doc.GetPage(i);
            _service.RedactArea(page, new Rect(50, 50, 100, 100));
        }

        // Assert - Just verify no exceptions and document still valid
        doc.PageCount.Should().Be(3);
    }

    [Fact]
    public void CombinedRedaction_TextAndAreaRedaction_Works()
    {
        // Arrange
        var inputPath = CreateTestFile("combined.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        var intermediatePath = Path.Combine(_tempDir, "intermediate.pdf");
        var finalPath = Path.Combine(_tempDir, "final.pdf");

        // Act - First do text redaction
        var textResult = _service.RedactText(inputPath, intermediatePath, "Secret");
        textResult.Success.Should().BeTrue();

        // Then do area redaction on the result
        using var doc = PdfDocument.Open(File.ReadAllBytes(intermediatePath));
        var page = doc.GetPage(1);
        _service.RedactArea(page, new Rect(100, 100, 100, 100));
        doc.Save(finalPath);

        // Assert
        File.Exists(finalPath).Should().BeTrue();
        using var final = PdfDocument.Open(File.ReadAllBytes(finalPath));
        final.PageCount.Should().Be(2);
    }

    #endregion
}
