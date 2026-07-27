using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// GUI coverage for the text-selection highlight ("selection box"), #815.
///
/// These drive a REAL routed pointer gesture (press at a known glyph → drag to
/// another glyph) through the control's interaction pipeline and assert that the
/// TextSelectionLayer acquires highlight rectangles at the correct ON-SCREEN
/// position.
///
/// The position oracle is deliberately NOT <c>PdfRectangleToDips</c> compared to
/// itself (that is tautological and blind to the exact "highlight far to the
/// left" layout regression the axaml <c>ZoomHost</c> centering fixes). Instead we
/// verify the drawn rectangles' real layout geometry: (1) the highlight canvas
/// and the page image share an origin, and (2) each drawn rect lands at the
/// glyph's MediaBox fraction inside the page image's bounds — computed
/// independently from PDF content coordinates. An origin/centering offset trips
/// both.
/// </summary>
[Collection("AvaloniaTests")]
public class PdfViewerSelectionTests
{
    [FixedAvaloniaFact]
    public async Task SinglePageDrag_DrawsHighlightOverSelectedGlyphs_AtCorrectOnPagePositions()
    {
        // Text placed well away from the left/top edges so an origin/centering
        // offset in the overlay would push the highlight off the glyphs.
        var bytes = TestPdfGenerator.CreatePdfWithTextAtPosition("SELECTME", x: 220, y: 500, fontSize: 24);

        PdfCoreDocument doc = null!;
        PdfViewerControl control = null!;
        Window window = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = PdfCoreDocument.Open(bytes);
            control = new PdfViewerControl { Document = doc, CurrentPage = 1 };
            window = new Window { Content = control, Width = 900, Height = 1000 };
            window.Show();
        });

        // Wait until the page image has rendered (real Bounds) and the page's
        // letters are available.
        Image? pdfImage = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                pdfImage = control.FindControl<Image>("PdfImage");
                ready = pdfImage is { } img
                        && img.IsAttachedToVisualTree()
                        && img.Bounds.Width > 1
                        && doc.GetPage(1).Letters.Count > 0;
            });
            if (ready) break;
            await Task.Delay(100);
        }

        var letters = doc.GetPage(1).Letters;
        letters.Should().NotBeEmpty("the test PDF has extractable glyphs to select");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            control.InteractionMode = InteractionMode.TextSelection;

            var overlay = control.FindControl<Canvas>("OverlayCanvas")!;
            overlay.IsAttachedToVisualTree().Should().BeTrue();

            // Press at the visually leftmost glyph, drag to the rightmost. Pointer
            // positions are reported relative to the OverlayCanvas (the control
            // reads the press point off it), so rootVisual == overlay makes
            // GetPosition an identity and the injected DIP point is what the
            // hit-test sees.
            var leftmost = letters.OrderBy(l => l.GlyphRectangle.Left).First();
            var rightmost = letters.OrderByDescending(l => l.GlyphRectangle.Right).First();
            var anchorDip = control.GlyphRectToViewerDipsForTest(leftmost.GlyphRectangle);
            var focusDip = control.GlyphRectToViewerDipsForTest(rightmost.GlyphRectangle);
            RaiseSelectionDrag(overlay, anchorDip.Center, focusDip.Center);
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var layer = control.FindControl<Canvas>("TextSelectionLayer")!;
            var overlay = control.FindControl<Canvas>("OverlayCanvas")!;
            var img = control.FindControl<Image>("PdfImage")!;

            var rects = layer.Children.OfType<Rectangle>().ToList();

            // 1) A real, multi-glyph selection was drawn.
            rects.Should().NotBeEmpty("dragging across the line must draw a highlight");
            rects.Count.Should().BeGreaterThanOrEqualTo(2,
                "dragging from the leftmost to the rightmost glyph highlights several glyphs");

            // 2) Layout oracle A — the highlight layer and the page image share an
            //    origin. The "highlight far to the left" regression breaks exactly
            //    this (overlay anchored at the widened grid's left edge while the
            //    image centers), and PdfRectangleToDips cannot see it.
            var sharedOrigin = overlay.TranslatePoint(new Point(0, 0), img);
            sharedOrigin.Should().NotBeNull();
            sharedOrigin!.Value.X.Should().BeApproximately(0, 1.5,
                "the selection overlay and the page image must share a horizontal origin");
            sharedOrigin.Value.Y.Should().BeApproximately(0, 1.5,
                "the selection overlay and the page image must share a vertical origin");

            // 3) Layout oracle B — EVERY drawn highlight lands on a real glyph.
            //    Each letter's expected on-screen rect is computed independently
            //    from PDF content coordinates (visual = content shifted to the
            //    MediaBox origin, Y flipped; rotation-0 page) as a fraction of the
            //    page image bounds. A drawn rect, translated into the page image's
            //    own coordinate space, must have its center inside one of those
            //    expected glyph rects. An origin/centering offset moves the drawn
            //    rects off every glyph and trips this — PdfRectangleToDips cannot.
            var page = doc.GetPage(1);
            var mb = page.MediaBox.Normalize();
            double imgW = img.Bounds.Width, imgH = img.Bounds.Height;
            imgW.Should().BeGreaterThan(0);

            Rect ExpectedGlyphImageRect(Excise.Core.Text.Letter l)
            {
                var g = l.GlyphRectangle;
                double x0 = (g.Left - mb.Left) / mb.Width * imgW;
                double y0 = (mb.Top - g.Top) / mb.Height * imgH;
                double x1 = (g.Right - mb.Left) / mb.Width * imgW;
                double y1 = (mb.Top - g.Bottom) / mb.Height * imgH;
                return new Rect(x0, y0, x1 - x0, y1 - y0);
            }
            var expectedGlyphRects = letters.Select(ExpectedGlyphImageRect).ToList();
            double pad = Math.Max(4.0, imgW * 0.02); // glyph-metric slack

            foreach (var r in rects)
            {
                var tl = r.TranslatePoint(new Point(0, 0), img);
                tl.Should().NotBeNull("each highlight rect must be resolvable into page-image space");
                var drawn = new Rect(tl!.Value.X, tl.Value.Y, r.Bounds.Width, r.Bounds.Height);

                // On-page.
                drawn.X.Should().BeGreaterThanOrEqualTo(-pad, "highlight must not spill off the left of the page");
                drawn.Y.Should().BeGreaterThanOrEqualTo(-pad, "highlight must not spill off the top of the page");
                drawn.Right.Should().BeLessThanOrEqualTo(imgW + pad, "highlight must not spill off the right of the page");
                drawn.Bottom.Should().BeLessThanOrEqualTo(imgH + pad, "highlight must not spill off the bottom of the page");

                // Sits on a real glyph.
                var c = drawn.Center;
                expectedGlyphRects.Should().Contain(
                    e => c.X >= e.X - pad && c.X <= e.Right + pad && c.Y >= e.Y - pad && c.Y <= e.Bottom + pad,
                    "every drawn highlight rect must sit over one of the page's glyphs, not off to the side");
            }

            // The highlight as a whole spans a meaningful width of the line (a
            // left-to-right drag, not a single-glyph blip).
            var drawnBBoxLeft = rects.Min(r => r.TranslatePoint(new Point(0, 0), img)!.Value.X);
            var drawnBBoxRight = rects.Max(r => r.TranslatePoint(new Point(0, 0), img)!.Value.X + r.Bounds.Width);
            (drawnBBoxRight - drawnBBoxLeft).Should().BeGreaterThan(imgW * 0.05,
                "a left-to-right drag across the line produces a wide highlight");
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Close();
            doc.Dispose();
        });
    }

    [FixedAvaloniaFact]
    public async Task ClearSelectionHighlight_EmptiesTheLayer()
    {
        var bytes = TestPdfGenerator.CreatePdfWithTextAtPosition("CLEARME", x: 200, y: 480, fontSize: 24);

        PdfCoreDocument doc = null!;
        PdfViewerControl control = null!;
        Window window = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = PdfCoreDocument.Open(bytes);
            control = new PdfViewerControl { Document = doc, CurrentPage = 1 };
            window = new Window { Content = control, Width = 900, Height = 1000 };
            window.Show();
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                var img = control.FindControl<Image>("PdfImage");
                ready = img is { } i && i.Bounds.Width > 1 && doc.GetPage(1).Letters.Count > 0;
            });
            if (ready) break;
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            control.InteractionMode = InteractionMode.TextSelection;
            var overlay = control.FindControl<Canvas>("OverlayCanvas")!;
            var letters = doc.GetPage(1).Letters;
            var a = control.GlyphRectToViewerDipsForTest(letters[0].GlyphRectangle);
            var f = control.GlyphRectToViewerDipsForTest(letters[^1].GlyphRectangle);
            RaiseSelectionDrag(overlay, a.Center, f.Center);

            control.FindControl<Canvas>("TextSelectionLayer")!.Children.OfType<Rectangle>()
                .Should().NotBeEmpty("a drag drew a highlight");

            control.ClearSelectionHighlight();

            control.FindControl<Canvas>("TextSelectionLayer")!.Children
                .Should().BeEmpty("ClearSelectionHighlight removes every highlight rect");
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Close();
            doc.Dispose();
        });
    }

    // ── Continuous (reading) view selection (#815) ──────────────────────────

    [FixedAvaloniaFact]
    public async Task ContinuousViewDrag_DrawsPerPageHighlight_OverSelectedGlyphs()
    {
        var bytes = TestPdfGenerator.CreatePdfWithTextAtPosition("READINGVIEW", x: 180, y: 520, fontSize: 26);

        PdfCoreDocument doc = null!;
        PdfViewerControl control = null!;
        Window window = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = PdfCoreDocument.Open(bytes);
            control = new PdfViewerControl { Document = doc, CurrentPage = 1 };
            control.ViewMode = PdfViewMode.Continuous;
            window = new Window { Content = control, Width = 900, Height = 1100 };
            window.Show();
        });

        // Wait for continuous slots to exist and the items panel to lay out.
        ItemsControl items = null!;
        PdfPageSlot slot = null!;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                items = control.FindControl<ItemsControl>("ContinuousItems")!;
                slot = items.ItemsSource?.Cast<PdfPageSlot>().FirstOrDefault()!;
                ready = slot != null && items.Bounds.Width > 1 && slot.DisplayWidth > 1
                        && doc.GetPage(1).Letters.Count > 0;
            });
            if (ready) break;
            await Task.Delay(100);
        }

        slot.Should().NotBeNull("continuous view builds one slot for the single page");
        var letters = doc.GetPage(1).Letters;
        letters.Should().NotBeEmpty();

        double zoom = 0, itemsWidth = 0, topDip = 0, dispW = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            zoom = control.ZoomLevel;
            itemsWidth = items.Bounds.Width;
            topDip = slot.TopDip;
            dispW = slot.DisplayWidth;
        });

        var page = doc.GetPage(1);
        var mb = page.MediaBox.Normalize();
        double upp = PdfViewerControl.PointsToDip * zoom;   // page-local DIP per PDF point
        double xOffset = Math.Max(0, (itemsWidth - dispW) / 2);

        // Rotation-0 page: page-local DIP of a content point. Items-coordinate =
        // page-local + centering x-offset + slot top.
        Point ItemsCenter(Excise.Core.Text.Letter l)
        {
            var g = l.GlyphRectangle;
            double localX = ((g.Left + g.Right) / 2 - mb.Left) * upp;
            double localY = (mb.Top - (g.Top + g.Bottom) / 2) * upp;
            return new Point(xOffset + localX, topDip + localY);
        }

        var leftmost = letters.OrderBy(l => l.GlyphRectangle.Left).First();
        var rightmost = letters.OrderByDescending(l => l.GlyphRectangle.Right).First();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.InteractionMode = InteractionMode.TextSelection;
            // Text selection must NOT force single-page anymore (#815).
            control.ViewMode.Should().Be(PdfViewMode.Continuous,
                "text selection is a read affordance and stays in the continuous reading view");

            RaiseSelectionDrag(items, ItemsCenter(leftmost), ItemsCenter(rightmost));
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The page's slot got highlight rects; no other slot did.
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            var target = slots.Single(s => s.PageNumber == 1);
            target.SelectionRects.Count.Should().BeGreaterThanOrEqualTo(2,
                "dragging across the line highlights several glyphs on that page");
            slots.Where(s => s.PageNumber != 1).Should().OnlyContain(s => s.SelectionRects.Count == 0,
                "only the page under the drag is highlighted");

            // Each highlight rect sits over a real glyph, at its independently
            // computed page-local position (visual = content shifted to MediaBox
            // origin, Y flipped; rotation-0 page). This is not ToContinuousDips
            // compared to itself — the expected geometry is derived by hand.
            Rect ExpectedLocal(Excise.Core.Text.Letter l)
            {
                var g = l.GlyphRectangle;
                double x = (g.Left - mb.Left) * upp;
                double y = (mb.Top - g.Top) * upp;
                return new Rect(x, y, g.Width * upp, g.Height * upp);
            }
            var expected = letters.Select(ExpectedLocal).ToList();
            double pad = Math.Max(4.0, dispW * 0.02);

            foreach (var hl in target.SelectionRects)
            {
                // On-page.
                hl.X.Should().BeGreaterThanOrEqualTo(-pad);
                hl.Y.Should().BeGreaterThanOrEqualTo(-pad);
                (hl.X + hl.Width).Should().BeLessThanOrEqualTo(slot.DisplayWidth + pad);
                (hl.Y + hl.Height).Should().BeLessThanOrEqualTo(slot.DisplayHeight + pad);

                // Centered on a real glyph.
                double cx = hl.X + hl.Width / 2, cy = hl.Y + hl.Height / 2;
                expected.Should().Contain(
                    e => cx >= e.X - pad && cx <= e.Right + pad && cy >= e.Y - pad && cy <= e.Bottom + pad,
                    "every continuous-view highlight rect must sit over one of the page's glyphs");
            }
        });

        // The bound data renders as real, POSITIONED Rectangles — this catches a
        // silent ReflectionBinding failure that would collapse every highlight to
        // (0,0) while the SelectionRects data still looked correct.
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var target = items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 1);
            var container = items.GetRealizedContainers()
                .FirstOrDefault(c => (c.DataContext as PdfPageSlot)?.PageNumber == 1);
            container.Should().NotBeNull("page 1 is at the top of the reading view and must be realized");

            var drawn = container!.GetVisualDescendants().OfType<Rectangle>().ToList();
            drawn.Count.Should().Be(target.SelectionRects.Count,
                "every bound highlight becomes a rendered Rectangle");

            // Each rendered highlight is actually POSITIONED (the ReflectionBinding
            // Canvas.Left/Top ran) at its bound page-local X — translating the
            // Rectangle into the page Border (page-local space) lands near a bound
            // X, not collapsed to 0. (~2px slack for the Border thickness.)
            var pageBorder = container!.GetVisualDescendants().OfType<Border>().First();
            var xs = drawn
                .Select(r => r.TranslatePoint(new Point(0, 0), pageBorder))
                .Where(p => p.HasValue).Select(p => p!.Value.X).ToList();
            xs.Should().HaveCount(drawn.Count, "every highlight resolves into the page border");
            xs.Should().Contain(v => v > 1,
                "highlight Rectangles are offset horizontally, not collapsed to x=0");
            xs.Should().OnlyContain(
                v => target.SelectionRects.Any(r => Math.Abs(r.X - v) < 2.5),
                "each rendered highlight's position matches a bound highlight X");
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.ClearContinuousSelectionHighlight();
            items.ItemsSource!.Cast<PdfPageSlot>()
                .Should().OnlyContain(s => s.SelectionRects.Count == 0,
                    "ClearContinuousSelectionHighlight empties every page's highlight");

            window.Close();
            doc.Dispose();
        });
    }

    [FixedAvaloniaFact]
    public async Task ContinuousViewSelection_DoesNotForceSinglePage_AndStaysOnAnchorPageAcrossPages()
    {
        // A two-page doc; dragging from page 1 into page 2 must not throw and the
        // bounded first version keeps the selection on the anchor (press) page.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise_sel_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);
        var bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);

        PdfCoreDocument doc = null!;
        PdfViewerControl control = null!;
        Window window = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = PdfCoreDocument.Open(bytes);
            control = new PdfViewerControl { Document = doc };
            control.ViewMode = PdfViewMode.Continuous;
            window = new Window { Content = control, Width = 900, Height = 1100 };
            window.Show();
        });

        ItemsControl items = null!;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                items = control.FindControl<ItemsControl>("ContinuousItems")!;
                ready = items.ItemsSource?.Cast<PdfPageSlot>().Count() == 2 && items.Bounds.Width > 1;
            });
            if (ready) break;
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.InteractionMode = InteractionMode.TextSelection;
            control.ViewMode.Should().Be(PdfViewMode.Continuous,
                "entering text selection must NOT force single-page in the reading view (#815)");

            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            var p1 = slots[0];
            var p2 = slots[1];
            double margin1 = Math.Max(0, (items.Bounds.Width - p1.DisplayWidth) / 2);

            // Press near the top of page 1, drag down into page 2's area. Should
            // not throw; the selection stays bounded to page 1 (or is empty), and
            // page 2 is never highlighted.
            var start = new Point(margin1 + p1.DisplayWidth / 2, p1.TopDip + 20);
            var end = new Point(margin1 + p2.DisplayWidth / 2, p2.TopDip + p2.DisplayHeight / 2);

            System.Action drag = () => RaiseSelectionDrag(items, start, end);
            drag.Should().NotThrow("a cross-page drag must not crash the reading view");

            p2.SelectionRects.Count.Should().Be(0,
                "the bounded first version keeps a selection on its anchor page; page 2 is never highlighted");
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Close();
            doc.Dispose();
        });
    }

    /// <summary>
    /// Press → move → release reported relative to <paramref name="rootVisual"/>,
    /// so GetPosition(rootVisual) is an identity and the injected DIP points are
    /// exactly what the selection hit-test observes.
    /// </summary>
    private static void RaiseSelectionDrag(Control rootVisual, Point start, Point end)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        rootVisual.RaiseEvent(new PointerPressedEventArgs(
            rootVisual, pointer, rootVisual, start, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

        rootVisual.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, rootVisual, pointer, rootVisual, end, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

        rootVisual.RaiseEvent(new PointerReleasedEventArgs(
            rootVisual, pointer, rootVisual, end, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }
}
