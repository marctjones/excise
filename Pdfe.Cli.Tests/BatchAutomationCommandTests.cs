using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using Pdfe.Cli;
using Pdfe.Core.Automation;
using Xunit;

namespace Pdfe.Cli.Tests;

public class BatchAutomationCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
            if (File.Exists(file)) try { File.Delete(file); } catch { }

        foreach (var directory in _tempDirectories)
            if (Directory.Exists(directory)) try { Directory.Delete(directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_BatchJson_RunsInfoTextAndRender_WithProgressAndReport()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var outputPng = Path.Combine(directory, "page.png");
        var workflow = Path.Combine(directory, "workflow.json");
        var report = Path.Combine(directory, "report.json");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("SECRET DATA"));
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new { id = "info", command = PdfCommandIds.DocumentInfo, input = "input.pdf" },
                new { id = "text", command = PdfCommandIds.ExtractText, input = "input.pdf", page = 1 },
                new { id = "render", command = PdfCommandIds.RenderPage, input = "input.pdf", output = "page.png", page = 1, dpi = 72 },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json", "--progress", "--output", report]);

        result.ExitCode.Should().Be(0);
        File.Exists(outputPng).Should().BeTrue();
        File.Exists(report).Should().BeTrue();
        result.StdErr.Should().Contain("\"type\":\"step-start\"");
        result.StdErr.Should().Contain("\"type\":\"step-complete\"");

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        root.GetProperty("overallStatus").GetString().Should().Be("PASS");
        root.GetProperty("passedCount").GetInt32().Should().Be(3);
        root.GetProperty("steps").GetArrayLength().Should().Be(3);

        var textResult = root.GetProperty("steps")[1].GetProperty("result");
        textResult.GetProperty("pages")[0].GetProperty("text").GetString().Should().Contain("SECRET DATA");
    }

    [Fact]
    public async Task RunAsync_BatchRedaction_RequiresConfirmation_AndDoesNotLeakPassword()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        var output = Path.Combine(directory, "redacted.pdf");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("SECRET DATA"));
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "redact",
                    command = PdfCommandIds.ApplyRedaction,
                    input = "input.pdf",
                    output = "redacted.pdf",
                    text = "SECRET",
                    password = "super-secret-password",
                },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json", "--progress"]);

        result.ExitCode.Should().Be(2);
        File.Exists(output).Should().BeFalse();
        result.StdOut.Should().Contain("DESTRUCTIVE_CONFIRMATION_REQUIRED");
        result.StdOut.Should().NotContain("super-secret-password");
        result.StdErr.Should().NotContain("super-secret-password");
    }

    /// <summary>
    /// #638: redacting an encrypted source through a batch workflow must
    /// require explicit acknowledgement (allowDecrypt: true), the same
    /// contract-violation shape as confirmDestructive, since batch/CI
    /// callers won't see a GUI dialog or read stderr.
    /// </summary>
    [Fact]
    public async Task RunAsync_BatchRedaction_EncryptedSource_RequiresAllowDecrypt()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var output = Path.Combine(directory, "redacted.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        File.WriteAllBytes(input, TestPdfBuilder.EncryptedSinglePageEmptyPassword());
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "redact",
                    command = PdfCommandIds.ApplyRedaction,
                    input = "input.pdf",
                    output = "redacted.pdf",
                    text = "SECRET",
                    confirmDestructive = true,
                },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json", "--progress"]);

        result.ExitCode.Should().Be(2);
        File.Exists(output).Should().BeFalse();
        result.StdOut.Should().Contain("DECRYPT_CONFIRMATION_REQUIRED");
    }

    [Fact]
    public async Task RunAsync_BatchRedaction_EncryptedSource_WithAllowDecrypt_Proceeds()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var output = Path.Combine(directory, "redacted.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        File.WriteAllBytes(input, TestPdfBuilder.EncryptedSinglePageEmptyPassword());
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "redact",
                    command = PdfCommandIds.ApplyRedaction,
                    input = "input.pdf",
                    output = "redacted.pdf",
                    text = "SECRET",
                    confirmDestructive = true,
                    allowDecrypt = true,
                },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json", "--progress"]);

        result.ExitCode.Should().Be(0);
        File.Exists(output).Should().BeTrue();
        result.StdOut.Should().NotContain("DECRYPT_CONFIRMATION_REQUIRED");
    }

    [Fact]
    public async Task RunAsync_BatchRedaction_RefusesInPlaceOutput()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("SECRET DATA"));
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "redact",
                    command = PdfCommandIds.ApplyRedaction,
                    input = "input.pdf",
                    output = "input.pdf",
                    text = "SECRET",
                    confirmDestructive = true,
                },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json"]);

        result.ExitCode.Should().Be(2);
        result.StdOut.Should().Contain("UNSAFE_OVERWRITE_REFUSED");
    }

    [Fact]
    public async Task RunAsync_InfoTextAndRenderJson_PrintStableShapes()
    {
        var input = TempPath(".pdf");
        var png = TempPath(".png");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("HELLO JSON"));

        var info = await RunCliCaptureAsync(["info", input, "--json"]);
        info.ExitCode.Should().Be(0);
        using (var doc = JsonDocument.Parse(info.StdOut))
        {
            doc.RootElement.GetProperty("command").GetString().Should().Be(PdfCommandIds.DocumentInfo);
            doc.RootElement.GetProperty("pageCount").GetInt32().Should().Be(1);
        }

        var text = await RunCliCaptureAsync(["text", input, "--page", "1", "--json"]);
        text.ExitCode.Should().Be(0);
        using (var doc = JsonDocument.Parse(text.StdOut))
        {
            doc.RootElement.GetProperty("command").GetString().Should().Be(PdfCommandIds.ExtractText);
            doc.RootElement.GetProperty("pages")[0].GetProperty("text").GetString().Should().Contain("HELLO JSON");
        }

        var render = await RunCliCaptureAsync(["render", input, "--output", png, "--page", "1", "--dpi", "72", "--json", "--password", "unused-secret"]);
        render.ExitCode.Should().Be(0);
        File.Exists(png).Should().BeTrue();
        render.StdOut.Should().NotContain("unused-secret");
        using (var doc = JsonDocument.Parse(render.StdOut))
        {
            doc.RootElement.GetProperty("command").GetString().Should().Be(PdfCommandIds.RenderPage);
            doc.RootElement.GetProperty("width").GetInt32().Should().BeGreaterThan(0);
            doc.RootElement.GetProperty("height").GetInt32().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task RunAsync_InfoMissingFile_ReturnsOperationFailureExitCode()
    {
        var result = await RunCliCaptureAsync(["info", "/tmp/pdfe-missing-for-json-test.pdf", "--json"]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("File not found");
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-cli-batch-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    private string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pdfe-cli-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    private static async Task<CliCaptureResult> RunCliCaptureAsync(string[] args)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);
        Environment.ExitCode = 0;
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(args);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }

        var processExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        return new CliCaptureResult(
            exitCode == 0 ? processExitCode : exitCode,
            capturedOut.ToString(),
            capturedErr.ToString());
    }

    private sealed record CliCaptureResult(int ExitCode, string StdOut, string StdErr);
}
