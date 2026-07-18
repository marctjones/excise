using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Ocr;
using Excise.Rendering;
using Excise.App.Services;
using Excise.App.ViewModels;
using SkiaSharp;
using System.Reactive.Linq;
using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// GUI wiring tests for "Make Searchable" (#658): the menu command →
/// dialog → <c>MainWindowViewModel</c> → <see cref="PdfSearchableConverter"/>
/// path. The engine itself (word matching, invisible-text layer, the
/// mutool/ghostscript-verified redaction round-trip) is already covered by
/// <c>Excise.Ocr.Tests/PdfSearchableConverterTests.cs</c> — these tests only
/// prove the GUI's orchestration: mutate the live document, mark it dirty,
/// refresh the bound viewer, and surface a friendly guard when no document
/// is loaded.
/// </summary>
public class MakeSearchableWiringTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static bool TesseractAvailable => new PdfOcrService().IsAvailable();

    /// <summary>Records whether/how <see cref="ShowMessageAsync"/> was called.</summary>
    private sealed class FakeUserDialogService : IUserDialogService
    {
        public int MessageCallCount { get; private set; }
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }

        public Task ShowMessageAsync(string title, string message)
        {
            MessageCallCount++;
            LastTitle = title;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }

    private static (MainWindowViewModel vm, FakeUserDialogService dialog) CreateViewModel()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dialog = new FakeUserDialogService();
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
        return (vm, dialog);
    }

    [Fact]
    public async Task MakeSearchableCommand_NoDocumentLoaded_ShowsFriendlyMessage_AndDoesNotThrow()
    {
        var (vm, dialog) = CreateViewModel();

        // No document loaded, and no desktop ApplicationLifetime in a
        // headless test host either way — the guard clause for "no
        // document" must fire (and must fire) before any window lookup.
        await vm.MakeSearchableCommand.Execute();

        dialog.MessageCallCount.Should().Be(1);
        dialog.LastTitle.Should().Be("Make Searchable");
        dialog.LastMessage.Should().Contain("Open a PDF");
    }

    [Fact]
    public async Task RunMakeSearchableAsync_NoDocumentLoaded_Throws()
    {
        var (vm, _) = CreateViewModel();
        var progress = new Progress<(int Done, int Total)>();

        var act = () => vm.RunMakeSearchableAsync("eng", false, progress, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunMakeSearchable_ThenOnCompleted_MakesScanSearchable_AndRefreshesTheBoundDocument()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        const string word = "REDACTME";
        var path = Path.Combine(Path.GetTempPath(), $"excise-make-searchable-gui-{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(path);
        var scanBytes = BuildScanPdf(word);
        File.WriteAllBytes(path, scanBytes);

        using (var fixtureCheck = PdfDocument.Open(scanBytes))
            fixtureCheck.GetPage(1).Letters.Should().BeEmpty(
                "fixture sanity: a scan has no real text layer before conversion");

        var (vm, _) = CreateViewModel();
        // LoadDocumentCommand is the lightweight scripting load path (no
        // thumbnails/dispatcher-bound rendering) — it deliberately does not
        // populate PdfCoreDocument, only the PdfDocumentService's document,
        // which is what RunMakeSearchableAsync mutates and
        // OnMakeSearchableCompletedAsync's RefreshAfterDocumentMutationAsync
        // call is what populates PdfCoreDocument for the first time below.
        await vm.LoadDocumentCommand(path);

        var reports = new List<(int Done, int Total)>();
        var progress = new SyncProgress<(int Done, int Total)>(reports.Add);

        var result = await vm.RunMakeSearchableAsync("eng", false, progress, CancellationToken.None);

        result.PagesProcessed.Should().Be(1);
        result.TotalWordsWritten.Should().BeGreaterThan(0,
            "tesseract must have recognized at least the one word rendered onto the scan");
        reports.Should().NotBeEmpty("progress must be reported at least once per page");
        reports[^1].Should().Be((1, 1));

        await vm.OnMakeSearchableCompletedAsync(result);

        // The GUI's job: the bound viewer document reflects the mutation
        // that RunMakeSearchableAsync applied to the live document, without
        // the caller having to reload the file from disk.
        vm.PdfCoreDocument.Should().NotBeNull();
        vm.PdfCoreDocument!.GetPage(1).Text.ToUpperInvariant().Should().Contain(word,
            "after Make Searchable completes, the page must be searchable in excise itself " +
            "(the #658/#627 acceptance criterion) without a manual reopen");
    }

    [Fact]
    public async Task RunMakeSearchable_AlreadySearchablePage_SkipsAndReportsZeroWordsWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-make-searchable-skip-{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(path);

        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank(200, 100);
            using var g = page.GetGraphics();
            g.DrawString("Already has text", PdfFont.Helvetica(12), PdfBrush.Black, 10, 50);
            g.Flush();
            doc.Save(path);
        }

        var (vm, _) = CreateViewModel();
        await vm.LoadDocumentCommand(path);

        var progress = new Progress<(int Done, int Total)>();
        var result = await vm.RunMakeSearchableAsync("eng", force: false, progress, CancellationToken.None);

        result.PagesSkipped.Should().Be(1);
        result.PagesProcessed.Should().Be(0);
        result.TotalWordsWritten.Should().Be(0);

        // A no-op run should not force a viewer refresh — OnMakeSearchableCompletedAsync
        // returns early. This should complete without throwing regardless.
        await vm.OnMakeSearchableCompletedAsync(result);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    /// <summary>
    /// A minimal, hand-written single-page PDF whose only content-stream
    /// operator is one <c>Do</c> call for a single image XObject — no text
    /// operators at all, i.e. a true scanned page. Mirrors
    /// <c>RevealHiddenTextTests.BuildRasterWithOverlayPdf</c> and
    /// <c>Excise.Ocr.Tests.PdfSearchableConverterTests.BuildTwoImagePdf</c>,
    /// simplified to a single word/image since this test only needs to
    /// prove GUI wiring, not the redaction round-trip those already cover.
    /// </summary>
    private static byte[] BuildScanPdf(string word)
    {
        byte[] rgb;
        int imgW, imgH;
        using (var src = PdfDocument.CreateNew())
        {
            var page = src.Pages.AddBlank(300, 150);
            using (var g = page.GetGraphics())
            {
                g.DrawString(word, PdfFont.Helvetica(28), PdfBrush.Black, 20, 60);
                g.Flush();
            }
            using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });
            imgW = bmp.Width;
            imgH = bmp.Height;
            rgb = new byte[imgW * imgH * 3];
            int idx = 0;
            for (int y = 0; y < imgH; y++)
            for (int x = 0; x < imgW; x++)
            {
                var c = bmp.GetPixel(x, y);
                rgb[idx++] = c.Red;
                rgb[idx++] = c.Green;
                rgb[idx++] = c.Blue;
            }
        }

        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        w.WriteLine("%PDF-1.4");
        w.Flush();
        var off = new long[6];
        off[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        off[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        off[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 150] " +
                    "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj");
        w.Flush();
        var body = "q 300 0 0 150 0 0 cm /Im0 Do Q";
        off[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream");
        w.Write(body); w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();
        off[5] = ms.Position;
        w.WriteLine($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream");
        w.Flush();
        ms.Write(rgb, 0, rgb.Length);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();
        long xref = ms.Position;
        w.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++) w.WriteLine($"{off[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }
}
