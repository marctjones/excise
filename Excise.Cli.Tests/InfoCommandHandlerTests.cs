using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class InfoCommandHandlerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-info-handler-{Guid.NewGuid():N}");

    public InfoCommandHandlerTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Execute_ReturnsTypedDocumentAndPageInformation()
    {
        var path = WritePdf("document.pdf");

        var result = InfoCommandHandler.Execute(
            new DocumentInfoRequest(path, Password: null),
            TestContext.Current.CancellationToken);

        result.FilePath.Should().Be(Path.GetFullPath(path));
        result.FileName.Should().Be("document.pdf");
        result.SizeBytes.Should().Be(new FileInfo(path).Length);
        result.Version.Should().Be("1.4");
        result.PageCount.Should().Be(1);
        result.Encrypted.Should().BeFalse();
        result.Pages.Should().ContainSingle();
        result.Pages[0].Should().Be(new DocumentPageInfo(1, 612, 792));
    }

    [Fact]
    public void Execute_ZeroPageDetailLimit_ReturnsSummaryWithoutPageDetails()
    {
        var path = WritePdf("summary-only.pdf");

        var result = InfoCommandHandler.Execute(
            new DocumentInfoRequest(
                path,
                Password: null,
                PageDetailLimit: 0),
            TestContext.Current.CancellationToken);

        result.PageCount.Should().Be(1);
        result.Pages.Should().BeEmpty();
    }

    [Fact]
    public void Execute_MissingFile_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_tempDirectory, "missing.pdf");

        var act = () => InfoCommandHandler.Execute(new DocumentInfoRequest(
            missingPath,
            Password: null));

        act.Should().Throw<FileNotFoundException>()
            .Which.FileName.Should().Be(Path.GetFullPath(missingPath));
    }

    [Fact]
    public void Execute_CanceledRequest_StopsBeforeOpeningDocument()
    {
        var path = WritePdf("canceled.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => InfoCommandHandler.Execute(
            new DocumentInfoRequest(path, Password: null),
            cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    private string WritePdf(string fileName)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage("INFO HANDLER"));
        return path;
    }
}
