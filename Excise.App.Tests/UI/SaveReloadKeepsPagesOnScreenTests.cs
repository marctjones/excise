using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Graphics;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1876: a save in the continuous view reopens the document it just wrote, and
/// the visible page stays on screen until the reopened document has rendered it
/// (it used to go blank for a full re-render). Every other document change still
/// drops the page at once: a stale frame shown for different content is a worse
/// bug than a blank one.
/// </summary>
/// <remarks>
/// The watch is event-driven (the slot's Bitmap, the items' source, the viewer's
/// Document), not a poll after the save returns: the blank began synchronously
/// inside the save.
/// </remarks>
[Collection("AvaloniaTests")]
public class SaveReloadKeepsPagesOnScreenTests
{
    private const int Page = 2;

    [FixedAvaloniaTheory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)] // #1877: a plain Save reopens the document too
    public async Task Save_InContinuousView_KeepsThePageOnScreen_UntilTheReopenedDocumentRendersIt(bool plainSave)
    {
        using var s = await Session.OpenAsync();
        // Read from inside the page, not at its top: a rebuild jumped back to the top.
        var scroller = s.Viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
        scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y + 300);
        await AnnotationPlacementAccuracyTests.WaitForIdleLayout(s.Window);
        await AnnotationPlacementAccuracyTests.WaitForContinuousPageRendered(s.Window, s.Viewer, Page);
        await AnnotationPlacementAccuracyTests.WaitForIdleLayout(s.Window);
        var readingAt = scroller.Offset.Y;
        var shown = s.Composite()!;
        int renders = s.Viewer.ContinuousRenderStartCount;
        using var before = AnnotationPlacementAccuracyTests.Capture(s.Window, s.Viewer);
        using var watch = new CompositeWatch(s.Viewer, Page);

        if (plainSave)
            await s.Vm.SaveFileCommand.Execute();
        else
            await s.Vm.SaveFileAsAsync(s.Output);
        s.Window.UpdateLayout();
        using var rightAfterSave = AnnotationPlacementAccuracyTests.Capture(s.Window, s.Viewer);
        var rerendered = await s.WaitForComposite(c => !ReferenceEquals(c, shown) && s.Viewer.ContinuousRenderStartCount > renders);
        using var after = AnnotationPlacementAccuracyTests.Capture(s.Window, s.Viewer);

        watch.DocumentChanges.Should().ContainSingle("the save reopens the document once");
        watch.EverAbsent.Should().BeFalse($"page {Page} must stay on screen across the save: {watch}");
        rerendered.Should().NotBeSameAs(shown, "the reopened document must render the page itself");
        scroller.Offset.Y.Should().BeApproximately(readingAt, 1, "the save keeps the reading position");
        Diff(before, rightAfterSave).Should().BeLessThan(0.005, "the frame right after the save still shows the page");
        Diff(before, after).Should().BeLessThan(0.005, "the reopened document renders the page it replaced");
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task SaveAs_ThatFlattensPendingTypewriterText_DropsThePage_BecauseTheSavedContentDiffers()
    {
        using var s = await Session.OpenAsync();
        s.Vm.OnTypewriterTextCreated(new PdfRectangle(72, 100, 300, 140), Page);
        s.Vm.OnTypewriterTextEdited(s.Vm.TypewriterTextOperations.Single().Id, "flattened on save", Page);
        await AnnotationPlacementAccuracyTests.WaitForContinuousPageRendered(s.Window, s.Viewer, Page);
        s.Viewer.ViewMode.Should().Be(PdfViewMode.Continuous, "fixture: the save must happen in the continuous view");
        using var watch = new CompositeWatch(s.Viewer, Page);

        await s.Vm.SaveFileAsAsync(s.Output);

        watch.DocumentChanges.Should().ContainSingle().Which.Present.Should().BeFalse(
            $"the pages drawn before the save lack the flattened text: {watch}");
        await AnnotationPlacementAccuracyTests.WaitForContinuousPageRendered(s.Window, s.Viewer, Page);
    }

    [FixedAvaloniaTheory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningADocument_DropsThePage_EvenTheSameFileChangedOnDisk(bool sameFileChangedOnDisk)
    {
        using var s = await Session.OpenAsync();
        var path = sameFileChangedOnDisk ? s.Source : Path.Combine(s.Dir, "other.pdf");
        var written = Path.Combine(s.Dir, "written.pdf");
        CreateFixture(written, boxShiftPt: 150);
        File.Move(written, path, overwrite: true); // the way an editor replaces a file
        using var watch = new CompositeWatch(s.Viewer, Page);

        await s.Vm.LoadDocumentAsync(path);

        var opened = s.Vm.PdfCoreDocument;
        watch.DocumentChanges.Should().Contain(c => ReferenceEquals(c.Document, opened));
        watch.DocumentChanges.Where(c => c.Document != null).Should().OnlyContain(c => !c.Present,
            $"a document that may differ must never be shown in the old page's pixels: {watch}");
    }

    /// <summary>Viewer pixels, in any channel, that differ between two captures.</summary>
    private static double Diff(SKBitmap a, SKBitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height), n = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
            if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) >= 60) n++;
        }
        return (double)n / Math.Max(1, w * h);
    }

    /// <summary>
    /// Three A4 pages, each mostly covered by a black box, so a blank page shows in
    /// any capture. A4's fractional size must survive the save's round trip.
    /// </summary>
    private static void CreateFixture(string path, double boxShiftPt = 0)
    {
        using var doc = PdfDocument.CreateNew();
        for (var i = 0; i < 3; i++)
        {
            var page = doc.Pages.AddBlank(595.276, 841.89);
            using var g = page.GetGraphics();
            g.DrawRectangle(72 + boxShiftPt, 144, 400, 560, PdfBrush.Black);
            g.Flush();
        }
        doc.Save(path);
    }

    private sealed class Session : IDisposable
    {
        public required string Dir { get; init; }
        public required string Source { get; init; }
        public required string Output { get; init; }
        public required MainWindowViewModel Vm { get; init; }
        public required MainWindow Window { get; init; }
        public required PdfViewerControl Viewer { get; init; }

        public static async Task<Session> OpenAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"excise-save-reload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "in.pdf");
            CreateFixture(source);
            var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            var s = new Session
            {
                Dir = dir, Source = source, Output = Path.Combine(dir, "out.pdf"), Vm = vm, Window = window,
                Viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!,
            };
            await vm.LoadDocumentAsync(source);
            await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
            vm.CurrentPageIndex = Page - 1;
            await AnnotationPlacementAccuracyTests.WaitForContinuousPageRendered(window, s.Viewer, Page);
            await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
            return s;
        }

        public WriteableBitmap? Composite() =>
            Viewer.FindControl<ItemsControl>("ContinuousItems")!.ItemsSource?.Cast<PdfPageSlot>()
                .FirstOrDefault(slot => slot.PageNumber == Page)?.Bitmap;

        public async Task<WriteableBitmap> WaitForComposite(
            Func<WriteableBitmap, bool> done)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (true)
            {
                Window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (Composite() is { } c && done(c) && Viewer.ContinuousInFlightCount == 0)
                {
                    await AnnotationPlacementAccuracyTests.WaitForIdleLayout(Window);
                    return c;
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"page {Page} never re-rendered: {Viewer.ContinuousDiagnostics()}");
                await Task.Delay(25);
            }
        }

        public void Dispose()
        {
            Window.Close();
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Whether page <see cref="Page"/>'s composite is on screen at every change of
    /// its slot's Bitmap, of the items' source, and of the viewer's Document.
    /// </summary>
    private sealed class CompositeWatch : IDisposable
    {
        private readonly PdfViewerControl _viewer;
        private readonly ItemsControl _items;
        private readonly int _page;
        private readonly List<string> _log = new();
        private PdfPageSlot? _slot;

        public CompositeWatch(PdfViewerControl viewer, int page)
        {
            _viewer = viewer;
            _items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
            _page = page;
            _items.PropertyChanged += OnItemsChanged;
            _viewer.PropertyChanged += OnViewerChanged;
            Rebind("start");
        }

        public bool EverAbsent { get; private set; }
        public List<(PdfDocument? Document, bool Present)> DocumentChanges { get; } = new();
        private bool Present => _slot?.Bitmap != null;

        private void Sample(string why)
        {
            _log.Add($"{why}:{(Present ? "shown" : "ABSENT")}");
            if (!Present) EverAbsent = true;
        }

        private void Rebind(string why)
        {
            if (_slot != null) _slot.PropertyChanged -= OnSlotChanged;
            _slot = _items.ItemsSource?.Cast<PdfPageSlot>().FirstOrDefault(s => s.PageNumber == _page);
            if (_slot != null) _slot.PropertyChanged += OnSlotChanged;
            Sample(why);
        }

        private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PdfPageSlot.Bitmap)) Sample("bitmap");
        }

        private void OnItemsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ItemsControl.ItemsSourceProperty) Rebind("slots");
        }

        private void OnViewerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != PdfViewerControl.DocumentProperty) return;
            Rebind("document");
            DocumentChanges.Add((_viewer.Document, Present));
        }

        public void Dispose()
        {
            _items.PropertyChanged -= OnItemsChanged;
            _viewer.PropertyChanged -= OnViewerChanged;
            if (_slot != null) _slot.PropertyChanged -= OnSlotChanged;
        }

        public override string ToString() => string.Join(", ", _log);
    }
}
