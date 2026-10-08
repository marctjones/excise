using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1984: saved Highlight / Underline / StrikeOut / Squiggly on rotated pages, through the
/// real workflow: arm the tool, drag over the word, Save As, reopen, mark a second word,
/// Undo, Redo, Save As again.
///
/// <para><b>Oracles.</b> qpdf reads the saved /Rect and /QuadPoints (structure). MuPDF
/// and Poppler each RENDER the saved file, and each one's own text extractor (MuPDF stext,
/// Poppler <c>pdftotext -bbox</c>) says where the word is displayed, so each renderer is
/// compared with itself, in its own page frame. The markup's coloured pixels are located
/// relative to the word along MuPDF's line direction: a highlight covers it, a strikeout
/// runs through its middle, an underline or squiggle runs along its baseline side. excise
/// reads nothing back.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class RotatedMarkupPlacementTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "excise-1984-" + Guid.NewGuid().ToString("N"));

    public RotatedMarkupPlacementTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private const string FirstWord = RotationProbes.Target; // ALPHA
    private const string SecondWord = "ONE";               // same line, so every probe has it

    /// <summary>
    /// Each kind on several geometries rather than every kind on every row: the four kinds
    /// share one authoring path, so the matrix varies rotation, origin and route across them.
    /// The counter-rotated-text probe takes all four.
    /// </summary>
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "s-r0", nameof(MarkupAnnotationKind.Highlight) },
        new object[] { "s-r90", nameof(MarkupAnnotationKind.Underline) },
        new object[] { "s-r180-z150", nameof(MarkupAnnotationKind.StrikeOut) },
        new object[] { "s-r270", nameof(MarkupAnnotationKind.Squiggly) },
        new object[] { "s-inherit90", nameof(MarkupAnnotationKind.Highlight) },
        new object[] { "s-crop90-z75", nameof(MarkupAnnotationKind.Underline) },
        new object[] { "s-r0-ui90", nameof(MarkupAnnotationKind.Squiggly) },
        new object[] { "s-r0-ui270-z150", nameof(MarkupAnnotationKind.StrikeOut) },
        new object[] { "s-crop0-ui90", nameof(MarkupAnnotationKind.Highlight) },
        new object[] { "s-textccw", nameof(MarkupAnnotationKind.Highlight) },
        new object[] { "s-textccw", nameof(MarkupAnnotationKind.Underline) },
        new object[] { "s-textccw", nameof(MarkupAnnotationKind.StrikeOut) },
        new object[] { "s-textccw", nameof(MarkupAnnotationKind.Squiggly) },
    };

    private static RotationScenario Scenario(string id) => id == "s-textccw"
        ? new RotationScenario("s-textccw", "probe-r90-textccw", 0, RotationView.SinglePage, 1.0, 1)
        : RotationScenarioTable.Get(id);

    [FixedAvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task SavedMarkup_LandsOnTheWord_ThroughSaveReopenEditUndoRedo(string scenarioId, string kindName)
    {
        var kind = Enum.Parse<MarkupAnnotationKind>(kindName);
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable || !MutoolReferenceRenderer.IsAvailable,
            "mutool is not installed; it is the glyph-region and render oracle.");
        Assert.SkipWhen(!PopplerGeometry.IsAvailable || !PdftoppmReferenceRenderer.IsAvailable,
            "Poppler (pdfinfo, pdftotext, pdftoppm) is not installed.");
        Assert.SkipWhen(!QpdfReferenceTool.IsAvailable, "qpdf is not installed.");

        var s = Scenario(scenarioId);
        var bytes = RotationFixtures.TryLoad(s.Fixture, out var absence);
        Assert.SkipWhen(bytes == null, absence);

        var source = System.IO.Path.Combine(_tempDir, "source.pdf");
        await File.WriteAllBytesAsync(source, bytes!);
        var sourceHash = SHA256.HashData(bytes!);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        if (s.Dpr != 1) viewer.RenderScalingOverride = s.Dpr;
        await vm.LoadDocumentAsync(source);
        vm.ViewMode = PdfViewMode.SinglePage;
        for (int i = 0; i < s.UiQuarterTurns; i++) await vm.RotatePageRightCommand.Execute();
        if (s.Zoom > 0) vm.ZoomLevel = s.Zoom;
        _out.WriteLine($"{s.Id} {kind}: fixture={s.FixtureId} final={s.FinalRotation}");

        // 1) Mark the first word, Save As, close and reopen.
        await MarkWordAsync(window, viewer, vm, kind, FirstWord);
        var first = System.IO.Path.Combine(_tempDir, "first.pdf");
        await vm.SaveFileAsAsync(first);
        await vm.LoadDocumentAsync(first);
        vm.ViewMode = PdfViewMode.SinglePage;
        AssertSaved(first, kind, s.FinalRotation, new[] { FirstWord });

        // 2) Edit again on the reopened file: mark a second word, Undo, Redo, Save As.
        await MarkWordAsync(window, viewer, vm, kind, SecondWord);
        int Count() => vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Count(a => a.Subtype.ToString() == kind.ToString());
        Count().Should().Be(2);
        await vm.UndoCommand.Execute();
        Count().Should().Be(1, "Undo removes the second markup");
        await vm.RedoCommand.Execute();
        Count().Should().Be(2, "Redo restores it");
        var second = System.IO.Path.Combine(_tempDir, "second.pdf");
        await vm.SaveFileAsAsync(second);
        AssertSaved(second, kind, s.FinalRotation, new[] { FirstWord, SecondWord });

        SHA256.HashData(await File.ReadAllBytesAsync(source)).Should().Equal(sourceHash,
            "Save As must leave the original input untouched");
    }

    /// <summary>
    /// A selection that spans two lines, marked and saved (#2009): one quad per selected line
    /// run, and nothing drawn in the gap between the lines or on the unselected words.
    ///
    /// <para>The selection is made by the real gesture. Oracles are independent: qpdf reads
    /// /QuadPoints and /Rect; MuPDF and Poppler each render the source and the saved file and
    /// the pixels that CHANGED are compared in regions located by that renderer's own text
    /// geometry (the middle third of the band between the two lines, and each word's box).
    /// Diffing against the unmarked render keeps the test independent of how a renderer
    /// synthesizes a Highlight that has no appearance stream.</para>
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData("s-r90", nameof(MarkupAnnotationKind.Highlight), "ALPHA", "TWO", "ALPHA,ONE,BRAVO,TWO", "")]
    [InlineData("s-r0", nameof(MarkupAnnotationKind.Highlight), "ALPHA", "TWO", "ALPHA,ONE,BRAVO,TWO", "")]
    // Mid-line to mid-line: the union box would also cover "ALPHA" and "TWO", which are not selected.
    [InlineData("s-r0", nameof(MarkupAnnotationKind.Highlight), "ONE", "BRAVO", "ONE,BRAVO", "ALPHA,TWO")]
    [InlineData("s-r90", nameof(MarkupAnnotationKind.Highlight), "ONE", "BRAVO", "ONE,BRAVO", "ALPHA,TWO")]
    [InlineData("s-r0", nameof(MarkupAnnotationKind.Underline), "ALPHA", "TWO", "ALPHA,ONE,BRAVO,TWO", "")]
    [InlineData("s-r90", nameof(MarkupAnnotationKind.StrikeOut), "ALPHA", "TWO", "ALPHA,ONE,BRAVO,TWO", "")]
    [InlineData("s-r0", nameof(MarkupAnnotationKind.Squiggly), "ALPHA", "TWO", "ALPHA,ONE,BRAVO,TWO", "")]
    public async Task SavedMarkup_OfAMultiLineSelection_MarksOnlyTheSelectedRuns(
        string scenarioId, string kindName, string startWord, string endWord, string markedCsv, string clearCsv)
    {
        var kind = Enum.Parse<MarkupAnnotationKind>(kindName);
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable || !MutoolReferenceRenderer.IsAvailable,
            "mutool is not installed; it is the glyph-region and render oracle.");
        Assert.SkipWhen(!PopplerGeometry.IsAvailable || !PdftoppmReferenceRenderer.IsAvailable,
            "Poppler (pdfinfo, pdftotext, pdftoppm) is not installed.");
        Assert.SkipWhen(!QpdfReferenceTool.IsAvailable, "qpdf is not installed.");

        var marked = markedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var clear = clearCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var s = RotationScenarioTable.Get(scenarioId);
        var source = System.IO.Path.Combine(_tempDir, "source.pdf");
        await File.WriteAllBytesAsync(source, RotationFixtures.TryLoad(s.Fixture, out _)!);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        await vm.LoadDocumentAsync(source);
        vm.ViewMode = PdfViewMode.SinglePage;
        vm.ZoomLevel = 1.0;

        await MarkSpanAsync(window, viewer, vm, kind, startWord, endWord);
        vm.CurrentTextSelectionLineRectangles.Should().HaveCount(2,
            "the viewer reports one rectangle per line run of the selection");
        var saved = System.IO.Path.Combine(_tempDir, "two-lines.pdf");
        await vm.SaveFileAsAsync(saved);

        // Structure (qpdf): one annotation, one quad per line, /Rect covering both quads.
        var annot = QpdfReferenceTool.ListAnnotations(saved)!.Where(a => a.Subtype == kind.ToString()).Should()
            .ContainSingle().Subject;
        annot.QuadPoints.Should().NotBeNull().And.HaveCount(16, "two lines are two quads");
        for (int q = 0; q < 2; q++)
        {
            var quad = annot.QuadPoints!.Skip(q * 8).Take(8).ToList();
            var xs = quad.Where((_, i) => i % 2 == 0).ToList();
            var ys = quad.Where((_, i) => i % 2 == 1).ToList();
            xs.Min().Should().BeGreaterThanOrEqualTo(annot.Left - 0.5);
            xs.Max().Should().BeLessThanOrEqualTo(annot.Right + 0.5);
            ys.Min().Should().BeGreaterThanOrEqualTo(annot.Bottom - 0.5);
            ys.Max().Should().BeLessThanOrEqualTo(annot.Top + 0.5);
        }

        // Pixels (MuPDF), against MuPDF's own render of the source and its own text boxes.
        var stext = MutoolStextGeometry.Read(saved, 1);
        using (var before = MutoolReferenceRenderer.RenderPage(source, 1, Dpi))
        using (var after = MutoolReferenceRenderer.RenderPage(saved, 1, Dpi))
        {
            AssertMarkedRuns(before!, after!, stext.Width, w => stext.Find(w)[0], marked, clear, kind, "MuPDF");
        }

        // Pixels (Poppler), likewise in its own frame.
        var poppler = PopplerGeometry.Words(saved, 1, cropBox: false);
        using (var before = PdftoppmReferenceRenderer.RenderPage(source, 1, Dpi))
        using (var after = PdftoppmReferenceRenderer.RenderPage(saved, 1, Dpi))
        {
            AssertMarkedRuns(before!, after!, after!.Width * 72.0 / Dpi,
                w => poppler.Words.First(p => p.Word == w).Box, marked, clear, kind, "Poppler");
        }
    }

    private void AssertMarkedRuns(SKBitmap before, SKBitmap after, double pageWidthPt, Func<string, VisualRegion> box,
        string[] marked, string[] clear, MarkupAnnotationKind kind, string what)
    {
        before.Width.Should().Be(after.Width);
        double scale = after.Width / pageWidthPt;
        int Changed(VisualRegion r)
        {
            int n = 0;
            for (int py = Math.Max(0, (int)(r.Top * scale)); py < Math.Min(after.Height, (int)(r.Bottom * scale)); py++)
                for (int px = Math.Max(0, (int)(r.Left * scale)); px < Math.Min(after.Width, (int)(r.Right * scale)); px++)
                {
                    var a = after.GetPixel(px, py);
                    var b = before.GetPixel(px, py);
                    if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 90) n++;
                }
            return n;
        }

        foreach (var w in marked)
            Changed(box(w).Inflate(6)).Should().BeGreaterThan(10, $"{what}: the {kind} marks '{w}'");
        foreach (var w in clear)
            Changed(box(w).Inflate(2)).Should().BeLessThan(3, $"{what}: '{w}' is not selected, so the {kind} leaves it alone");

        // The band between the two lines, measured between the first word of each (ALPHA, BRAVO).
        var a1 = box("ALPHA");
        var b1 = box("BRAVO");
        var everything = VisualRegion.Union(marked.Select(box));
        bool separatedInY = a1.Bottom <= b1.Top || b1.Bottom <= a1.Top;
        VisualRegion gap;
        if (separatedInY)
        {
            double lo = Math.Min(a1.Bottom, b1.Bottom), hi = Math.Max(a1.Top, b1.Top);
            gap = new VisualRegion(everything.Left, lo + (hi - lo) / 3, everything.Right, hi - (hi - lo) / 3);
        }
        else
        {
            double lo = Math.Min(a1.Right, b1.Right), hi = Math.Max(a1.Left, b1.Left);
            gap = new VisualRegion(lo + (hi - lo) / 3, everything.Top, hi - (hi - lo) / 3, everything.Bottom);
        }
        _out.WriteLine($"{what}: gap {gap}: changed pixels {Changed(gap)}");
        gap.Width.Should().BeGreaterThan(0);
        gap.Height.Should().BeGreaterThan(0);
        Changed(gap).Should().BeLessThan(3, $"{what}: nothing of the {kind} is drawn in the gap between the lines");
    }

    private Task MarkWordAsync(Window window, PdfViewerControl viewer, MainWindowViewModel vm,
        MarkupAnnotationKind kind, string word) => MarkSpanAsync(window, viewer, vm, kind, word, word);

    private async Task MarkSpanAsync(Window window, PdfViewerControl viewer, MainWindowViewModel vm,
        MarkupAnnotationKind kind, string word, string endWord)
    {
        // The oracle for where to drag: MuPDF stext of the document as it now stands.
        var probe = System.IO.Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(probe, vm.PdfCoreDocument!.SaveToBytes());
        var stext = MutoolStextGeometry.Read(probe, 1);
        File.Delete(probe);
        var chars = stext.FindChars(word);
        chars.Should().NotBeEmpty($"MuPDF must find '{word}'");

        vm.MarkupAnnotationKind = kind;
        vm.IsMarkupAnnotationMode = true;
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
        var img = viewer.SinglePagePart.PdfImage!;
        await WaitAsync(() =>
        {
            window.UpdateLayout();
            return !viewer.IsLoading &&
                   Math.Abs(img.Bounds.Width / img.Bounds.Height - stext.Width / stext.Height) < 0.01;
        }, "the page to render at MuPDF's displayed aspect");
        var overlay = viewer.SinglePagePart.OverlayCanvas!;
        Point ToOverlay(VisualRegion c) => new(
            c.CenterX / stext.Width * img.Bounds.Width, c.CenterY / stext.Height * img.Bounds.Height);

        int before = vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Count;
        var endChars = stext.FindChars(endWord);
        endChars.Should().NotBeEmpty($"MuPDF must find '{endWord}'");
        RaiseSelectionDrag(overlay, ToOverlay(chars[0][0]), ToOverlay(endChars[0][^1]));
        await WaitAsync(() => vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Count == before + 1,
            $"the {kind} on '{word}' to be applied on mouse-up");
        _out.WriteLine($"marked '{word}' (selected '{vm.SelectedText}')");
    }

    private void AssertSaved(string path, MarkupAnnotationKind kind, int rotation, IReadOnlyList<string> words)
    {
        // Structure (qpdf): one annotation per word, each with one quad inside its /Rect.
        QpdfReferenceTool.Check(path)!.Value.Success.Should().BeTrue("qpdf --check accepts the saved file");
        PopplerGeometry.Info(path, 1).Rotation.Should().Be(rotation, "Poppler reads the saved rotation");
        var annots = QpdfReferenceTool.ListAnnotations(path)!
            .Where(a => a.Subtype == kind.ToString()).ToList();
        annots.Should().HaveCount(words.Count, $"one {kind} per marked word in {System.IO.Path.GetFileName(path)}");
        foreach (var a in annots)
        {
            a.QuadPoints.Should().NotBeNull().And.HaveCount(8, "one word is one quad");
            var xs = a.QuadPoints!.Where((_, i) => i % 2 == 0).ToList();
            var ys = a.QuadPoints!.Where((_, i) => i % 2 == 1).ToList();
            xs.Min().Should().BeGreaterThanOrEqualTo(a.Left - 0.5);
            xs.Max().Should().BeLessThanOrEqualTo(a.Right + 0.5);
            ys.Min().Should().BeGreaterThanOrEqualTo(a.Bottom - 0.5);
            ys.Max().Should().BeLessThanOrEqualTo(a.Top + 0.5);
        }

        // Placement, MuPDF: its render against its own stext.
        var stext = MutoolStextGeometry.Read(path, 1);
        using (var mupdf = MutoolReferenceRenderer.RenderPage(path, 1, Dpi))
        {
            mupdf.Should().NotBeNull("mutool renders the saved file");
            foreach (var w in words)
                AssertMarkupOn(mupdf!, stext.Width, stext.Find(w)[0], stext.DirectionOf(w), kind, $"MuPDF '{w}'");
        }

        // Placement, Poppler: its render (MediaBox frame) against its own -bbox words.
        var poppler = PopplerGeometry.Words(path, 1, cropBox: false);
        using (var render = PdftoppmReferenceRenderer.RenderPage(path, 1, Dpi))
        {
            render.Should().NotBeNull("pdftoppm renders the saved file");
            double pageWidthPt = render!.Width * 72.0 / Dpi;
            foreach (var w in words)
            {
                var box = poppler.Words.First(p => p.Word == w).Box;
                AssertMarkupOn(render, pageWidthPt, box, stext.DirectionOf(w), kind, $"Poppler '{w}'");
            }
        }
    }

    private const int Dpi = 144;

    /// <summary>Where the markup's coloured pixels sit relative to the word, along the line direction.</summary>
    private void AssertMarkupOn(SKBitmap bmp, double pageWidthPt, VisualRegion word, (double X, double Y) dir,
        MarkupAnnotationKind kind, string what)
    {
        bool vertical = Math.Abs(dir.Y) > Math.Abs(dir.X);
        double cross = vertical ? word.Width : word.Height;
        double along = vertical ? word.Height : word.Width;
        var down = (X: -dir.Y, Y: dir.X);
        var window = vertical
            ? new VisualRegion(word.Left - 0.8 * cross, word.Top - 2, word.Right + 0.8 * cross, word.Bottom + 2)
            : new VisualRegion(word.Left - 2, word.Top - 0.8 * cross, word.Right + 2, word.Bottom + 0.8 * cross);

        double scale = bmp.Width / pageWidthPt;
        int colored = 0, wordPixels = 0, coloredInWord = 0;
        double projection = 0, alongMin = double.MaxValue, alongMax = double.MinValue;
        for (int py = Math.Max(0, (int)(window.Top * scale)); py < Math.Min(bmp.Height, (int)(window.Bottom * scale)); py++)
            for (int px = Math.Max(0, (int)(window.Left * scale)); px < Math.Min(bmp.Width, (int)(window.Right * scale)); px++)
            {
                double x = (px + 0.5) / scale, y = (py + 0.5) / scale;
                var c = bmp.GetPixel(px, py);
                bool isColored = Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 80;
                bool inWord = word.Contains(x, y);
                if (inWord) wordPixels++;
                if (!isColored) continue;
                colored++;
                if (inWord) coloredInWord++;
                projection += ((x - word.CenterX) * down.X + (y - word.CenterY) * down.Y) / (cross / 2);
                double a = vertical ? y : x;
                alongMin = Math.Min(alongMin, a);
                alongMax = Math.Max(alongMax, a);
            }

        colored.Should().BeGreaterThan(10, $"{what}: the {kind} must be drawn at the word {word}");
        projection /= colored;
        double coverage = (double)coloredInWord / Math.Max(1, wordPixels);
        double alongCoverage = (alongMax - alongMin) / along;
        _out.WriteLine($"{what}: word {word} dir ({dir.X},{dir.Y}) colored={colored} coverage={coverage:F2} " +
                       $"projection={projection:F2} along={alongCoverage:F2}");

        alongCoverage.Should().BeInRange(0.75, 1.35, $"{what}: the {kind} must run the length of the word");
        switch (kind)
        {
            // Thresholds carry the classified #1982 metric difference: excise's letter cell
            // is baseline to baseline + font size (18 pt), MuPDF's word box is glyph ascent
            // (13.1 pt) and Poppler's the font bbox (16.65 pt, with descender). A highlight
            // filling the cell therefore sits about half a box above MuPDF's centre; one
            // beside or past the word has no coverage at all.
            case MarkupAnnotationKind.Highlight:
                coverage.Should().BeGreaterThan(0.35, $"{what}: a highlight covers the word");
                Math.Abs(projection).Should().BeLessThan(0.75, $"{what}: a highlight is centred on the word");
                break;
            case MarkupAnnotationKind.StrikeOut:
                Math.Abs(projection).Should().BeLessThan(0.5, $"{what}: a strikeout runs through the word's middle");
                break;
            default:
                projection.Should().BeGreaterThan(0.6,
                    $"{what}: a {kind} runs along the word's baseline side, below the text as it reads");
                break;
        }
    }

    private static async Task WaitAsync(Func<bool> ready, string what, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    /// <summary>Press, move, release in <paramref name="target"/>'s coordinates, raised from the top level (see #1983's harness note).</summary>
    private static void RaiseSelectionDrag(Control target, Point start, Point end)
    {
        var root = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("target is not in a window");
        Point R(Point p) => target.TranslatePoint(p, root) ?? throw new InvalidOperationException("no transform to the window");
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, root, R(start), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        target.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, target, pointer, root, R(end), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, R(end), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }
}
