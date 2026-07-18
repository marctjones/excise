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

[Collection("AvaloniaTests")]
public class MainWindowRenderSchedulingTests
{
    [FixedAvaloniaFact]
    public async Task RenderCurrentPageAsync_DropsStaleRenderCompletion()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-render-race-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        try
        {
            var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
            documentService.LoadDocument(pdfPath);
            var renderService = new ControlledRenderService();
            var vm = CreateViewModel(documentService, renderService);
            vm.AdjacentPagePrefetchEnabled = false;
            SetPrivateField(vm, "_currentFilePath", pdfPath);

            vm.CurrentPageIndex = 0;
            var firstRender = InvokeRenderCurrentPageAsync(vm);
            await renderService.WaitForRequestAsync(0);

            vm.CurrentPageIndex = 1;
            var secondRender = InvokeRenderCurrentPageAsync(vm);
            await renderService.WaitForRequestAsync(1);

            renderService.Complete(0, CreateBitmap(width: 10, height: 10, SKColors.Red));
            await firstRender;

            vm.CurrentPageImage.Should().BeNull("a stale render must not update the visible page");
            renderService.RequestToken(0).IsCancellationRequested.Should().BeTrue();

            renderService.Complete(1, CreateBitmap(width: 20, height: 10, SKColors.Blue));
            await secondRender;

            vm.CurrentPageImage.Should().NotBeNull();
            vm.CurrentPageImage!.PixelSize.Width.Should().Be(20);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    [FixedAvaloniaFact]
    public async Task RenderCurrentPageAsync_PrefetchesAdjacentPagesAfterVisiblePageWins()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-render-prefetch-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);

        try
        {
            var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
            documentService.LoadDocument(pdfPath);
            var renderService = new ControlledRenderService();
            var vm = CreateViewModel(documentService, renderService);
            SetPrivateField(vm, "_currentFilePath", pdfPath);

            vm.CurrentPageIndex = 1;
            var render = InvokeRenderCurrentPageAsync(vm);
            await renderService.WaitForRequestAsync(1);

            renderService.Complete(1, CreateBitmap(width: 20, height: 10, SKColors.Blue));
            await render;

            vm.CurrentPageImage.Should().NotBeNull("the visible page should be committed before prefetch starts");
            await renderService.WaitForRequestAsync(2);
            renderService.RequestToken(2).IsCancellationRequested.Should().BeFalse();
            renderService.Complete(2, CreateBitmap(width: 10, height: 10, SKColors.Green));

            await renderService.WaitForRequestAsync(0);
            renderService.RequestToken(0).IsCancellationRequested.Should().BeFalse();
            renderService.Complete(0, CreateBitmap(width: 10, height: 10, SKColors.Red));
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

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

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"field {fieldName} should exist");
        field!.SetValue(target, value);
    }

    private static Task InvokeRenderCurrentPageAsync(MainWindowViewModel viewModel)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "RenderCurrentPageAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("render scheduling should remain testable");
        return (Task)method!.Invoke(viewModel, null)!;
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
