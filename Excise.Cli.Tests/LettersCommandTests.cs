using AwesomeAssertions;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class LettersCommandTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(
        Path.GetTempPath(),
        $"excise-letters-command-{Guid.NewGuid():N}.pdf");

    public LettersCommandTests()
        => File.WriteAllBytes(_pdfPath, TestPdfBuilder.SinglePage("LETTERS COMMAND"));

    public void Dispose()
    {
        if (File.Exists(_pdfPath))
            File.Delete(_pdfPath);
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsOperationFailure()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"excise-letters-missing-{Guid.NewGuid():N}.pdf");

        var result = await RunCliCaptureAsync(["letters", missingPath]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("File not found");
    }

    [Fact]
    public async Task RunAsync_PageOutsideDocument_ReturnsOperationFailure()
    {
        var result = await RunCliCaptureAsync(["letters", _pdfPath, "--page", "2"]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Invalid page number");
    }

    [Fact]
    public async Task RunAsync_NegativeLimit_ReturnsOperationFailure()
    {
        var result = await RunCliCaptureAsync(["letters", _pdfPath, "--limit", "-1"]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Invalid limit");
    }

    private static async Task<CliCaptureResult> RunCliCaptureAsync(string[] args)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);
        try
        {
            var exitCode = await Program.RunAsync(args);
            return new CliCaptureResult(exitCode, capturedOut.ToString(), capturedErr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Environment.ExitCode = 0;
        }
    }

    private sealed record CliCaptureResult(int ExitCode, string StdOut, string StdErr);
}
