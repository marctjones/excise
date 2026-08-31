using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class LetterInspectionHandlerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-letter-handler-{Guid.NewGuid():N}");

    public LetterInspectionHandlerTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Execute_ReturnsBoundedTypedLetterPositions()
    {
        var path = WritePdf("letters.pdf", "LETTERS");

        var result = LetterInspectionHandler.Execute(
            Request(path, limit: 2),
            TestContext.Current.CancellationToken);

        result.FilePath.Should().Be(Path.GetFullPath(path));
        result.PageNumber.Should().Be(1);
        result.TotalLetterCount.Should().BeGreaterThanOrEqualTo(2);
        result.Letters.Should().HaveCount(2);
        result.Letters[0].Value.Should().Be("L");
        result.Letters[0].FontName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Execute_PageOutsideDocument_ThrowsTypedRangeFailure()
    {
        var path = WritePdf("range.pdf", "RANGE");

        var act = () => LetterInspectionHandler.Execute(Request(path, pageNumber: 2));

        var exception = act.Should().Throw<DocumentPageOutOfRangeException>().Which;
        exception.PageNumber.Should().Be(2);
        exception.PageCount.Should().Be(1);
    }

    [Fact]
    public void Execute_NegativeLimit_IsRejectedBeforeDocumentWork()
    {
        var path = Path.Combine(_tempDirectory, "does-not-need-to-exist.pdf");

        var act = () => LetterInspectionHandler.Execute(Request(path, limit: -1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static LetterInspectionRequest Request(
        string path,
        int pageNumber = 1,
        int limit = 50)
        => new(
            path,
            PageNumber: pageNumber,
            Limit: limit,
            IgnorePermissions: false,
            ForAccessibility: false);

    private string WritePdf(string fileName, string text)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage(text));
        return path;
    }
}
