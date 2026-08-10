using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The bound <c>PdfViewerControl</c> owns display rendering; the ViewModel does
/// not render.
///
/// This class used to be MainWindowRenderSchedulingTests and carried two more
/// tests, which drove <c>MainWindowViewModel.RenderCurrentPageAsync</c> through
/// reflection. That method had no production callers — the legacy VM render
/// path was bypassed and left in place — so those two asserted scheduling
/// guarantees for a renderer the user never invoked, and they were removed with
/// it (#920).
///
/// What remains is the guard that made the deletion safe, and it is the reason
/// this file still exists: if the ViewModel ever starts rendering again, this
/// fails.
/// </summary>
[Collection("AvaloniaTests")]
public class ViewerOwnsDisplayRenderingTests
{
    [FixedAvaloniaFact]
    public async Task LoadAndNavigate_DoNotInvokeLegacyViewModelRenderService()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-viewer-owned-render-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);

        try
        {
            var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
            var renderService = new ControlledRenderService();
            var vm = CreateViewModel(documentService, renderService);

            await vm.LoadDocumentAsync(pdfPath);

            renderService.RenderCallCount.Should().Be(
                0,
                "the bound PdfViewerControl owns display rendering; the VM should not render an unbound CurrentPageImage during document open");

            vm.CurrentPageIndex = 1;
            vm.CurrentPageIndex = 2;

            renderService.RenderCallCount.Should().Be(
                0,
                "page navigation should update CurrentPage and let PdfViewerControl render through its binding");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    private static MainWindowViewModel CreateViewModel(PdfDocumentService documentService, PdfRenderService renderService)
    {
        return new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            NullLoggerFactory.Instance,
            documentService,
            renderService,
            new RedactionService(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService());
    }



    private static SKBitmap CreateBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private sealed class ControlledRenderService : PdfRenderService
    {
        private readonly ConcurrentDictionary<int, RenderRequest> _requests = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> _arrivals = new();
        private int _renderCallCount;

        public ControlledRenderService()
            : base(NullLogger<PdfRenderService>.Instance)
        {
        }

        public override Task<SKBitmap?> RenderPageAsync(
            string pdfPath,
            int pageIndex,
            int dpi = 150,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renderCallCount);
            var request = new RenderRequest(cancellationToken);
            _requests[pageIndex] = request;
            _arrivals.GetOrAdd(pageIndex, _ => NewArrival()).TrySetResult(null);
            return request.Completion.Task;
        }

        public int RenderCallCount => Volatile.Read(ref _renderCallCount);

        public Task WaitForRequestAsync(int pageIndex) =>
            _arrivals.GetOrAdd(pageIndex, _ => NewArrival()).Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Complete(int pageIndex, SKBitmap bitmap)
        {
            _requests.TryGetValue(pageIndex, out var request).Should().BeTrue();
            request!.Completion.TrySetResult(bitmap).Should().BeTrue();
        }

        public CancellationToken RequestToken(int pageIndex)
        {
            _requests.TryGetValue(pageIndex, out var request).Should().BeTrue();
            return request!.CancellationToken;
        }

        private static TaskCompletionSource<object?> NewArrival() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record RenderRequest(CancellationToken CancellationToken)
        {
            public TaskCompletionSource<SKBitmap?> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
