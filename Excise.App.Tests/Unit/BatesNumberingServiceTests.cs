using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Unit tests for BatesNumberingService.
/// Tests Bates numbering options, document result tracking, and error handling.
/// </summary>
public class BatesNumberingServiceTests
{
    private readonly BatesNumberingService _service;
    private readonly ILogger<BatesNumberingService> _logger;

    public BatesNumberingServiceTests()
    {
        _logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<BatesNumberingService>();
        _service = new BatesNumberingService(_logger);
    }

    // ========================================================================
    // BATES OPTIONS DEFAULTS TESTS
    // ========================================================================

    [Fact]
    public void BatesOptions_DefaultPrefix_IsEmpty()
    {
        var options = new BatesOptions();
        options.Prefix.Should().Be("");
    }

    [Fact]
    public void BatesOptions_DefaultSuffix_IsEmpty()
    {
        var options = new BatesOptions();
        options.Suffix.Should().Be("");
    }

    [Fact]
    public void BatesOptions_DefaultStartNumber_IsOne()
    {
        var options = new BatesOptions();
        options.StartNumber.Should().Be(1);
    }

    [Fact]
    public void BatesOptions_DefaultNumberOfDigits_IsSix()
    {
        var options = new BatesOptions();
        options.NumberOfDigits.Should().Be(6);
    }

    [Fact]
    public void BatesOptions_DefaultPosition_IsBottomRight()
    {
        var options = new BatesOptions();
        options.Position.Should().Be(BatesPosition.BottomRight);
    }

    [Fact]
    public void BatesOptions_DefaultFontName_IsArial()
    {
        var options = new BatesOptions();
        options.FontName.Should().Be("Arial");
    }

    [Fact]
    public void BatesOptions_DefaultFontSize_IsTen()
    {
        var options = new BatesOptions();
        options.FontSize.Should().Be(10);
    }

    [Fact]
    public void BatesOptions_DefaultMarginX_Is36()
    {
        var options = new BatesOptions();
        options.MarginX.Should().Be(36);
    }

    [Fact]
    public void BatesOptions_DefaultMarginY_Is36()
    {
        var options = new BatesOptions();
        options.MarginY.Should().Be(36);
    }

    [Fact]
    public void BatesOptions_DefaultOutputDirectory_IsNull()
    {
        var options = new BatesOptions();
        options.OutputDirectory.Should().BeNull();
    }

    [Fact]
    public void BatesOptions_DefaultOutputSuffix_IsBates()
    {
        var options = new BatesOptions();
        options.OutputSuffix.Should().Be("_bates");
    }

    // ========================================================================
    // BATES POSITION ENUM TESTS
    // ========================================================================

    [Fact]
    public void BatesPosition_HasAllPositions()
    {
        var positions = new[]
        {
            BatesPosition.TopLeft,
            BatesPosition.TopCenter,
            BatesPosition.TopRight,
            BatesPosition.BottomLeft,
            BatesPosition.BottomCenter,
            BatesPosition.BottomRight
        };

        positions.Should().HaveCount(6);
    }

    // ========================================================================
    // BATES RESULT TESTS
    // ========================================================================

    [Fact]
    public void BatesResult_EmptyDocuments_InitializesAsEmptyList()
    {
        var result = new BatesResult();
        result.Documents.Should().NotBeNull();
        result.Documents.Should().BeEmpty();
    }

    [Fact]
    public void BatesResult_FirstBatesNumber_DefaultIsEmpty()
    {
        var result = new BatesResult();
        result.FirstBatesNumber.Should().Be("");
    }

    [Fact]
    public void BatesResult_LastBatesNumber_DefaultIsEmpty()
    {
        var result = new BatesResult();
        result.LastBatesNumber.Should().Be("");
    }

    [Fact]
    public void BatesResult_NextBatesNumber_DefaultIsZero()
    {
        var result = new BatesResult();
        result.NextBatesNumber.Should().Be(0);
    }

    [Fact]
    public void BatesResult_TotalPages_DefaultIsZero()
    {
        var result = new BatesResult();
        result.TotalPages.Should().Be(0);
    }

    [Fact]
    public void BatesResult_CanAddDocuments()
    {
        var result = new BatesResult();
        var docResult = new BatesDocumentResult
        {
            FilePath = "/path/to/file.pdf",
            FileName = "file.pdf",
            Success = true
        };

        result.Documents.Add(docResult);

        result.Documents.Should().HaveCount(1);
        result.Documents[0].FilePath.Should().Be("/path/to/file.pdf");
    }

    // ========================================================================
    // BATES DOCUMENT RESULT TESTS
    // ========================================================================

    [Fact]
    public void BatesDocumentResult_DefaultFilePath_IsEmpty()
    {
        var result = new BatesDocumentResult();
        result.FilePath.Should().Be("");
    }

    [Fact]
    public void BatesDocumentResult_DefaultFileName_IsEmpty()
    {
        var result = new BatesDocumentResult();
        result.FileName.Should().Be("");
    }

    [Fact]
    public void BatesDocumentResult_DefaultOutputPath_IsEmpty()
    {
        var result = new BatesDocumentResult();
        result.OutputPath.Should().Be("");
    }

    [Fact]
    public void BatesDocumentResult_DefaultFirstBatesNumber_IsEmpty()
    {
        var result = new BatesDocumentResult();
        result.FirstBatesNumber.Should().Be("");
    }

    [Fact]
    public void BatesDocumentResult_DefaultLastBatesNumber_IsEmpty()
    {
        var result = new BatesDocumentResult();
        result.LastBatesNumber.Should().Be("");
    }

    [Fact]
    public void BatesDocumentResult_DefaultPageCount_IsZero()
    {
        var result = new BatesDocumentResult();
        result.PageCount.Should().Be(0);
    }

    [Fact]
    public void BatesDocumentResult_DefaultSuccess_IsFalse()
    {
        var result = new BatesDocumentResult();
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void BatesDocumentResult_DefaultErrorMessage_IsNull()
    {
        var result = new BatesDocumentResult();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void BatesDocumentResult_CanSetProperties()
    {
        var result = new BatesDocumentResult
        {
            FilePath = "/path/file.pdf",
            FileName = "file.pdf",
            OutputPath = "/path/file_bates.pdf",
            FirstBatesNumber = "000001",
            LastBatesNumber = "000010",
            PageCount = 10,
            Success = true,
            ErrorMessage = null
        };

        result.FilePath.Should().Be("/path/file.pdf");
        result.FileName.Should().Be("file.pdf");
        result.OutputPath.Should().Be("/path/file_bates.pdf");
        result.FirstBatesNumber.Should().Be("000001");
        result.LastBatesNumber.Should().Be("000010");
        result.PageCount.Should().Be(10);
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    // ========================================================================
    // APPLY BATES NUMBERS TO SET - ERROR HANDLING
    // ========================================================================

    [Fact]
    public void ApplyBatesNumbersToSet_NonExistentFile_ReturnsFailureResult()
    {
        var result = _service.ApplyBatesNumbersToSet(
            new[] { "/nonexistent/file.pdf" },
            new BatesOptions());

        result.Documents.Should().HaveCount(1);
        result.Documents[0].Success.Should().BeFalse();
        result.Documents[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ApplyBatesNumbersToSet_NonExistentFile_FilesSetCorrectly()
    {
        var filePath = "/nonexistent/file.pdf";
        var result = _service.ApplyBatesNumbersToSet(
            new[] { filePath },
            new BatesOptions());

        result.Documents[0].FilePath.Should().Be(filePath);
        result.Documents[0].FileName.Should().Be("file.pdf");
    }

    [Fact]
    public void ApplyBatesNumbersToSet_DuplicateFiles_ProcessesOnlyOnce()
    {
        var filePath = "/nonexistent/file.pdf";
        var result = _service.ApplyBatesNumbersToSet(
            new[] { filePath, filePath },
            new BatesOptions());

        result.Documents.Should().HaveCount(1,
            "Duplicate file should be skipped, only one failure recorded");
    }

    [Fact]
    public void ApplyBatesNumbersToSet_EmptyFileList_ReturnsEmptyResult()
    {
        var result = _service.ApplyBatesNumbersToSet(
            new string[] { },
            new BatesOptions());

        result.Documents.Should().BeEmpty();
    }

    [Fact]
    public void ApplyBatesNumbersToSet_NonExistentFiles_SetsFirstAndLastNumbersCorrectly()
    {
        var options = new BatesOptions { StartNumber = 100, NumberOfDigits = 4 };
        var result = _service.ApplyBatesNumbersToSet(
            new[] { "/nonexistent1.pdf", "/nonexistent2.pdf" },
            options);

        result.FirstBatesNumber.Should().Be("0100");
        result.LastBatesNumber.Should().Be("0099");  // No pages processed, so stays at startNumber-1
    }

    [Fact]
    public void ApplyBatesNumbersToSet_WithPrefix_IncludesPrefixInNumber()
    {
        var options = new BatesOptions { StartNumber = 1, Prefix = "DOC-" };
        var result = _service.ApplyBatesNumbersToSet(
            new string[] { },
            options);

        result.FirstBatesNumber.Should().Be("DOC-000001");
    }

    [Fact]
    public void ApplyBatesNumbersToSet_WithSuffix_IncludesSuffixInNumber()
    {
        var options = new BatesOptions { StartNumber = 1, Suffix = "-CONF" };
        var result = _service.ApplyBatesNumbersToSet(
            new string[] { },
            options);

        result.FirstBatesNumber.Should().Be("000001-CONF");
    }

    [Fact]
    public void ApplyBatesNumbersToSet_WithPrefixAndSuffix_IncludesBoth()
    {
        var options = new BatesOptions
        {
            StartNumber = 1,
            Prefix = "DOC",
            Suffix = "CONF"
        };
        var result = _service.ApplyBatesNumbersToSet(
            new string[] { },
            options);

        result.FirstBatesNumber.Should().Be("DOC000001CONF");
    }

    // ========================================================================
    // CALCULATE NEXT NUMBER TESTS
    // ========================================================================

    [Fact]
    public void CalculateNextNumber_NonExistentFiles_ReturnsStartNumber()
    {
        var result = _service.CalculateNextNumber(
            new[] { "/nonexistent/file.pdf" },
            100);

        result.Should().Be(100,
            "Non-existent file contributes 0 pages, so next number = startNumber + 0");
    }

    [Fact]
    public void CalculateNextNumber_EmptyFileList_ReturnsStartNumber()
    {
        var result = _service.CalculateNextNumber(
            new string[] { },
            50);

        result.Should().Be(50);
    }

    [Fact]
    public void CalculateNextNumber_MultipleNonExistentFiles_ReturnsStartNumber()
    {
        var result = _service.CalculateNextNumber(
            new[] { "/nonexistent1.pdf", "/nonexistent2.pdf", "/nonexistent3.pdf" },
            200);

        result.Should().Be(200,
            "All non-existent files contribute 0 pages");
    }

    [Fact]
    public void CalculateNextNumber_WithDifferentStartNumbers_CalculatesCorrectly()
    {
        var startNumbers = new[] { 1, 10, 100, 1000 };

        foreach (var start in startNumbers)
        {
            var result = _service.CalculateNextNumber(new string[] { }, start);
            result.Should().Be(start);
        }
    }

    // ========================================================================
    // BATES OPTIONS CUSTOMIZATION TESTS
    // ========================================================================

    [Fact]
    public void BatesOptions_CanSetCustomValues()
    {
        var options = new BatesOptions
        {
            Prefix = "CASE-",
            Suffix = "-SEALED",
            StartNumber = 500,
            NumberOfDigits = 8,
            Position = BatesPosition.TopCenter,
            FontName = "Times New Roman",
            FontSize = 12,
            MarginX = 50,
            MarginY = 75,
            OutputDirectory = "/output",
            OutputSuffix = "_numbered"
        };

        options.Prefix.Should().Be("CASE-");
        options.Suffix.Should().Be("-SEALED");
        options.StartNumber.Should().Be(500);
        options.NumberOfDigits.Should().Be(8);
        options.Position.Should().Be(BatesPosition.TopCenter);
        options.FontName.Should().Be("Times New Roman");
        options.FontSize.Should().Be(12);
        options.MarginX.Should().Be(50);
        options.MarginY.Should().Be(75);
        options.OutputDirectory.Should().Be("/output");
        options.OutputSuffix.Should().Be("_numbered");
    }

    [Fact]
    public void BatesOptions_AllPositions_AreAccessible()
    {
        var positions = new[]
        {
            BatesPosition.TopLeft,
            BatesPosition.TopCenter,
            BatesPosition.TopRight,
            BatesPosition.BottomLeft,
            BatesPosition.BottomCenter,
            BatesPosition.BottomRight
        };

        foreach (var position in positions)
        {
            var options = new BatesOptions { Position = position };
            options.Position.Should().Be(position);
        }
    }

    // ========================================================================
    // ZERO PADDING TESTS
    // ========================================================================

    [Theory]
    [InlineData(1, 6)]       // Default: 6 digits, so 000001
    [InlineData(1, 4)]       // Custom: 4 digits, so 0001
    [InlineData(1, 8)]       // Custom: 8 digits, so 00000001
    [InlineData(999, 6)]     // 999 with 6 digits = 000999
    public void ApplyBatesNumbersToSet_PadsWithZeros_BasedOnNumberOfDigits(int startNumber, int digits)
    {
        var options = new BatesOptions { StartNumber = startNumber, NumberOfDigits = digits };
        var expectedNumber = startNumber.ToString().PadLeft(digits, '0');

        var result = _service.ApplyBatesNumbersToSet(
            new string[] { },
            options);

        result.FirstBatesNumber.Should().Be(expectedNumber);
    }

    // ========================================================================
    // RESULT STATE CONSISTENCY TESTS
    // ========================================================================

    [Fact]
    public void ApplyBatesNumbersToSet_ErrorCase_DoesNotThrow()
    {
        var action = () => _service.ApplyBatesNumbersToSet(
            new[] { "/nonexistent.pdf" },
            new BatesOptions());

        action.Should().NotThrow();
    }

    [Fact]
    public void ApplyBatesNumbersToSet_TotalPages_StartsAtZero()
    {
        var result = _service.ApplyBatesNumbersToSet(
            new[] { "/nonexistent.pdf" },
            new BatesOptions());

        result.TotalPages.Should().Be(0);
    }

    [Fact]
    public void BatesResult_AllProperties_CanBeSet()
    {
        var result = new BatesResult
        {
            FirstBatesNumber = "ABC000001",
            LastBatesNumber = "ABC000010",
            NextBatesNumber = 11,
            TotalPages = 10
        };

        result.FirstBatesNumber.Should().Be("ABC000001");
        result.LastBatesNumber.Should().Be("ABC000010");
        result.NextBatesNumber.Should().Be(11);
        result.TotalPages.Should().Be(10);
    }
}
