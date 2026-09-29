using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.TestSupport;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// #1204 — construction-known, mouse-driven viewer tests proving a real drag
/// (the same Avalonia.Headless <see cref="MainWindow"/> pipeline
/// <c>TextSelectionDragTests</c> exercises) reaches
/// <see cref="MainWindowViewModel.ClipboardHistory"/> correctly for horizontal
/// CJK (a real embedded-font fixture) and PDF Identity-V vertical writing (a
/// synthetic fixture, following <c>VerticalWritingMetricsTests</c>'s pattern).
///
/// Horizontal CJK through the mouse-selection/clipboard path already works:
/// <see cref="TextSelectionEngine.SortReadingOrder"/> + <c>JoinText</c> on the
/// real fixture reproduce mutool's own line-by-line text exactly (verified
/// independently before writing these tests). Vertical (Identity-V) reading
/// order did NOT: <c>SortReadingOrder</c>'s column-aware strategy grouped
/// glyphs by Y-band (the horizontal-writing axis) and interleaved two columns
/// row-by-row instead of reading one column top-to-bottom before the next.
/// Fixed in <c>TextSelectionEngine.SortColumnAware</c> by reusing the same
/// <c>HasPredominantlyVerticalRuns</c> guard <c>DeterminePageTextOrder</c>
/// already had for whole-page order — bail to producer order, which a real
/// vertical-writing producer already emits column-by-column.
///
/// #1902: <c>JoinText</c> then broke a line between every pair of glyphs
/// stacked down a column, because its line rule read the horizontal-writing
/// axis. It now joins a vertical-writing glyph along its column, so a column
/// copies as one line and the break falls at the column change, as Poppler's
/// pdftotext reads the same bytes.
/// </summary>
[Collection("AvaloniaTests")]
public class CjkMouseSelectionClipboardTests : IDisposable
{
    private const string CjkFixtureRelative = "test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf";
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private const double RenderDpi = 120.0;

    public CjkMouseSelectionClipboardTests(ITestOutputHelper o) { _out = o; }

    public void Dispose() => _windows.Dispose();

    [FixedAvaloniaFact]
    public async Task RealCjkFixture_DragOverSimplifiedChineseLine_CopiesExactKnownTextToClipboardHistory()
    {
        var fixturePath = TestRepoLayout.FindFile(CjkFixtureRelative);
        Assert.SkipWhen(fixturePath == null,
            TestRepoLayout.AbsenceReason("CJK fixture", CjkFixtureRelative));

        // Known, independently-confirmed (mutool -F txt) exact line text,
        // including the fullwidth colon and full stop.
        const string expected = "简体中文：快速的棕色狐狸跳过懒狗。";

        var (vm, window, page) = await OpenFixtureAsync(fixturePath!);
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        var idx = FindSubsequence(ordered, expected);
        idx.Should().BeGreaterThanOrEqualTo(0,
            "the known simplified-Chinese line must be present in the extracted, reading-ordered letters");

        var anchor = ordered[idx];
        var focus = ordered[idx + expected.Length - 1];
        await DragAsync(window, page, anchor, focus);

        vm.SelectedText.Should().Be(expected,
            "a drag across one real horizontal-CJK line must copy the exact known text, " +
            "including fullwidth punctuation, with no internal line break");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expected);
    }

    [FixedAvaloniaFact]
    public async Task RealCjkFixture_DragAcrossWrappedKoreanLine_CopiesTextWithRealLineBreak()
    {
        var fixturePath = TestRepoLayout.FindFile(CjkFixtureRelative);
        Assert.SkipWhen(fixturePath == null,
            TestRepoLayout.AbsenceReason("CJK fixture", CjkFixtureRelative));

        // The fixture wraps this sentence onto two real visual lines (confirmed
        // independently: the glyphs sit at two distinct Y positions, 398.5pt and
        // 373.2pt) — a genuine producer line break, not one this test invents.
        const string plain = "한국어：빠른 갈색 여우가 게으른 개를 뛰어넘는다.";
        const string expectedWithBreak = "한국어：빠른 갈색 여우가 게으른 개를 뛰\n어넘는다.";

        var (vm, window, page) = await OpenFixtureAsync(fixturePath!);
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        var idx = FindSubsequence(ordered, plain);
        idx.Should().BeGreaterThanOrEqualTo(0,
            "the known Korean sentence must be present, spaces and all, in reading order");

        var anchor = ordered[idx];
        var focus = ordered[idx + plain.Length - 1];
        await DragAsync(window, page, anchor, focus);

        vm.SelectedText.Should().Be(expectedWithBreak,
            "a drag spanning a real wrapped CJK line must reproduce the producer's own line break");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expectedWithBreak);
    }

    [FixedAvaloniaFact]
    public async Task IdentityVVerticalFixture_TwoColumnDrag_CopiesColumnMajorLogicalSequence()
    {
        // Synthetic Type0/Identity-V fixture (VerticalWritingMetricsTests'
        // technique): two columns, right column read first (traditional CJK
        // vertical order), each glyph advancing one em down (spec default
        // /DW2 [880 -1000]). Includes a fullwidth ideographic comma to cover
        // CJK punctuation in a vertical run.
        const string columnA = "日本語";   // "Nihongo" — Japanese (language)
        const string columnB = "漢字、";   // "kanji" + fullwidth comma
        var path = Path.Combine(Path.GetTempPath(), $"excise-cjk-vertical-{Guid.NewGuid():N}.pdf");
        RawPdfFixtures.WriteIdentityVPdf(path,
            new RawPdfFixtures.VerticalColumn(columnA, 300, 700),
            new RawPdfFixtures.VerticalColumn(columnB, 200, 700));

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        for (var i = 0; i < 20 && vm.PdfCoreDocument == null; i++)
            await Task.Delay(100);
        vm.ViewMode = PdfViewMode.SinglePage;
        vm.IsTextSelectionMode = true;
        for (var i = 0; i < 20; i++) { await Task.Delay(100); window.UpdateLayout(); }

        var page = vm.PdfCoreDocument!.GetPage(1);
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        ordered.Should().HaveCount(columnA.Length + columnB.Length);

        // Before the #1204 fix, SortColumnAware grouped these six glyphs into
        // three Y-bands (one per row) and interleaved the columns row-by-row
        // ("日漢本字語、" — column A's glyph, then column B's glyph, per row).
        // The fix bails to producer order for a predominantly-vertical run, so
        // reading order is column-by-column: the whole of column A (painted
        // first, at the rightmost X) then the whole of column B.
        var expectedSequence = columnA + columnB;
        string.Concat(ordered.Select(l => l.Value)).Should().Be(expectedSequence,
            "reading order for a vertical two-column run must read one column top-to-bottom " +
            "before the next, not interleave rows across columns");

        await DragAsync(window, page, ordered[0], ordered[^1]);

        // #1902: one line per column, the break at the column change.
        var expectedText = columnA + "\n" + columnB;
        vm.SelectedText.Should().Be(expectedText,
            "a vertical two-column drag must copy each column as one line, in column-major order");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expectedText);

        File.Delete(path);
    }

    // ── shared plumbing (mirrors TextSelectionDragTests / RtlMouseSelectionClipboardTests) ──

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfPage Page)> OpenFixtureAsync(string path)
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        for (var i = 0; i < 20 && vm.PdfCoreDocument == null; i++)
            await Task.Delay(100);

        vm.ViewMode = PdfViewMode.SinglePage;
        vm.IsTextSelectionMode = true;
        for (var i = 0; i < 20; i++) { await Task.Delay(100); window.UpdateLayout(); }

        var page = vm.PdfCoreDocument!.GetPage(1);
        return (vm, window, page);
    }

    private async Task DragAsync(MainWindow window, PdfPage page, Letter anchor, Letter focus)
    {
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var overlay = viewer.SinglePagePart.OverlayCanvas!;
        var start = ToWindowPoint(anchor, page, overlay, window);
        var end = ToWindowPoint(focus, page, overlay, window);

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));

        for (var i = 0; i < 30; i++) { await Task.Delay(100); window.UpdateLayout(); }
    }

    private static async Task CopyAsync(MainWindowViewModel vm)
    {
        var before = vm.ClipboardHistory.Count;
        vm.CopyTextCommand.Execute().Subscribe();
        for (int i = 0; i < 50 && vm.ClipboardHistory.Count == before; i++)
            await Task.Delay(100);
        vm.ClipboardHistory.Count.Should().BeGreaterThan(before,
            "an explicit Copy of a CJK selection must add a clipboard-history entry");
    }

    private static int FindSubsequence(System.Collections.Generic.IReadOnlyList<Letter> letters, string target)
    {
        for (int i = 0; i + target.Length <= letters.Count; i++)
        {
            bool ok = true;
            for (int k = 0; k < target.Length; k++)
                if (letters[i + k].Value.Length == 0 || letters[i + k].Value[0] != target[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static Point ToWindowPoint(Letter l, PdfPage page, Canvas overlay, Window window)
    {
        var r = l.GlyphRectangle;
        var center = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(
                page.PageNumber,
                new PdfRectangle(
                    (r.Left + r.Right) * 0.5,
                    (r.Bottom + r.Top) * 0.5,
                    (r.Left + r.Right) * 0.5,
                    (r.Bottom + r.Top) * 0.5)),
            RenderDpi);
        return overlay.TranslatePoint(new Point(center.X, center.Y), window) ?? default;
    }
}
