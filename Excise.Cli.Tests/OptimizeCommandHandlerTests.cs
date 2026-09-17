using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Excise.Core.Security;
using Excise.Core.Writing;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>#1550 — <c>excise optimize</c>.</summary>
public sealed class OptimizeCommandHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"excise-optimize-handler-{Guid.NewGuid():N}");

    public OptimizeCommandHandlerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// A page whose content stream is stored uncompressed, which is what the
    /// hand-built <see cref="TestPdfBuilder"/> writes — so Lossless has
    /// something to shrink.
    /// </summary>
    private string WriteInput(string name = "input.pdf", string text = "OPTIMIZE ME")
    {
        var path = Path.Combine(_directory, name);
        var suffix = new StringBuilder();
        for (var i = 0; i < 200; i++)
            suffix.Append("BT /F1 8 Tf 20 ").Append(20 + i * 3).Append(" Td (filler line for compression) Tj ET\n");
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage(text, contentSuffix: suffix.ToString()));
        return path;
    }

    [Fact]
    public void Execute_Lossless_WritesASmallerCopy_AndLeavesTheInputAlone()
    {
        var input = WriteInput();
        var before = File.ReadAllBytes(input);
        var output = Path.Combine(_directory, "output.pdf");

        var result = OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(input, output, PdfOptimizationPreset.Lossless),
            TestContext.Current.CancellationToken);

        File.ReadAllBytes(input).Should().Equal(before, "the original is never touched");
        result.OutputPath.Should().Be(Path.GetFullPath(output));
        result.InputSizeBytes.Should().Be(before.Length);
        result.OutputSizeBytes.Should().Be(new FileInfo(output).Length);
        result.OutputSizeBytes.Should().BeLessThan(result.InputSizeBytes);
        result.Optimization.StreamsRecompressed.Should().BeGreaterThan(0);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), "OPTIMIZE ME").Should().NotBeEmpty(
            "the page text is still in the file, inside a now-compressed stream");
    }

    [Fact]
    public void Execute_SameInputAndOutputPath_IsRefusedWithoutWriting()
    {
        var input = WriteInput();
        var before = File.ReadAllBytes(input);

        var act = () => OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(input, input, PdfOptimizationPreset.Screen),
            TestContext.Current.CancellationToken);

        act.Should().Throw<InvalidOperationException>().WithMessage("*always writes a new file*");
        File.ReadAllBytes(input).Should().Equal(before);
    }

    [Fact]
    public void Execute_PreCancelled_WritesNothing()
    {
        var output = Path.Combine(_directory, "output.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(WriteInput(), output, PdfOptimizationPreset.Lossless),
            cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Execute_EncryptedInput_StaysEncrypted_UnlessAllowDecrypt()
    {
        var input = Path.Combine(_directory, "encrypted.pdf");
        using (var document = PdfDocument.Open(File.ReadAllBytes(WriteInput("plain.pdf"))))
        {
            File.WriteAllBytes(input, document.SaveToBytes(new PdfEncryptionOptions
            {
                UserPassword = "pw",
                OwnerPassword = "pw",
            }));
        }

        var kept = Path.Combine(_directory, "kept.pdf");
        var keptResult = OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(input, kept, PdfOptimizationPreset.Lossless, Password: "pw"),
            TestContext.Current.CancellationToken);
        keptResult.Diagnostics.Should().Contain(d => d.Contains("re-encrypted"));
        using (var reopened = PdfDocument.Open(kept, "pw"))
            reopened.IsEncrypted.Should().BeTrue();

        var dropped = Path.Combine(_directory, "dropped.pdf");
        var droppedResult = OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(input, dropped, PdfOptimizationPreset.Lossless, Password: "pw", AllowDecrypt: true),
            TestContext.Current.CancellationToken);
        droppedResult.Diagnostics.Should().Contain(d => d.Contains("NOT be encrypted"));
        using (var reopened = PdfDocument.Open(dropped))
            reopened.IsEncrypted.Should().BeFalse();
    }

    [Fact]
    public void JsonReport_CarriesBothSizesAndThePreset()
    {
        var input = WriteInput();
        var output = Path.Combine(_directory, "output.pdf");
        var result = OptimizeCommandHandler.Execute(
            new OptimizeCommandRequest(input, output, PdfOptimizationPreset.Standard),
            TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(
            OptimizeCommand.ToJson(result), CliJsonContext.Default.OptimizeJsonReport);

        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("preset").GetString().Should().Be("standard");
        parsed.RootElement.GetProperty("inputSizeBytes").GetInt64().Should().Be(result.InputSizeBytes);
        parsed.RootElement.GetProperty("outputSizeBytes").GetInt64().Should().Be(result.OutputSizeBytes);
    }

    [Theory]
    [InlineData(1.0, "0.0%")]
    [InlineData(0.25, "-75.0%")]
    [InlineData(1.5, "+50.0%")]
    public void FormatChange_ShowsTheSignedPercentage(double ratio, string expected)
        => OptimizeCommand.FormatChange(ratio).Should().Be(expected);
}
