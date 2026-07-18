using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Automation;
using Excise.Core.Document;
using Xunit;

namespace Excise.Cli.Tests;

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
    /// #643 (superseding #638's fail-closed gate): redacting an encrypted
    /// source through a batch workflow succeeds by DEFAULT and re-encrypts
    /// the output with the same parameters and the step's password (here:
    /// the empty password). DECRYPT_CONFIRMATION_REQUIRED is gone — there
    /// is no longer an encryption loss to confirm.
    /// </summary>
    [Fact]
    public async Task RunAsync_BatchRedaction_EncryptedSource_ReEncryptsByDefault()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var output = Path.Combine(directory, "redacted.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        WriteWriterEncryptedFixture(input, "SECRET DATA", password: null);
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

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().NotContain("DECRYPT_CONFIRMATION_REQUIRED");
        File.Exists(output).Should().BeTrue();

        using var reopened = PdfDocument.Open(File.ReadAllBytes(output));
        reopened.IsEncrypted.Should().BeTrue(
            "an encrypted batch source must produce an encrypted output by default (#643)");
    }

    /// <summary>
    /// #643 flipped allowDecrypt's meaning: it is now the explicit opt-OUT
    /// that writes an unprotected copy (under #638 it was the opt-in
    /// required for the step to run at all).
    /// </summary>
    [Fact]
    public async Task RunAsync_BatchRedaction_EncryptedSource_WithAllowDecrypt_WritesPlaintext()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var output = Path.Combine(directory, "redacted.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        WriteWriterEncryptedFixture(input, "SECRET DATA", password: null);
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

        using var reopened = PdfDocument.Open(File.ReadAllBytes(output));
        reopened.IsEncrypted.Should().BeFalse("allowDecrypt: true is the explicit opt-out that drops protection");
    }

    /// <summary>
    /// #643: the redaction step honors the workflow's password field — a
    /// password-protected source opens with it and the output is
    /// re-encrypted with the same password.
    /// </summary>
    [Fact]
    public async Task RunAsync_BatchRedaction_PasswordProtectedSource_UsesStepPassword()
    {
        var directory = TempDirectory();
        var input = Path.Combine(directory, "input.pdf");
        var output = Path.Combine(directory, "redacted.pdf");
        var workflow = Path.Combine(directory, "workflow.json");
        WriteWriterEncryptedFixture(input, "SECRET DATA", password: "pw123");
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
                    password = "pw123",
                    confirmDestructive = true,
                },
            },
        }));

        var result = await RunCliCaptureAsync(["batch", workflow, "--json", "--progress"]);

        result.ExitCode.Should().Be(0);
        File.Exists(output).Should().BeTrue();

        using var reopened = PdfDocument.Open(File.ReadAllBytes(output), "pw123");
        reopened.IsEncrypted.Should().BeTrue("the output must stay protected by the same password (#643)");
    }

    /// <summary>
    /// A REAL excise-writer-encrypted one-page fixture (unlike
    /// <see cref="TestPdfBuilder.EncryptedSinglePageEmptyPassword"/>, whose
    /// content stream is not actually per-object encrypted).
    /// </summary>
    private static void WriteWriterEncryptedFixture(string path, string text, string? password)
    {
        using var doc = PdfDocument.Open(TestPdfBuilder.SinglePage(text));
        File.WriteAllBytes(path, doc.SaveToBytes(new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = password,
            OwnerPassword = password,
        }));
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
        var result = await RunCliCaptureAsync(["info", "/tmp/excise-missing-for-json-test.pdf", "--json"]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("File not found");
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-batch-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    private string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"excise-cli-batch-{Guid.NewGuid():N}");
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
