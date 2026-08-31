using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Authoring;
using Excise.Core.Validation;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class ValidationHandlerTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(
        Path.GetTempPath(),
        $"excise-validation-handler-{Guid.NewGuid():N}.pdf");

    public ValidationHandlerTests()
        => File.WriteAllBytes(_pdfPath, TestPdfBuilder.SinglePage("VALIDATION"));

    public void Dispose()
    {
        if (File.Exists(_pdfPath))
            File.Delete(_pdfPath);
    }

    [Fact]
    public void Execute_AlwaysRunsBoundedPdfUaValidation()
    {
        var result = ValidationHandler.Execute(
            new ValidationRequest(_pdfPath, Password: null, PdfAConformance: null),
            TestContext.Current.CancellationToken);

        result.FilePath.Should().Be(Path.GetFullPath(_pdfPath));
        result.Reports.Should().ContainSingle();
        result.Reports[0].Standard.Should().Be(ConformanceStandard.PdfUA1);
        result.CheckedSubsetConformant.Should().Be(
            result.Reports.All(report => report.CheckedSubsetConformant));
    }

    [Fact]
    public void Execute_RequestedPdfAValidation_IsIncludedInTypedResult()
    {
        var result = ValidationHandler.Execute(
            new ValidationRequest(_pdfPath, Password: null, PdfAConformance.PdfA1B),
            TestContext.Current.CancellationToken);

        result.Reports.Should().HaveCount(2);
        result.Reports.Select(report => report.Standard).Should().Equal(
            ConformanceStandard.PdfUA1,
            ConformanceStandard.PdfA1B);
    }

    [Fact]
    public void Execute_CanceledRequest_StopsBeforeOpeningDocument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => ValidationHandler.Execute(
            new ValidationRequest(_pdfPath, Password: null, PdfAConformance: null),
            cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_InvalidPdfALevel_ReturnsCleanOperationFailure()
    {
        var result = await RunCliCaptureAsync(["validate", _pdfPath, "--pdfa", "3z"]);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Error: invalid --pdfa level");
    }

    [Fact]
    public async Task RunAsync_JsonReport_PreservesBoundedCheckerShape()
    {
        var result = await RunCliCaptureAsync(["validate", _pdfPath, "--json"]);

        result.ExitCode.Should().Be(1, "the deliberately untagged fixture is not PDF/UA conformant");
        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;
        root.GetProperty("command").GetString().Should().Be("validate");
        root.GetProperty("status").GetString().Should().Be("FAIL");
        root.GetProperty("reports")[0].GetProperty("standard").GetString().Should().Be("PdfUA1");
        root.GetProperty("note").GetString().Should().Contain("not a full ISO conformance verdict");
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
