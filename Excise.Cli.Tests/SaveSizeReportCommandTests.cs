using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli;
using Xunit;

namespace Excise.Cli.Tests;

public class SaveSizeReportCommandTests : IDisposable
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
    public async Task RunAsync_SaveSizeReportJson_ReportsSizesAndLatencyForMultipleFiles()
    {
        var first = TempPdf("first.pdf", "First report text");
        var second = TempPdf("second.pdf", "Second report text");

        var result = await RunCliCaptureAsync(["save-size-report", first, second, "--max-ratio", "10"]);

        result.ExitCode.Should().Be(0);
        result.StdErr.Should().BeEmpty();
        using var json = JsonDocument.Parse(result.StdOut);
        var root = json.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("command").GetString().Should().Be("save-size-report");
        root.GetProperty("overallStatus").GetString().Should().Be("PASS");
        root.GetProperty("files").GetArrayLength().Should().Be(2);

        var entry = root.GetProperty("files")[0];
        entry.GetProperty("status").GetString().Should().Be("PASS");
        entry.GetProperty("originalSizeBytes").GetInt64().Should().BeGreaterThan(0);
        entry.GetProperty("savedSizeBytes").GetInt32().Should().BeGreaterThan(0);
        entry.GetProperty("sizeRatio").GetDouble().Should().BeGreaterThan(0);
        entry.GetProperty("openMilliseconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        entry.GetProperty("saveMilliseconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        entry.GetProperty("pageCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_SaveSizeReportOutput_WritesSameJsonToFile()
    {
        var input = TempPdf("input.pdf", "Output report text");
        var report = TempPath("report.json");

        var result = await RunCliCaptureAsync(["save-size-report", input, "--output", report, "--max-ratio", "10"]);

        result.ExitCode.Should().Be(0);
        File.Exists(report).Should().BeTrue();
        File.ReadAllText(report).Should().Be(result.StdOut.TrimEnd());
    }

    [Fact]
    public async Task RunAsync_SaveSizeReportThresholdFailure_ReturnsNonZero()
    {
        var input = TempPdf("input.pdf", "Threshold report text");

        var result = await RunCliCaptureAsync(["save-size-report", input, "--max-ratio", "0.01"]);

        result.ExitCode.Should().Be(1);
        using var json = JsonDocument.Parse(result.StdOut);
        json.RootElement.GetProperty("overallStatus").GetString().Should().Be("FAIL");
        json.RootElement.GetProperty("files")[0].GetProperty("status").GetString().Should().Be("FAIL");
    }

    private string TempPdf(string fileName, string text)
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage(text));
        _tempFiles.Add(path);
        return path;
    }

    private string TempPath(string fileName)
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, fileName);
        _tempFiles.Add(path);
        return path;
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-save-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliCaptureAsync(string[] args)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await Program.RunAsync(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Environment.ExitCode = 0;
        }
    }
}
