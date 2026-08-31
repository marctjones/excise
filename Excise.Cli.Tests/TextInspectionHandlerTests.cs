using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class TextInspectionHandlerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-text-handler-{Guid.NewGuid():N}");

    public TextInspectionHandlerTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Execute_ReturnsTypedTextForAllOrSelectedPages(int? pageNumber)
    {
        var path = WritePdf("text.pdf", "TYPED TEXT RESULT");

        var result = TextInspectionHandler.Execute(
            Request(path, pageNumber),
            TestContext.Current.CancellationToken);

        result.FilePath.Should().Be(Path.GetFullPath(path));
        result.PageCount.Should().Be(1);
        result.SelectedPageNumber.Should().Be(pageNumber);
        result.Pages.Should().ContainSingle();
        result.Pages[0].PageNumber.Should().Be(1);
        result.Pages[0].Text.Should().Contain("TYPED TEXT RESULT");
    }

    [Fact]
    public void Execute_PageOutsideDocument_ThrowsTypedRangeFailure()
    {
        var path = WritePdf("range.pdf", "PAGE RANGE");

        var act = () => TextInspectionHandler.Execute(Request(path, pageNumber: 2));

        var exception = act.Should().Throw<DocumentPageOutOfRangeException>().Which;
        exception.PageNumber.Should().Be(2);
        exception.PageCount.Should().Be(1);
    }

    [Fact]
    public void Execute_CanceledRequest_StopsBeforeOpeningDocument()
    {
        var path = WritePdf("canceled.pdf", "CANCELED");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => TextInspectionHandler.Execute(Request(path), cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    private static TextInspectionRequest Request(string path, int? pageNumber = null)
        => new(
            path,
            Password: null,
            PageNumber: pageNumber,
            IgnorePermissions: false,
            ForAccessibility: false);

    private string WritePdf(string fileName, string text)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage(text));
        return path;
    }
}
