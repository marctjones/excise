using AwesomeAssertions;
using Pdfe.Cli;
using Pdfe.Core.Automation;
using Xunit;

namespace Pdfe.Cli.Tests;

public class CommandMetadataCommandTests
{
    [Fact]
    public async Task RunAsync_CommandsJson_PrintsSharedCommandMetadata()
    {
        var previousOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(["commands", "--json"]);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        exitCode.Should().Be(0);
        captured.ToString().Should().Contain(PdfCommandIds.Open);
        captured.ToString().Should().Contain(PdfCommandIds.RenderPage);
        captured.ToString().Should().Contain("\"cliCommand\": \"render\"");
    }

    [Fact]
    public async Task RunAsync_CommandsSingleId_PrintsOneCommand()
    {
        var previousOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(["commands", PdfCommandIds.ApplyRedaction]);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        exitCode.Should().Be(0);
        captured.ToString().Should().Contain("Apply Redaction");
        captured.ToString().Should().Contain("Security-sensitive", Exactly.Once());
    }

    [Fact]
    public async Task RunAsync_CommandsUnknownId_FailsClearly()
    {
        var previousErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(["commands", "missing.command"]);
        }
        finally
        {
            Console.SetError(previousErr);
        }

        exitCode.Should().Be(1);
        Environment.ExitCode.Should().Be(1);
        captured.ToString().Should().Contain("Unknown command id: missing.command");
        Environment.ExitCode = 0;
    }
}
