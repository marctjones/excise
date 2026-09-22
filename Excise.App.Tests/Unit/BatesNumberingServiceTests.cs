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
    // APPLY BATES NUMBERS TO SET - ERROR HANDLING
    // ========================================================================

    [Fact]
    public void ApplyBatesNumbersToSet_NonExistentFile_ReturnsFailureResult()
    {
        var filePath = "/nonexistent/file.pdf";

        var result = _service.ApplyBatesNumbersToSet(new[] { filePath }, new BatesOptions());

        result.Documents.Should().HaveCount(1);
        result.Documents[0].Success.Should().BeFalse();
        result.Documents[0].ErrorMessage.Should().NotBeNullOrEmpty();
        result.Documents[0].FilePath.Should().Be(filePath);
        result.Documents[0].FileName.Should().Be("file.pdf");
        result.TotalPages.Should().Be(0);
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

    // ========================================================================
    // NUMBER FORMAT
    // ========================================================================

    [Theory]
    [InlineData("", "", "000001")]
    [InlineData("DOC-", "", "DOC-000001")]
    [InlineData("", "-CONF", "000001-CONF")]
    [InlineData("DOC", "CONF", "DOC000001CONF")]
    public void ApplyBatesNumbersToSet_FirstNumber_IncludesPrefixAndSuffix(
        string prefix, string suffix, string expected)
    {
        var options = new BatesOptions { StartNumber = 1, Prefix = prefix, Suffix = suffix };

        var result = _service.ApplyBatesNumbersToSet(new string[] { }, options);

        result.FirstBatesNumber.Should().Be(expected);
    }

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

    [Theory]
    [InlineData(new string[] { }, 50)]
    [InlineData(new[] { "/nonexistent/file.pdf" }, 100)]
    [InlineData(new[] { "/nonexistent1.pdf", "/nonexistent2.pdf", "/nonexistent3.pdf" }, 200)]
    public void CalculateNextNumber_FilesThatContributeNoPages_ReturnsStartNumber(
        string[] files, int startNumber)
    {
        _service.CalculateNextNumber(files, startNumber).Should().Be(startNumber,
            "a missing file contributes 0 pages, so next number = startNumber + 0");
    }

    // ========================================================================
    // #1671: A PREFIX THE STAMP FONT CANNOT DRAW
    // ========================================================================

    [Fact]
    public void ApplyBatesNumbers_PrefixOutsideTheFontEncoding_IsRefused_BeforeAnyPageIsStamped()
    {
        using var document = Excise.Core.Document.PdfDocument.CreateNew();
        document.Pages.AddBlank(300, 400);
        document.Pages.AddBlank(300, 400);

        var act = () => _service.ApplyBatesNumbers(document, new BatesOptions { Prefix = "ŁÓDŹ-" });

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("U+0141").And.Contain("Bates prefix and suffix");
        document.GetPage(1).GetContentStreamBytes().Should().BeEmpty();
        document.GetPage(2).GetContentStreamBytes().Should().BeEmpty();
    }

    [Fact]
    public void ApplyBatesNumbers_LatinOneAccentedPrefix_StillStamps()
    {
        using var document = Excise.Core.Document.PdfDocument.CreateNew();
        document.Pages.AddBlank(300, 400);

        _service.ApplyBatesNumbers(document, new BatesOptions { Prefix = "MÜLLER-JOSÉ-" });

        System.Text.Encoding.Latin1.GetString(document.GetPage(1).GetContentStreamBytes())
            .Should().Contain("(M\\334LLER-JOS\\311-000001)").And.NotContain("?");
    }

    [Fact]
    public void ApplyBatesNumbersToSet_PrefixOutsideTheFontEncoding_RecordsAFailureThatNamesTheCharacter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-bates-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = Excise.Core.Document.PdfDocument.CreateNew())
            {
                document.Pages.AddBlank(300, 400);
                document.Save(path);
            }

            var result = _service.ApplyBatesNumbersToSet(new[] { path }, new BatesOptions { Prefix = "Ł-" });

            result.Documents.Should().ContainSingle().Which.Success.Should().BeFalse();
            result.Documents[0].ErrorMessage.Should().Contain("U+0141");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
