using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class RenderPageHandlerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-render-handler-{Guid.NewGuid():N}");

    public RenderPageHandlerTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Execute_RendersPngAndCreatesOutputDirectory()
    {
        var input = WritePdf("render.pdf");
        var output = Path.Combine(_tempDirectory, "nested", "page.png");

        var result = RenderPageHandler.Execute(
            Request(input, output),
            TestContext.Current.CancellationToken);

        result.InputPath.Should().Be(Path.GetFullPath(input));
        result.OutputPath.Should().Be(Path.GetFullPath(output));
        result.PageNumber.Should().Be(1);
        result.Dpi.Should().Be(72);
        result.Width.Should().Be(612);
        result.Height.Should().Be(792);
        File.ReadAllBytes(output).Should().StartWith([0x89, 0x50, 0x4E, 0x47]);
    }

    [Fact]
    public void Execute_PageOutsideDocument_DoesNotCreateOutput()
    {
        var input = WritePdf("range.pdf");
        var output = Path.Combine(_tempDirectory, "range.png");

        var act = () => RenderPageHandler.Execute(Request(input, output, pageNumber: 2));

        act.Should().Throw<DocumentPageOutOfRangeException>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Execute_CanceledRequest_DoesNotCreateOutput()
    {
        var input = WritePdf("canceled.pdf");
        var output = Path.Combine(_tempDirectory, "canceled.png");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => RenderPageHandler.Execute(
            Request(input, output),
            cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
    }

    private static RenderPageRequest Request(
        string input,
        string output,
        int pageNumber = 1)
        => new(
            input,
            output,
            Password: null,
            PageNumber: pageNumber,
            Dpi: 72,
            IgnorePermissions: false);

    private string WritePdf(string fileName)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage("RENDER HANDLER"));
        return path;
    }
}
