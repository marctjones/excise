using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Excise.TestSupport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
using Excise.Rendering.Differential;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Broadens the true drag-to-redact GUI coverage (#1161). The existing
/// <c>RedactionMouseWorkflowTests</c> covers four content shapes at the default
/// view; the pointer→region→pending→apply path also has to survive state
/// accumulation, a non-default zoom, and page rotation — where the coordinate
/// mapping between the on-screen box and the content glyphs is most likely to be
/// wrong. Every case here uses real <c>MouseDown/Move/Up</c>, then verifies
/// removal against a re-parse of the SAVED file and the saved raw bytes, exactly
/// as the original does — the mouse path, not the command, is what a regression
/// must fail.
/// </summary>
[Collection("AvaloniaTests")]
public class RedactionMouseDragBroadeningTests
{
    private const double RenderDpi = 120.0;

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task TwoDragsInOneSession_RemoveBothTargets_AndKeepSurvivors()
    {
        var (dir, src) = NewPdf("multibox");
        CreateLabeledPdf(src, rotation: 0,
            ("KEEPTOP", 100, 700), ("SECRETONE", 100, 520), ("SECRETTWO", 100, 340), ("KEEPBOTTOM", 100, 160));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            // First drag → first pending redaction.
            await DoDrag(window, ContentRectOf(page, "SECRETONE"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Count.Should().Be(1, "first mouse drag makes one pending redaction");

            // Second drag in the SAME session → accumulates, does not replace.
            await DoDrag(window, ContentRectOf(page, "SECRETTWO"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Count.Should().Be(2,
                "a second mouse drag must accumulate a second pending redaction, not overwrite the first");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("SECRETONE").And.NotContain("SECRETTWO",
                "both mouse-drawn redactions must be applied from one session");
            text.Should().Contain("KEEPTOP").And.Contain("KEEPBOTTOM",
                "content outside both boxes must survive");
            // Independent, decompressing oracle (#1049): the term must be gone
            // from the SAVED BYTES including inside FlateDecode streams, not just
            // from excise's own extraction above.
            var savedOne = File.ReadAllBytes(outPath);
            SavedPdfLeakScanner.FindTerm(savedOne, "SECRETONE").Should().BeEmpty(
                "the mouse-drawn redaction must REMOVE the glyphs, not hide them");
            SavedPdfLeakScanner.FindTerm(savedOne, "SECRETTWO").Should().BeEmpty();
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragAtNonDefaultZoom_RemovesTargetUnderTheBox()
    {
        var (dir, src) = NewPdf("zoom");
        CreateLabeledPdf(src, rotation: 0,
            ("KEEPZOOM", 100, 640), ("ZOOMSECRET", 100, 460), ("KEEPLOW", 100, 280));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            // Kick the viewport off 100% — the overlay lives inside the scale
            // transform, so the content→viewer mapping must still land the box.
            vm.ZoomInCommand.Execute().Subscribe();
            vm.ZoomInCommand.Execute().Subscribe();
            await WaitForIdleLayout(window);

            await DoDrag(window, ContentRectOf(page, "ZOOMSECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle("one drag at zoom makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("ZOOMSECRET", "the box drawn at non-default zoom must map to the glyphs under it");
            text.Should().Contain("KEEPZOOM").And.Contain("KEEPLOW", "content outside the box must survive");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "ZOOMSECRET").Should().BeEmpty(
                "the box drawn at non-default zoom must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragOnRotatedPage_RemovesTargetUnderTheBox()
    {
        var (dir, src) = NewPdf("rotate90");
        CreateLabeledPdf(src, rotation: 90,
            ("KEEPROT", 100, 640), ("ROTSECRET", 100, 460), ("KEEPROT2", 100, 280));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            page.Rotation.Should().Be(90, "the fixture page must load rotated so the viewer renders it rotated");

            await DoDrag(window, ContentRectOf(page, "ROTSECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag on a rotated page makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("ROTSECRET",
                "on a /Rotate 90 page the on-screen box must map through the rotation to the right glyphs");
            text.Should().Contain("KEEPROT").And.Contain("KEEPROT2",
                "only the targeted word may be removed on a rotated page");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "ROTSECRET").Should().BeEmpty(
                "on a /Rotate 90 page the redaction must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1161: the issue's rotation family is 90/180/270, not just 90 -- 180 does
    /// not swap width/height the way 90/270 do, so it exercises a different arm
    /// of the rotation math in <see cref="PdfCoordinateMapper"/>.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragOnRotatedPage180_RemovesTargetUnderTheBox()
    {
        var (dir, src) = NewPdf("rotate180");
        CreateLabeledPdf(src, rotation: 180,
            ("KEEPROT180A", 100, 640), ("ROT180SECRET", 100, 460), ("KEEPROT180B", 100, 280));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            page.Rotation.Should().Be(180, "the fixture page must load rotated so the viewer renders it rotated");

            await DoDrag(window, ContentRectOf(page, "ROT180SECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag on a /Rotate 180 page makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("ROT180SECRET",
                "on a /Rotate 180 page the on-screen box must map through the rotation to the right glyphs");
            text.Should().Contain("KEEPROT180A").And.Contain("KEEPROT180B",
                "only the targeted word may be removed on a rotated page");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "ROT180SECRET").Should().BeEmpty(
                "on a /Rotate 180 page the redaction must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragOnRotatedPage270_RemovesTargetUnderTheBox()
    {
        var (dir, src) = NewPdf("rotate270");
        CreateLabeledPdf(src, rotation: 270,
            ("KEEPROT270A", 100, 640), ("ROT270SECRET", 100, 460), ("KEEPROT270B", 100, 280));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            page.Rotation.Should().Be(270, "the fixture page must load rotated so the viewer renders it rotated");

            await DoDrag(window, ContentRectOf(page, "ROT270SECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag on a /Rotate 270 page makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("ROT270SECRET",
                "on a /Rotate 270 page the on-screen box must map through the rotation to the right glyphs");
            text.Should().Contain("KEEPROT270A").And.Contain("KEEPROT270B",
                "only the targeted word may be removed on a rotated page");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "ROT270SECRET").Should().BeEmpty(
                "on a /Rotate 270 page the redaction must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1161: "zoomed / scrolled viewport" -- the zoom half is covered by
    /// <see cref="DragAtNonDefaultZoom_RemovesTargetUnderTheBox"/>; this covers the
    /// other half, a drag while the single-page <c>ScrollViewer</c> is scrolled
    /// away from its origin (not just zoomed). The overlay's pointer mapping goes
    /// through the live visual transform (<c>TranslatePoint</c> in this harness,
    /// <c>GetPosition</c> in production), so a stale cached offset would show up
    /// here as a box that lands on the pre-scroll position instead.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragAfterScrollingAwayFromOrigin_RemovesTargetUnderTheBox()
    {
        var (dir, src) = NewPdf("scrolled");
        CreateLabeledPdf(src, rotation: 0,
            ("KEEPNEAR", 100, 700), ("SCROLLSECRET", 100, 100));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            // Zoom in enough that the page exceeds the viewport, then scroll all
            // the way down so the target is only reachable away from (0,0).
            vm.SetManualZoom(2.5);
            await WaitForIdleLayout(window);

            var scrollViewer = viewer.SinglePagePart.PdfScrollViewer!;
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            await WaitForIdleLayout(window);
            scrollViewer.Offset.Y.Should().BeGreaterThan(0,
                "the fixture must actually force a scroll away from the origin, or this test proves nothing");

            await DoDrag(window, ContentRectOf(page, "SCROLLSECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag while scrolled away from the origin makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("SCROLLSECRET",
                "a box drawn while the viewport is scrolled away from the origin must still map to the glyphs under it");
            text.Should().Contain("KEEPNEAR", "content outside the box must survive");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "SCROLLSECRET").Should().BeEmpty(
                "the scrolled-viewport redaction must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1161: continuous scroll is the app's DEFAULT view mode (not single-page),
    /// and entering redaction mode forces a switch back to single-page
    /// (<c>MainWindowViewModel.IsRedactionMode</c>). This drives that transition
    /// from a page the user scrolled to in the reading view -- not page 1 -- and
    /// checks the drag lands on the SAME page's glyphs, not page 1's or a stale
    /// pre-switch layout.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragAfterEnteringRedactionModeFromScrolledContinuousView_RemovesTargetOnThatPage()
    {
        var (dir, src) = NewPdf("continuous-switch");
        CreateTwoPageLabeledPdf(src,
            new[] { ("KEEPPAGE1", 100.0, 400.0) },
            new[] { ("KEEPPAGE2", 100.0, 640.0), ("CONTINUOUSSECRET", 100.0, 460.0) });

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 2000, Height = 1600 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            await WaitForIdleLayout(window);
            vm.IsContinuousView.Should().BeTrue("continuous scroll is the app's default view mode");

            // Scroll to page 2 while still reading in continuous mode.
            vm.CurrentPageIndex = 1;
            await WaitForIdleLayout(window);
            vm.CurrentPageIndex.Should().Be(1, "the scroll-driven page sync must not snap back to page 1");

            vm.IsRedactionMode = true;
            await WaitForIdleLayout(window);
            vm.IsContinuousView.Should().BeFalse("entering redaction mode must switch out of the continuous view");
            vm.CurrentPage.Should().Be(2,
                "the single page redaction lands on must be the page the user was reading, not page 1");

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            var overlay = viewer.SinglePagePart.OverlayCanvas!;
            var page = vm.PdfCoreDocument!.GetPage(2);

            await DoDrag(window, ContentRectOf(page, "CONTINUOUSSECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag right after switching in from continuous mode makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var saved = File.ReadAllBytes(outPath);
            using (var reopened = PdfDocument.Open(saved))
            {
                var page1Text = string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value));
                var page2Text = string.Concat(reopened.GetPage(2).Letters.Select(l => l.Value));
                page1Text.Should().Contain("KEEPPAGE1", "content on the other page must survive");
                page2Text.Should().NotContain("CONTINUOUSSECRET",
                    "a drag made right after switching in from a scrolled continuous view must still map to the right page's glyphs");
                page2Text.Should().Contain("KEEPPAGE2");
            }
            SavedPdfLeakScanner.FindTerm(saved, "CONTINUOUSSECRET").Should().BeEmpty(
                "the redaction entered from continuous mode must remove the glyphs from the saved bytes");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1161: a single box spanning two side-by-side text blocks (columns) must
    /// remove BOTH runs of glyphs under it, not stop at whichever run the
    /// drag started over. Verified the same way as the other drags here.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragAcrossColumnBoundary_RemovesBothColumnsUnderTheBox()
    {
        var (dir, src) = NewPdf("columns");
        CreateLabeledPdf(src, rotation: 0,
            ("KEEPABOVE", 100, 650), ("LEFTCOL", 100, 500), ("RIGHTCOL", 300, 500), ("KEEPBELOW", 100, 350));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            var spanningColumns = RectSpanning(page, "LEFTCOL", "RIGHTCOL");
            await DoDrag(window, spanningColumns, page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag across the column gap makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("LEFTCOL").And.NotContain("RIGHTCOL",
                "a box that crosses the gap between two columns must remove glyphs from BOTH runs under it");
            text.Should().Contain("KEEPABOVE").And.Contain("KEEPBELOW",
                "content outside the box's vertical range must survive");
            var saved = File.ReadAllBytes(outPath);
            SavedPdfLeakScanner.FindTerm(saved, "LEFTCOL").Should().BeEmpty(
                "the left column's glyphs must be removed, not merely hidden");
            SavedPdfLeakScanner.FindTerm(saved, "RIGHTCOL").Should().BeEmpty(
                "the right column's glyphs must be removed, not merely hidden");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1161: a box drawn against a page whose /CropBox is smaller than its
    /// /MediaBox must map through the CROP box's origin, not the media box's.
    /// The target sits close to the crop box's right edge -- the seam where an
    /// offset error in <see cref="PdfPage.VisualToContent"/> shows up as a
    /// missed (or wrongly shifted) redaction rather than a merely-imprecise one.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragNearCropBoxEdge_RemovesTargetPositionedAtTheVisibleBoundary()
    {
        const double cropLeft = 50, cropBottom = 50, cropRight = 550, cropTop = 750;
        var (dir, src) = NewPdf("cropedge");

        var font = PdfFont.Helvetica(18);
        var targetWidth = font.MeasureWidth("EDGESECRET");
        var targetX = cropRight - 4 - targetWidth; // right edge lands just inside the crop's right edge

        CreateLabeledPdfWithCropBox(src, new PdfRectangle(cropLeft, cropBottom, cropRight, cropTop),
            ("KEEPCENTER", 200, 400), ("EDGESECRET", targetX, 400));

        var (window, vm, viewer, overlay, page) = await OpenInRedactionMode(src);
        try
        {
            page.CropBox.Should().Be(new PdfRectangle(cropLeft, cropBottom, cropRight, cropTop),
                "the fixture must actually load a CropBox smaller than the MediaBox, or this proves nothing");

            await DoDrag(window, ContentRectOf(page, "EDGESECRET"), page, overlay);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                "one drag near the crop edge makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var text = SavedText(outPath);
            text.Should().NotContain("EDGESECRET",
                "a box near the crop box's edge must still map through the crop box's own origin to the right glyphs");
            text.Should().Contain("KEEPCENTER", "content away from the crop edge must survive");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), "EDGESECRET").Should().BeEmpty(
                "the near-edge redaction must remove the glyphs from the saved bytes, not just miss them visually");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// #1489: a box drawn around the text the user SEES, near the far corner of a
    /// large page at a render scale where <c>120 × zoom × dpr</c> is not an
    /// integer, removes exactly the glyphs inside it.
    /// </summary>
    /// <remarks>
    /// The other drags here aim through <c>ToViewerDips(…, 120)</c> — the input
    /// coordinate space itself — so they are blind to the drawn page disagreeing
    /// with that space. This one aims at INK found in the published raster and
    /// maps it through the Image's laid-out bounds, which is where the user's
    /// eye puts the box.
    /// <para>
    /// Before the fix, at zoom 0.5 × dpr 2.858 (scale 1.429) the 2000 pt page
    /// rendered at 171 DPI into 4750 px and was laid out 4750 × 96 / 137.18 =
    /// 3324.0 DIPs wide instead of 3333.3: 0.28% short, so ink at x ≈ 1700 pt was
    /// drawn about 4.8 pt left of where input maps. The survivor gap is chosen to
    /// sit between the pad and pad + that drift: with the default AnyOverlap rule
    /// a correct mapping clears the left survivor by ~2.4 pt and the drifted one
    /// clips its last glyph.
    /// </para>
    /// Verified with mutool (an independent extractor) and the decompressing
    /// saved-bytes scanner, not excise's own letters.
    /// </remarks>
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task DragAroundVisibleGlyphs_NearFarCornerOfLargePage_AtNonIntegerRenderScale_RemovesExactlyThem()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool is not installed; the far-corner drag is verified with an independent extractor (#1489).");

        const double widthPt = 2000, heightPt = 1400;
        const double fontSize = 18, baselineY = 150, targetX = 1700;
        const double leftGapPt = 2.5, rightGapPt = 4.0, padPt = 1.5;
        const string target = "FARSECRET", leftSurvivor = "KEEPL", rightSurvivor = "KEEPR";

        var (dir, src) = NewPdf("farcorner");
        using (var doc = PdfDocument.CreateNew())
        {
            var blank = doc.Pages.AddBlank(widthPt, heightPt);
            var font = PdfFont.Helvetica(fontSize);
            using (var graphics = blank.GetGraphics())
            {
                graphics.DrawString(leftSurvivor, font, PdfBrush.Black,
                    targetX - leftGapPt - font.MeasureWidth(leftSurvivor), baselineY);
                graphics.DrawString(target, font, PdfBrush.Black, targetX, baselineY);
                graphics.DrawString(rightSurvivor, font, PdfBrush.Black,
                    targetX + font.MeasureWidth(target) + rightGapPt, baselineY);
                graphics.Flush();
            }
            doc.Save(src);
        }

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 2000, Height = 1600 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            await WaitForIdleLayout(window);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = 2.858;
            vm.SetManualZoom(0.5);
            vm.IsRedactionMode = true;
            await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
            await WaitForIdleLayout(window);
            await WaitForFinalSinglePageRender(window, viewer);
            viewer.ZoomLevel.Should().BeApproximately(0.5, 1e-9, "the drift depends on zoom × dpr being 1.429");

            var page = vm.PdfCoreDocument!.GetPage(1);
            var image = viewer.SinglePagePart.PdfImage!;
            var bitmap = (WriteableBitmap)image.Source!;

            // Fixture guard: the survivor must sit between pad and pad + drift, or
            // this test is green on both sides of the fix and proves nothing.
            var targetBox = GlyphRunBox(page, target);
            var leftBox = GlyphRunBox(page, leftSurvivor);
            (targetBox.Left - leftBox.Right).Should().BeApproximately(leftGapPt, 0.25);

            // Where the target is DRAWN: its ink in the raster, searched inside its
            // own glyph cells (survivor ink starts at least 2.5 pt outside them).
            double pxPerPtX = bitmap.PixelSize.Width / page.VisualWidth;
            double pxPerPtY = bitmap.PixelSize.Height / page.VisualHeight;
            var ink = InkBounds(bitmap,
                (int)Math.Floor((targetBox.Left - 1) * pxPerPtX),
                (int)Math.Floor((heightPt - targetBox.Top - 2) * pxPerPtY),
                (int)Math.Ceiling((targetBox.Right + 1) * pxPerPtX),
                (int)Math.Ceiling((heightPt - targetBox.Bottom + 2) * pxPerPtY));
            ink.Width.Should().BeGreaterThan(0, "the target must be inked in the published raster");

            // The box the user draws: ink ± pad, in raster pixels, placed on screen
            // through the Image's laid-out bounds (Stretch=Fill).
            double dipPerPxX = image.Bounds.Width / bitmap.PixelSize.Width;
            double dipPerPxY = image.Bounds.Height / bitmap.PixelSize.Height;
            var startDip = new Point((ink.X - padPt * pxPerPtX) * dipPerPxX, (ink.Y - padPt * pxPerPtY) * dipPerPxY);
            var endDip = new Point((ink.Right + padPt * pxPerPtX) * dipPerPxX, (ink.Bottom + padPt * pxPerPtY) * dipPerPxY);
            var start = image.TranslatePoint(startDip, window)!.Value;
            var end = image.TranslatePoint(endDip, window)!.Value;
            new Rect(window.ClientSize).Contains(end).Should().BeTrue(
                $"the far-corner drag must land inside the window (end {end}, client {window.ClientSize})");

            await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));
            await WaitForIdleLayout(window);
            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle("one drag makes one pending redaction");

            var outPath = Path.Combine(dir, "out.pdf");
            await ApplyAndSave(vm, outPath);

            var extracted = MutoolTextExtractor.ExtractPage(outPath, 1);
            extracted.Should().NotBeNull("mutool must be able to read the redacted copy");
            var compact = new string(extracted!.Where(c => !char.IsWhiteSpace(c)).ToArray());
            compact.Should().Contain(leftSurvivor,
                $"the glyph just left of the box must survive: a drifted box clips its last letter (mutool read '{compact}')");
            compact.Should().Contain(rightSurvivor,
                $"the glyph just right of the box must survive (mutool read '{compact}')");
            compact.Should().NotContain(target,
                "an independent extractor must not read the glyphs the user boxed");
            compact.Replace(leftSurvivor, "").Replace(rightSurvivor, "").Should().BeEmpty(
                "no glyph of the boxed word may survive, even one the box only just covered");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outPath), target).Should().BeEmpty(
                "the boxed glyphs must be removed from the saved bytes, not hidden");
        }
        finally { window.Close(); }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static async Task WaitForFinalSinglePageRender(Window window, PdfViewerControl viewer)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (viewer.SinglePagePart.SinglePagePublishCount == 0 || viewer.IsLoading || viewer.SinglePagePart.SinglePagePlaceholderForTests != null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the single-page render never published");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        window.UpdateLayout();
    }

    /// <summary>The union of the glyph cells of the first run of letters spelling <paramref name="word"/>.</summary>
    private static PdfRectangle GlyphRunBox(PdfPage page, string word)
    {
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters!.ToList());
        var joined = string.Concat(ordered.Select(l => l.Value));
        var idx = joined.IndexOf(word, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, $"fixture must contain '{word}'");
        var run = ordered.Skip(idx).Take(word.Length).Select(l => l.GlyphRectangle.Normalize()).ToList();
        return new PdfRectangle(run.Min(r => r.Left), run.Min(r => r.Bottom), run.Max(r => r.Right), run.Max(r => r.Top));
    }

    /// <summary>Bounds, in raster pixels, of the dark pixels inside a search window.</summary>
    private static PixelRect InkBounds(WriteableBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        using var fb = bitmap.Lock();
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(fb.Size.Width, x1); y1 = Math.Min(fb.Size.Height, y1);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var row = new byte[fb.RowBytes];
        for (var y = y0; y < y1; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (var x = x0; x < x1; x++)
            {
                var p = x * 4;
                if (row[p] >= 128 || row[p + 1] >= 128 || row[p + 2] >= 128) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0 ? default : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static (string dir, string src) NewPdf(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-drag-broaden", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, $"{tag}.pdf"));
    }

    private static async Task<(MainWindow window, MainWindowViewModel vm, PdfViewerControl viewer, Canvas overlay, PdfPage page)>
        OpenInRedactionMode(string src)
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 2000, Height = 1600 };
        window.Show();
        await vm.LoadDocumentAsync(src);
        await WaitForIdleLayout(window);

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        vm.ZoomActualSizeCommand.Execute().Subscribe();
        vm.IsRedactionMode = true;
        await WaitForIdleLayout(window);

        var overlay = viewer.SinglePagePart.OverlayCanvas!;
        var page = vm.PdfCoreDocument!.GetPage(1);
        return (window, vm, viewer, overlay, page);
    }

    private static async Task DoDrag(Window window, PdfRectangle contentRect, PdfPage page, Canvas overlay)
    {
        var (start, end) = ToWindowDragPoints(contentRect, page, overlay, window);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));
        await WaitForIdleLayout(window);
    }

    private static async Task ApplyAndSave(MainWindowViewModel vm, string outPath)
    {
        await vm.ApplyRedactionsCommand();
        await vm.SaveDocumentCommand(outPath);
        File.Exists(outPath).Should().BeTrue("saving after mouse redactions must write an output PDF");
    }

    private static string SavedText(string path)
    {
        using var saved = PdfDocument.Open(File.ReadAllBytes(path));
        return string.Concat(saved.GetPage(1).Letters.Select(l => l.Value));
    }

    /// <summary>The content-space bounding box of the first run of letters spelling <paramref name="word"/>.</summary>
    private static PdfRectangle ContentRectOf(PdfPage page, string word)
    {
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters!.ToList());
        var joined = string.Concat(ordered.Select(l => l.Value));
        var idx = joined.IndexOf(word, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, $"fixture must contain '{word}'");
        var run = ordered.Skip(idx).Take(word.Length).Select(l => l.GlyphRectangle).ToList();

        double left = run.Min(r => Math.Min(r.Left, r.Right)) - 3;
        double right = run.Max(r => Math.Max(r.Left, r.Right)) + 3;
        double bottom = run.Min(r => Math.Min(r.Bottom, r.Top)) - 3;
        double top = run.Max(r => Math.Max(r.Bottom, r.Top)) + 3;
        return new PdfRectangle(left, bottom, right, top);
    }

    /// <summary>The union of two words' content-space boxes -- a single drag rectangle spanning both (#1161).</summary>
    private static PdfRectangle RectSpanning(PdfPage page, string wordA, string wordB)
    {
        var a = ContentRectOf(page, wordA);
        var b = ContentRectOf(page, wordB);
        return new PdfRectangle(
            Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom),
            Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));
    }

    private static (Point Start, Point End) ToWindowDragPoints(
        PdfRectangle contentRect, PdfPage page, Canvas overlay, Window window)
    {
        var viewerRect = PdfCoordinateMapper.ToViewerDips(
            page, PdfPageRect.FromContentPoints(page.PageNumber, contentRect), RenderDpi);
        var start = overlay.TranslatePoint(new Point(viewerRect.X, viewerRect.Y), window) ?? default;
        var end = overlay.TranslatePoint(new Point(viewerRect.Right, viewerRect.Y2), window) ?? default;
        return (start, end);
    }

    private static void CreateLabeledPdf(string path, int rotation, params (string Word, double X, double Y)[] items)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using (var graphics = page.GetGraphics())
        {
            var font = PdfFont.Helvetica(18);
            foreach (var (word, x, y) in items)
                graphics.DrawString(word, font, PdfBrush.Black, x, y);
            graphics.Flush();
        }
        if (rotation != 0)
            page.Rotation = rotation;
        doc.Save(path);
    }

    /// <summary>Like <see cref="CreateLabeledPdf"/>, but two pages, one set of words each (#1161).</summary>
    private static void CreateTwoPageLabeledPdf(
        string path,
        (string Word, double X, double Y)[] page1Items,
        (string Word, double X, double Y)[] page2Items)
    {
        using var doc = PdfDocument.CreateNew();
        var font = PdfFont.Helvetica(18);
        foreach (var items in new[] { page1Items, page2Items })
        {
            var page = doc.Pages.AddBlank();
            using var graphics = page.GetGraphics();
            foreach (var (word, x, y) in items)
                graphics.DrawString(word, font, PdfBrush.Black, x, y);
            graphics.Flush();
        }
        doc.Save(path);
    }

    /// <summary>Like <see cref="CreateLabeledPdf"/>, but with an explicit /CropBox smaller than the /MediaBox (#1161).</summary>
    private static void CreateLabeledPdfWithCropBox(string path, PdfRectangle cropBox, params (string Word, double X, double Y)[] items)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using (var graphics = page.GetGraphics())
        {
            var font = PdfFont.Helvetica(18);
            foreach (var (word, x, y) in items)
                graphics.DrawString(word, font, PdfBrush.Black, x, y);
            graphics.Flush();
        }
        page.Dictionary["CropBox"] = new PdfArray(new PdfObject[]
        {
            new PdfInteger((long)cropBox.Left), new PdfInteger((long)cropBox.Bottom),
            new PdfInteger((long)cropBox.Right), new PdfInteger((long)cropBox.Top),
        });
        doc.Save(path);
    }

    private static async Task WaitForIdleLayout(Window window)
    {
        for (var i = 0; i < 12; i++) { await Task.Delay(100); window.UpdateLayout(); }
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
            foreach (var child in p.Children)
                if (child is Control c && FindNamedDescendant<T>(c, name) is { } hit) return hit;
        if (root is Decorator d && d.Child is Control dc && FindNamedDescendant<T>(dc, name) is { } dh) return dh;
        if (root is ContentControl cc && cc.Content is Control ccc && FindNamedDescendant<T>(ccc, name) is { } ch) return ch;
        return root.FindControl<T>(name);
    }
}
