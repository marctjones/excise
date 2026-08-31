using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Excise.Core.Security;
using Excise.Ocr;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class MakeSearchableCommandHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"excise-make-searchable-handler-{Guid.NewGuid():N}");

    public MakeSearchableCommandHandlerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Execute_ReturnsTypedSavedOutcome_WithoutWritingPresentationChannels()
    {
        var input = WriteInput();
        var output = Path.Combine(_directory, "output.pdf");
        var converter = new FakeConverter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        MakeSearchableCommandResult result;
        try
        {
            result = MakeSearchableCommandHandler.Execute(Request(input, output), converter,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        result.InputPath.Should().Be(Path.GetFullPath(input));
        result.OutputPath.Should().Be(Path.GetFullPath(output));
        result.FirstPage.Should().Be(1);
        result.LastPage.Should().Be(1);
        result.PagesProcessed.Should().Be(1);
        result.TotalWordsWritten.Should().Be(3);
        result.EncodingGaps.Should().ContainSingle();
        converter.ProcessedPages.Should().Equal(1);
        File.Exists(output).Should().BeTrue();
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty(
            "the delivery-neutral handler returns progress and diagnostics for the command adapter");
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotProbeConverterOrWriteOutput()
    {
        var converter = new FakeConverter();
        var output = Path.Combine(_directory, "output.pdf");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => MakeSearchableCommandHandler.Execute(
            Request(Path.Combine(_directory, "missing.pdf"), output), converter, cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        converter.AvailabilityProbeCount.Should().Be(0);
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Execute_PageOutsideDocument_ThrowsTypedRangeFailureBeforeConversionOrSave()
    {
        var input = WriteInput();
        var output = Path.Combine(_directory, "output.pdf");
        var converter = new FakeConverter();

        var act = () => MakeSearchableCommandHandler.Execute(
            Request(input, output, pageNumber: 2), converter, TestContext.Current.CancellationToken);

        act.Should().Throw<DocumentPageOutOfRangeException>();
        converter.ProcessedPages.Should().BeEmpty();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Execute_SameInputAndOutputPath_DetachesBeforeSaving()
    {
        var path = WriteInput();

        var result = MakeSearchableCommandHandler.Execute(Request(path, path), new FakeConverter(),
            TestContext.Current.CancellationToken);

        result.OutputPath.Should().Be(Path.GetFullPath(path));
        using var saved = PdfDocument.Open(path);
        saved.GetPage(1).Text.Should().Contain("SEARCHABLE INPUT",
            "same-path OCR output must not retain a source handle over its destination");
    }

    [Fact]
    public void Execute_EmptyPasswordEncryptedInput_ReEncryptsSavedOutput()
    {
        var input = Path.Combine(_directory, "encrypted-input.pdf");
        var output = Path.Combine(_directory, "encrypted-output.pdf");
        using (var document = PdfDocument.Open(TestPdfBuilder.SinglePage("ENCRYPTED INPUT")))
        {
            File.WriteAllBytes(input, document.SaveToBytes(new PdfEncryptionOptions
            {
                UserPassword = null,
                OwnerPassword = null,
                Permissions = -4,
            }));
        }

        MakeSearchableCommandHandler.Execute(Request(input, output), new FakeConverter(),
            TestContext.Current.CancellationToken);

        using var saved = PdfDocument.Open(output);
        saved.IsEncrypted.Should().BeTrue(
            "make-searchable has the same default re-encryption policy as the pre-extraction command (#643)");
    }

    [Fact]
    public async Task RunAsync_MissingInput_PreservesTextErrorAndExitCode()
    {
        var missing = Path.Combine(_directory, "missing.pdf");
        var output = Path.Combine(_directory, "output.pdf");
        var previousError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            var exitCode = await Program.RunAsync(["make-searchable", missing, output]);

            exitCode.Should().Be(1);
            capturedError.ToString().Should().Contain($"File not found: {missing}");
        }
        finally
        {
            Console.SetError(previousError);
            Environment.ExitCode = 0;
        }
    }

    [Fact]
    public async Task RunAsync_TextPage_PreservesHumanOutputAndExitCode()
    {
        Assert.SkipUnless(new PdfOcrService().IsAvailable(), "tesseract not installed");
        var input = WriteInput();
        var output = Path.Combine(_directory, "output.pdf");
        var previousOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        try
        {
            var exitCode = await Program.RunAsync(["make-searchable", input, output]);

            exitCode.Should().Be(0);
        }
        finally
        {
            Console.SetOut(previousOut);
            Environment.ExitCode = 0;
        }

        capturedOut.ToString().Should().Contain("Page 1/1: skipped (already has a text layer)");
        capturedOut.ToString().Should().Contain("Processed 0 page(s), skipped 1, wrote 0 word(s).");
        capturedOut.ToString().Should().Contain($"Output: {Path.GetFullPath(output)}");
        File.Exists(output).Should().BeTrue();
    }

    private string WriteInput()
    {
        var input = Path.Combine(_directory, "input.pdf");
        File.WriteAllBytes(input, TestPdfBuilder.SinglePage("SEARCHABLE INPUT"));
        return input;
    }

    private static MakeSearchableCommandRequest Request(string input, string output, int? pageNumber = null)
        => new(input, output, pageNumber, Dpi: 300, Language: "eng", TessdataPrefix: null, Force: false);

    private sealed class FakeConverter : ISearchablePageConverter
    {
        internal int AvailabilityProbeCount { get; private set; }
        internal List<int> ProcessedPages { get; } = [];

        public bool IsAvailable()
        {
            AvailabilityProbeCount++;
            return true;
        }

        public SearchablePageResult MakePageSearchable(Excise.Core.Document.PdfPage page, bool force)
        {
            ProcessedPages.Add(page.PageNumber);
            return new SearchablePageResult(
                page.PageNumber,
                Skipped: false,
                AlreadyHadText: false,
                WordsWritten: 3,
                WordsSkippedEncoding: 1);
        }
    }
}
