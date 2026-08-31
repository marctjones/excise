using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class RedactCommandHandlerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"excise-redact-handler-{Guid.NewGuid():N}");

    public RedactCommandHandlerTests()
        => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Execute_ReturnsTypedOutcome_WithoutWritingPresentationChannels()
    {
        var input = Path.Combine(_tempDirectory, "input.pdf");
        var output = Path.Combine(_tempDirectory, "output.pdf");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("SECRET PUBLIC"));

        var previousOut = Console.Out;
        var previousError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        RedactCommandResult result;
        try
        {
            result = RedactCommandHandler.Execute(new RedactCommandRequest(
                input,
                output,
                "SECRET"),
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        result.Count.Should().Be(1);
        result.InputPath.Should().Be(Path.GetFullPath(input));
        result.OutputPath.Should().Be(Path.GetFullPath(output));
        result.Flattened.Should().BeFalse();
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty(
            "the handler returns diagnostics and leaves presentation to its caller");
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotOpenOrWriteFiles()
    {
        var output = Path.Combine(_tempDirectory, "output.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => RedactCommandHandler.Execute(new RedactCommandRequest(
            Path.Combine(_tempDirectory, "missing.pdf"),
            output,
            "SECRET"),
            cancellationToken: cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Execute_RejectsConflictingFlattenAndStructuralOptions()
    {
        var act = () => RedactCommandHandler.Execute(new RedactCommandRequest(
            Path.Combine(_tempDirectory, "input.pdf"),
            Path.Combine(_tempDirectory, "output.pdf"),
            "SECRET",
            DrawBox: false,
            FlattenOcr: true));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*flatten-ocr*");
    }
}
