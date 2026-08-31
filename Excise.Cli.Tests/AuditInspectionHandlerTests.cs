using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Excise.Ocr;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class AuditInspectionHandlerTests : IDisposable
{
    private readonly string _cleanPdfPath = Path.Combine(
        Path.GetTempPath(),
        $"excise-audit-clean-{Guid.NewGuid():N}.pdf");
    private readonly string _coveredPdfPath = Path.Combine(
        Path.GetTempPath(),
        $"excise-audit-covered-{Guid.NewGuid():N}.pdf");

    public AuditInspectionHandlerTests()
    {
        File.WriteAllBytes(_cleanPdfPath, TestPdfBuilder.SinglePage("VISIBLE"));
        File.WriteAllBytes(
            _coveredPdfPath,
            TestPdfBuilder.SinglePage(
                "SECRET",
                contentSuffix: "0 0 0 rg 90 680 200 40 re f"));
    }

    public void Dispose()
    {
        if (File.Exists(_cleanPdfPath)) File.Delete(_cleanPdfPath);
        if (File.Exists(_coveredPdfPath)) File.Delete(_coveredPdfPath);
    }

    [Fact]
    public void Execute_ShallowAudit_ReturnsStructuralLeaksWithoutOcrProbe()
    {
        var scanner = new FakeDifferentialScanner(isAvailable: true);

        var result = AuditInspectionHandler.Execute(
            new AuditInspectionRequest(_coveredPdfPath, Password: null, Deep: false),
            scanner,
            TestContext.Current.CancellationToken);

        result.StructuralHits.Should().Contain(hit => hit.Text.Contains("SECRET"));
        result.DifferentialOcrHits.Should().BeEmpty();
        result.TotalHitCount.Should().Be(result.StructuralHits.Count);
        scanner.AvailabilityProbeCount.Should().Be(0);
    }

    [Fact]
    public void Execute_DeepAudit_UsesInjectedDifferentialScanner()
    {
        var scanner = new FakeDifferentialScanner(isAvailable: true);

        var result = AuditInspectionHandler.Execute(
            new AuditInspectionRequest(_cleanPdfPath, Password: null, Deep: true),
            scanner,
            TestContext.Current.CancellationToken);

        result.StructuralHits.Should().BeEmpty();
        result.DifferentialOcrHits.Should().ContainSingle();
        result.DifferentialOcrHits[0].Text.Should().Be("RASTER SECRET");
        scanner.AvailabilityProbeCount.Should().Be(1);
        scanner.ScanCount.Should().Be(1);
    }

    [Fact]
    public void Execute_DeepAuditWithoutRecognizer_ThrowsTypedAvailabilityFailure()
    {
        var scanner = new FakeDifferentialScanner(isAvailable: false);

        var act = () => AuditInspectionHandler.Execute(
            new AuditInspectionRequest(_cleanPdfPath, Password: null, Deep: true),
            scanner);

        act.Should().Throw<DeepAuditUnavailableException>();
        scanner.ScanCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ShallowJson_PreservesAuditSchemaAndFindingExitCode()
    {
        var result = await RunCliCaptureAsync(["audit", _coveredPdfPath, "--json"]);

        result.ExitCode.Should().Be(2);
        using var document = JsonDocument.Parse(result.StdOut);
        var root = document.RootElement;
        root.GetProperty("structural").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("structural")[0].TryGetProperty("hidden_by", out _).Should().BeTrue();
        root.GetProperty("differential_ocr").GetArrayLength().Should().Be(0);
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

    private sealed class FakeDifferentialScanner(bool isAvailable) : IDifferentialOcrScanner
    {
        internal int AvailabilityProbeCount { get; private set; }

        internal int ScanCount { get; private set; }

        public bool IsAvailable()
        {
            AvailabilityProbeCount++;
            return isAvailable;
        }

        public IReadOnlyList<DifferentialOcrHit> Scan(byte[] pdfBytes)
        {
            pdfBytes.Should().NotBeEmpty();
            ScanCount++;
            return
            [
                new DifferentialOcrHit(
                    1,
                    "RASTER SECRET",
                    new PdfRectangle(10, 20, 30, 40),
                    0.95f),
            ];
        }
    }

    private sealed record CliCaptureResult(int ExitCode, string StdOut, string StdErr);
}
