using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// #1203 — construction-known, mouse-driven viewer tests proving a real drag
/// (Avalonia.Headless input simulation through <see cref="MainWindow"/>, the
/// same pipeline <c>TextSelectionDragTests</c> exercises) reaches
/// <see cref="MainWindowViewModel.ClipboardHistory"/> in LOGICAL order for
/// Arabic and Hebrew: RTL-only word/phrase, mixed Latin+RTL lines, and a
/// column-local drag on an RTL two-column page.
///
/// Boundary (per #785, out of scope here): whole-line UBA paragraph-direction
/// resolution and explicit bidi controls are NOT exercised. Every fixture
/// below is either a single RTL run or a Latin run immediately followed by an
/// RTL run — the shape #632/#766/#373 already extract correctly — never an
/// RTL-first line mixing directions (that specific shape is #785's open gap).
///
/// Fixtures are raw PDFs (<see cref="RawPdfFixtures.WriteSimpleFontPdf"/>): a
/// non-embedded Helvetica-metric font plus a <c>/ToUnicode</c> CMap, exactly
/// the technique <c>Excise.Avalonia.Tests.RtlFixtures</c> and
/// <c>VerticalWritingMetricsTests</c> already use — real outlines are
/// irrelevant to selection/copy-order assertions.
/// </summary>
[Collection("AvaloniaTests")]
public class RtlMouseSelectionClipboardTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private const double RenderDpi = 120.0;

    public RtlMouseSelectionClipboardTests(ITestOutputHelper o) { _out = o; }

    public void Dispose() => _windows.Dispose();

    // Logical order (first char = first letter a reader pronounces).
    private const string ArabicWord = "سلام";        // "salaam" — peace
    private const string HebrewWord1 = "שלום";        // "shalom" — peace
    private const string HebrewWord2 = "עולם";        // "olam" — world

    [FixedAvaloniaFact]
    public async Task ArabicWordOnly_DragAcrossWord_CopiesLogicalWordToClipboardHistory()
    {
        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line(ArabicWord, 100, 700, Rtl: true));

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        ordered.Should().HaveCount(ArabicWord.Length);

        await DragAsync(window, page, ordered[0], ordered[^1]);

        vm.SelectedText.Should().Be(ArabicWord,
            "a drag across a single RTL word must copy it in logical (reading) order");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(ArabicWord,
            "the clipboard-history entry must retain logical order for an RTL word");

        // Highlight rectangles: one per glyph, each landing on a real glyph —
        // reusing the PdfViewerSelectionTests oracle (page-image-fraction),
        // not a self-referential PdfRectangleToDips comparison.
        AssertHighlightsLandOnGlyphs(window, page, expectedCount: ArabicWord.Length);
    }

    [FixedAvaloniaFact]
    public async Task HebrewPhrase_DragAcrossTwoWords_CopiesLogicalPhraseWithWordSpace()
    {
        var phrase = $"{HebrewWord1} {HebrewWord2}";
        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line(phrase, 100, 700, Rtl: true));

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        await DragAsync(window, page, ordered[0], ordered[^1]);

        vm.SelectedText.Should().Be(phrase,
            "a drag across an RTL phrase must copy both words in logical order with the word space preserved");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(phrase);
    }

    [FixedAvaloniaFact]
    public async Task MixedLatinArabicLine_DragAcrossLine_CopiesLatinThenArabicLogicalOrder()
    {
        const string expected = "HELLO" + ArabicWord;
        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line(expected, 100, 700, RtlRunStart: "HELLO".Length));
        // #785 boundary: this is a Latin run immediately followed by one RTL
        // run — not a direction change mid-run — so each run is placed in its
        // OWN visual order (Latin as-is, Arabic mirrored), matching a real
        // producer's paragraph layout, not the whole line reversed.

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        await DragAsync(window, page, ordered[0], ordered[^1]);

        vm.SelectedText.Should().Be(expected,
            "Latin text followed by a single RTL run must copy Latin-then-Arabic in logical order (#632/#766)");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expected);
    }

    [FixedAvaloniaFact]
    public async Task MixedLatinHebrewLine_DragAcrossLine_CopiesLatinThenHebrewLogicalOrder()
    {
        const string expected = "HELLO" + HebrewWord1;
        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line(expected, 100, 700, RtlRunStart: "HELLO".Length));

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        await DragAsync(window, page, ordered[0], ordered[^1]);

        vm.SelectedText.Should().Be(expected,
            "Latin text followed by a single RTL run must copy Latin-then-Hebrew in logical order");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expected);
    }

    [FixedAvaloniaFact]
    public async Task MixedLatinArabicLine_PositionedGapWithNoSpaceGlyph_StillCopiesAWordSpace()
    {
        // #1203 discriminating case: a real producer often places two runs
        // via Td/TJ positioning rather than an explicit space GLYPH. The
        // bidi-seam fix above must not overcorrect into treating every
        // Latin/RTL seam as gapless — a genuinely wide, positioned gap must
        // still read as a word space, exactly like a same-direction line
        // already does (DegenerateGlyphWidthTests.GapPositionedWords_...).
        // "HELLO" ends well before x=250 regardless of exactly which
        // Standard-14 glyph widths the fixture's codes resolve to.
        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line("HELLO", 100, 700),
            new RawPdfFixtures.Line(ArabicWord, 250, 700, Rtl: true));
        const string expected = "HELLO " + ArabicWord;

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        await DragAsync(window, page, ordered[0], ordered[^1]);

        vm.SelectedText.Should().Be(expected,
            "a genuinely wide, positioned gap between a Latin run and an RTL run must still " +
            "copy as a word space — the bidi-seam fix only silences a FABRICATED gap " +
            "(the RTL run's own mirrored width), not a real one");

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expected);
    }

    [FixedAvaloniaFact]
    public async Task RtlTwoColumnDrag_StaysWithinColumn_AndSeparatesRowsWithLineBreaks()
    {
        // Same geometry as TextSelectionDragTests' proven two-column English
        // fixture (x=72/330, y=700/660/620/580) — only the words are RTL.
        const string left0 = "بيت"; const string left1 = "عين";
        const string left2 = "قلب"; const string left3 = "شمس";
        const string right0 = "ماء"; const string right1 = "نار";
        const string right2 = "أرض"; const string right3 = "جو";

        var (vm, window, page) = await OpenFixtureAsync(
            new RawPdfFixtures.Line(left0, 72, 700, Rtl: true),
            new RawPdfFixtures.Line(left1, 72, 660, Rtl: true),
            new RawPdfFixtures.Line(left2, 72, 620, Rtl: true),
            new RawPdfFixtures.Line(left3, 72, 580, Rtl: true),
            new RawPdfFixtures.Line(right0, 330, 700, Rtl: true),
            new RawPdfFixtures.Line(right1, 330, 660, Rtl: true),
            new RawPdfFixtures.Line(right2, 330, 620, Rtl: true),
            new RawPdfFixtures.Line(right3, 330, 580, Rtl: true));

        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        var leftColumn = ordered.Where(l => l.GlyphRectangle.Left < 200).ToList();
        var rightColumn = ordered.Where(l => l.GlyphRectangle.Left >= 200).ToList();
        leftColumn.Should().HaveCount(left0.Length + left1.Length + left2.Length + left3.Length,
            "the column split must find exactly the left-column glyphs");
        rightColumn.Should().NotBeEmpty();

        await DragAsync(window, page, leftColumn[0], leftColumn[^1]);

        var expected = string.Join("\n", left0, left1, left2, left3);
        vm.SelectedText.Should().Be(expected,
            "a column-local drag down the left RTL column must read each row logically, " +
            "top-to-bottom, without pulling in the right column");
        vm.SelectedText.Should().NotContain(right0).And.NotContain(right1);

        await CopyAsync(vm);
        vm.ClipboardHistory[0].Text.Should().Be(expected);

        AssertHighlightsLandOnGlyphs(window, page, expectedCount: leftColumn.Count);
        // No highlight rect should sit in the right column's X range.
        var img = window.FindControl<PdfViewerControl>("PdfViewerControl")!.PdfImage!;
        var layer = window.FindControl<PdfViewerControl>("PdfViewerControl")!.TextSelectionLayer!;
        var rects = layer.Children.OfType<Rectangle>().ToList();
        var rightGlyphImageLeft = ExpectedGlyphImageRect(rightColumn[0], page, img).X;
        foreach (var r in rects)
        {
            var tl = r.TranslatePoint(new Point(0, 0), img)!.Value;
            tl.X.Should().BeLessThan(rightGlyphImageLeft - 4,
                "a column-local drag must not draw any highlight over the right column");
        }
    }

    // ── shared plumbing ──────────────────────────────────────────────────────

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfPage Page)> OpenFixtureAsync(
        params RawPdfFixtures.Line[] lines)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise-rtl-gui-{Guid.NewGuid():N}.pdf");
        RawPdfFixtures.WriteSimpleFontPdf(path, lines);

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
        var overlay = viewer.OverlayCanvas!;
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
            "an explicit Copy of an RTL selection must add a clipboard-history entry");
    }

    private static void AssertHighlightsLandOnGlyphs(MainWindow window, PdfPage page, int expectedCount)
    {
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var img = viewer.PdfImage!;
        var layer = viewer.TextSelectionLayer!;
        var rects = layer.Children.OfType<Rectangle>().ToList();

        rects.Should().HaveCount(expectedCount,
            "one highlight rectangle must be drawn per selected glyph");

        var expectedGlyphRects = page.Letters.Select(l => ExpectedGlyphImageRect(l, page, img)).ToList();
        double imgW = img.Bounds.Width;
        double pad = Math.Max(4.0, imgW * 0.02);
        foreach (var r in rects)
        {
            var tl = r.TranslatePoint(new Point(0, 0), img);
            tl.Should().NotBeNull();
            var drawn = new Rect(tl!.Value.X, tl.Value.Y, r.Bounds.Width, r.Bounds.Height);
            var c = drawn.Center;
            expectedGlyphRects.Should().Contain(
                e => c.X >= e.X - pad && c.X <= e.Right + pad && c.Y >= e.Y - pad && c.Y <= e.Bottom + pad,
                "every drawn highlight rect must sit over one of the page's real glyphs");
        }
    }

    private static Rect ExpectedGlyphImageRect(Letter l, PdfPage page, Image img)
    {
        var mb = page.MediaBox.Normalize();
        double imgW = img.Bounds.Width, imgH = img.Bounds.Height;
        var g = l.GlyphRectangle;
        double x0 = (g.Left - mb.Left) / mb.Width * imgW;
        double y0 = (mb.Top - g.Top) / mb.Height * imgH;
        double x1 = (g.Right - mb.Left) / mb.Width * imgW;
        double y1 = (mb.Top - g.Bottom) / mb.Height * imgH;
        return new Rect(x0, y0, x1 - x0, y1 - y0);
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

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
        }
        if (root is Decorator d && d.Child is Control dc)
        {
            var hit = FindNamedDescendant<T>(dc, name);
            if (hit != null) return hit;
        }
        if (root is ContentControl cc && cc.Content is Control ccc)
        {
            var hit = FindNamedDescendant<T>(ccc, name);
            if (hit != null) return hit;
        }
        return root.FindControl<T>(name);
    }
}
