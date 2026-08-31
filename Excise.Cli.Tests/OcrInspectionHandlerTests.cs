using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Xunit;

namespace Excise.Cli.Tests;

public sealed class OcrInspectionHandlerTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(
        Path.GetTempPath(),
        $"excise-ocr-handler-{Guid.NewGuid():N}.pdf");

    public OcrInspectionHandlerTests()
        => File.WriteAllBytes(_pdfPath, TestPdfBuilder.SinglePage("OCR INPUT"));

    public void Dispose()
    {
        if (File.Exists(_pdfPath))
            File.Delete(_pdfPath);
    }

    [Fact]
    public void Execute_UsesInjectedRecognizerAndReturnsTypedPageText()
    {
        var recognizer = new FakeRecognizer(isAvailable: true);

        var result = OcrInspectionHandler.Execute(
            Request(pageNumber: 1),
            recognizer,
            TestContext.Current.CancellationToken);

        result.FilePath.Should().Be(Path.GetFullPath(_pdfPath));
        result.PageCount.Should().Be(1);
        result.Pages.Should().Equal(new OcrTextPageResult(1, "OCR PAGE 1"));
        recognizer.RecognizedPages.Should().Equal(1);
    }

    [Fact]
    public void Execute_UnavailableRecognizer_FailsBeforeOpeningPages()
    {
        var recognizer = new FakeRecognizer(isAvailable: false);

        var act = () => OcrInspectionHandler.Execute(Request(), recognizer);

        act.Should().Throw<OcrUnavailableException>();
        recognizer.RecognizedPages.Should().BeEmpty();
    }

    [Fact]
    public void Execute_PageOutsideDocument_ThrowsTypedRangeFailure()
    {
        var recognizer = new FakeRecognizer(isAvailable: true);

        var act = () => OcrInspectionHandler.Execute(Request(pageNumber: 2), recognizer);

        act.Should().Throw<DocumentPageOutOfRangeException>();
        recognizer.RecognizedPages.Should().BeEmpty();
    }

    [Fact]
    public void Execute_CanceledRequest_DoesNotProbeRecognizer()
    {
        var recognizer = new FakeRecognizer(isAvailable: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => OcrInspectionHandler.Execute(
            Request(),
            recognizer,
            cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        recognizer.AvailabilityProbeCount.Should().Be(0);
    }

    private OcrInspectionRequest Request(int? pageNumber = null)
        => new(
            _pdfPath,
            PageNumber: pageNumber,
            Dpi: 300,
            Language: "eng",
            TessdataPrefix: null,
            IgnorePermissions: false,
            ForAccessibility: false);

    private sealed class FakeRecognizer(bool isAvailable) : IOcrTextRecognizer
    {
        internal int AvailabilityProbeCount { get; private set; }

        internal List<int> RecognizedPages { get; } = [];

        public bool IsAvailable()
        {
            AvailabilityProbeCount++;
            return isAvailable;
        }

        public string RecognizeText(PdfPage page)
        {
            RecognizedPages.Add(page.PageNumber);
            return $"OCR PAGE {page.PageNumber}";
        }
    }
}
