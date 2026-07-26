using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Editing;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;
using PdfRectangle = Excise.Core.Document.PdfRectangle;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #780: pointer-driven type-over creation, DIP↔PDF coordinate round-trips
/// (incl. rotated pages and page clamp), and Esc-to-cancel of the active box.
/// These paths were entirely untested — every prior typewriter test called the
/// ViewModel directly, so the control's coordinate mapping and the interaction
/// gesture path had no coverage.
/// </summary>
[Collection("AvaloniaTests")]
public class PdfViewerControlTypewriterTests
{
    private static PdfCoreDocument NewSinglePage(double w = 300, double h = 400, int rotation = 0)
    {
        var doc = PdfCoreDocument.CreateNew();
        doc.Pages.AddBlank(w, h);
        if (rotation != 0)
            doc.GetPage(1).Rotation = rotation;
        return doc;
    }

    private static PdfViewerControl NewTypewriterControl(PdfCoreDocument doc)
    {
        var control = new PdfViewerControl();
        control.Document = doc;
        control.CurrentPage = 1;
        control.InteractionMode = InteractionMode.Typewriter;
        return control;
    }

    [FixedAvaloniaFact]
    public async Task ClickWithoutDrag_PlacesADefaultSizedBox()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var doc = NewSinglePage();
            var control = NewTypewriterControl(doc);

            TypewriterTextCreatedEventArgs? created = null;
            control.TypewriterTextCreated += (_, e) => created = e;

            // A plain click: start == end (no drag).
            control.CreateTypewriterTextFromPointer(new Point(80, 90), new Point(80, 90));

            created.Should().NotBeNull("a click must place a box, not nothing (#780)");
            created!.PageNumber.Should().Be(1);
            created.Rect.Width.Should().BeGreaterThan(0);
            created.Rect.Height.Should().BeGreaterThan(0);
        });
    }

    [FixedAvaloniaFact]
    public async Task Drag_PlacesABoxSizedToTheGesture_LargerThanAClick()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var doc = NewSinglePage();
            var control = NewTypewriterControl(doc);

            PdfRectangle click = default, drag = default;
            control.TypewriterTextCreated += (_, e) => { if (click == default) click = e.Rect; else drag = e.Rect; };

            control.CreateTypewriterTextFromPointer(new Point(20, 20), new Point(20, 20));      // click
            control.CreateTypewriterTextFromPointer(new Point(20, 20), new Point(260, 200));    // wide drag

            drag.Width.Should().BeGreaterThan(click.Width, "a drag sizes the box to the gesture");
            drag.Height.Should().BeGreaterThan(click.Height);
        });
    }

    [FixedAvaloniaFact]
    public async Task DipToPdf_FlipsYAxis_TopOfScreenMapsToTopOfPage()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var doc = NewSinglePage(300, 400);
            var control = NewTypewriterControl(doc);

            // PDF origin is bottom-left; a box near the TOP of the screen (small
            // DIP Y) must map to a LARGE PDF Top. Compare a top-placed box with
            // a bottom-placed box.
            var top = control.ViewerDipsToPdfRect(new Rect(10, 5, 100, 40), 1);
            var bottom = control.ViewerDipsToPdfRect(new Rect(10, 300, 100, 40), 1);

            top.Top.Should().BeGreaterThan(bottom.Top,
                "screen-top must map to page-top under the PDF Y-flip");
        });
    }

    [FixedAvaloniaTheory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task DipPdfRoundTrip_IsIdentity_AtEveryRotation(int rotation)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var doc = NewSinglePage(300, 400, rotation);
            var control = NewTypewriterControl(doc);

            // A box comfortably inside the page and above the minimum size, so
            // neither clamp nor min-size substitution perturbs the round-trip.
            var original = new PdfRectangle(60, 120, 180, 170);

            var dip = control.PdfRectToViewerDips(original, 1);
            var roundTripped = control.ViewerDipsToPdfRect(dip, 1);

            roundTripped.Left.Should().BeApproximately(original.Left, 1.5,
                $"content→viewer→content must be identity at /Rotate {rotation}");
            roundTripped.Bottom.Should().BeApproximately(original.Bottom, 1.5);
            roundTripped.Right.Should().BeApproximately(original.Right, 1.5);
            roundTripped.Top.Should().BeApproximately(original.Top, 1.5);
        });
    }

    [FixedAvaloniaFact]
    public async Task NormalizeDipRect_ClampsBoxOntoThePage()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var doc = NewSinglePage(300, 400);
            var control = NewTypewriterControl(doc);

            // The page's DIP extent, derived from the control itself (a request
            // larger than the page clamps to the page's width/height at 0,0).
            var full = control.NormalizeTypewriterDipRect(new Rect(0, 0, 100000, 100000));
            var pageWidthDips = full.Width;
            var pageHeightDips = full.Height;

            // A box dragged far off the right/bottom edge must be pulled back so
            // it stays fully on the page — otherwise the typed text lands off-page.
            var clamped = control.NormalizeTypewriterDipRect(new Rect(100000, 100000, 120, 40));

            (clamped.X + clamped.Width).Should().BeLessThanOrEqualTo(pageWidthDips + 0.5);
            (clamped.Y + clamped.Height).Should().BeLessThanOrEqualTo(pageHeightDips + 0.5);
            clamped.X.Should().BeGreaterThanOrEqualTo(0);
            clamped.Y.Should().BeGreaterThanOrEqualTo(0);
        });
    }

    [FixedAvaloniaFact]
    public async Task Escape_OnEmptyActiveBox_RemovesIt()
    {
        await RunEscapeCase(initialText: "", expectDeleted: true);
    }

    [FixedAvaloniaFact]
    public async Task Escape_OnNonEmptyActiveBox_KeepsTypedText()
    {
        await RunEscapeCase(initialText: "typed content", expectDeleted: false);
    }

    // #780: the move-handle and resize-grip pointer gestures raise
    // TypewriterTextBoundsChanged with PDF-mapped bounds, but the whole
    // handler → RaiseTypewriterBoundsChanged → DIP↔PDF path had no coverage.
    // These drive a real routed pointer gesture (press → move → release) on the
    // handle Border found in the visual tree and assert the reported PDF bounds
    // reflect the drag. The move/resize handlers report pointer positions
    // relative to the TypewriterLayer, so we raise the events with
    // rootVisual == that same Canvas: GetPosition(layer) is then an identity
    // transform and the handlers observe exactly the delta we inject. The
    // control must be hosted in a shown Window first — an identity transform
    // still requires the Canvas to be attached to a visual root, or
    // GetPosition collapses to (0,0) and no gesture is seen.
    [FixedAvaloniaFact]
    public async Task DragMoveHandle_ShiftsPdfPosition_AndPreservesSize()
    {
        // Drag the move handle by (+30, +24) DIP. The exact DIP→PDF scale
        // depends on the preview render DPI/zoom the control chose for the host
        // window, so the precise magnitude is checked against `expected` —
        // computed at gesture time by mapping the shell's actual moved DIP
        // position through the same conversion the event uses. Independently, we
        // assert the box really shifted (right + down, PDF Y flipped) and kept
        // its size — neither of which depends on the scale.
        var (original, changed, expected) = await RunHandleDragCase(
            pick: shell => FindBorder(shell, b => Math.Abs(b.Height - 10) < 0.5),
            deltaDip: new Point(30, 24));

        changed.Should().NotBeNull("releasing the move handle must raise TypewriterTextBoundsChanged");
        var r = changed!.Rect;

        r.Width.Should().BeApproximately(original.Width, 2.0, "a move preserves the box size");
        r.Height.Should().BeApproximately(original.Height, 2.0);
        r.Left.Should().BeGreaterThan(original.Left + 10, "a rightward drag moves the PDF box right");
        r.Bottom.Should().BeLessThan(original.Bottom - 8, "a downward DIP drag lowers PDF Y (flip)");
        r.Left.Should().BeApproximately(expected.Left, 1.0, "the reported bounds equal the mapped moved position");
        r.Bottom.Should().BeApproximately(expected.Bottom, 1.0);
    }

    [FixedAvaloniaFact]
    public async Task DragResizeGrip_ChangesPdfSize_AndPreservesAnchoredCorner()
    {
        // Grow via the bottom-right grip by (+40, +40) DIP = (+24, +24) PDF pts.
        // The DIP top-left corner is anchored, which in PDF is (Left, Top).
        var (original, changed, expected) = await RunHandleDragCase(
            pick: shell => FindBorder(shell, b => Math.Abs(b.Width - 12) < 0.5 && Math.Abs(b.Height - 12) < 0.5),
            deltaDip: new Point(40, 40));

        changed.Should().NotBeNull("releasing the resize grip must raise TypewriterTextBoundsChanged");
        var r = changed!.Rect;

        r.Left.Should().BeApproximately(original.Left, 3.0, "the anchored top-left corner's Left is fixed");
        r.Top.Should().BeApproximately(original.Top, 3.0, "the anchored top-left corner's Top is fixed");
        r.Width.Should().BeGreaterThan(original.Width + 15, "growing the grip widens the PDF box");
        r.Height.Should().BeGreaterThan(original.Height + 15, "growing the grip heightens the PDF box");
        r.Width.Should().BeApproximately(expected.Width, 1.0, "the reported bounds equal the mapped resized box");
        r.Height.Should().BeApproximately(expected.Height, 1.0);
    }

    // Hosts the control in a shown Window (so the overlay Canvas is attached and
    // GetPosition resolves), seeds one on-page pending box, runs the pointer
    // gesture on the picked handle, and returns the seeded bounds plus the
    // bounds-changed event raised on release.
    private static async Task<(PdfRectangle original, TypewriterTextBoundsChangedEventArgs? changed, PdfRectangle expected)> RunHandleDragCase(
        Func<Control, Border> pick, Point deltaDip)
    {
        var original = new PdfRectangle(60, 150, 200, 250); // w=140, h=100, on a 300x400 page
        PdfCoreDocument? doc = null;
        PdfViewerControl control = null!;
        Window window = null!;
        TypewriterTextBoundsChangedEventArgs? changed = null;
        PdfRectangle expected = default;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = NewSinglePage(300, 400);
            control = NewTypewriterControl(doc);
            control.TypewriterTextBoundsChanged += (_, e) => changed = e;
            window = new Window { Content = control, Width = 700, Height = 800 };
            window.Show();
        });

        // Wait for the overlay Canvas to attach and lay out before injecting the
        // gesture (the identity transform needs a live visual root).
        Canvas? layer = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                layer = control.FindControl<Canvas>("TypewriterLayer");
            });
            if (layer is not null && layer.IsAttachedToVisualTree())
                break;
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.TypewriterTextOperations = new[]
            {
                PdfTypewriterTextOperation.Create(1, original, "handle"),
            };
            window.UpdateLayout();

            var currentLayer = control.FindControl<Canvas>("TypewriterLayer")!;
            currentLayer.IsAttachedToVisualTree().Should().BeTrue("the overlay must be attached for the gesture");

            var shell = currentLayer.GetVisualDescendants().OfType<Grid>().First();
            var handle = pick(shell);
            RaiseHandleDrag(currentLayer, handle, deltaDip);

            // The gesture leaves the shell at its final DIP geometry; map it the
            // same way RaiseTypewriterBoundsChanged does so the reported bounds
            // can be checked without hard-coding the preview DPI/zoom scale.
            var finalRect = new Rect(
                Canvas.GetLeft(shell), Canvas.GetTop(shell), shell.Width, shell.Height);
            expected = control.ViewerDipsToPdfRect(
                control.NormalizeTypewriterDipRect(finalRect), 1);
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Close();
            doc?.Dispose();
        });

        return (original, changed, expected);
    }

    private static Border FindBorder(Control shell, Func<Border, bool> predicate)
    {
        var border = shell.GetVisualDescendants().OfType<Border>().FirstOrDefault(predicate);
        border.Should().NotBeNull("the target handle Border must be in the editor visual tree");
        return border!;
    }

    // Press → move → release on <paramref name="handle"/>, reporting pointer
    // positions relative to <paramref name="layer"/>. rootVisual == layer makes
    // GetPosition(layer) an identity, so the injected delta is exactly what the
    // move/resize handlers observe (they are delta-based off the press point, so
    // the absolute press position is irrelevant).
    private static void RaiseHandleDrag(Canvas layer, Border handle, Point deltaDip)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var pressProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var start = new Point(100, 100);
        var end = start + deltaDip;

        handle.RaiseEvent(new PointerPressedEventArgs(
            handle, pointer, layer, start, 0, pressProps, KeyModifiers.None));

        handle.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, handle, pointer, layer, end, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

        handle.RaiseEvent(new PointerReleasedEventArgs(
            handle, pointer, layer, end, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static async Task RunEscapeCase(string initialText, bool expectDeleted)
    {
        PdfCoreDocument? doc = null;
        var control = new PdfViewerControl();
        var deletedFired = false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            doc = NewSinglePage();
            control.Document = doc;
            control.CurrentPage = 1;
            control.InteractionMode = InteractionMode.Typewriter;
            control.TypewriterTextDeleted += (_, _) => deletedFired = true;
            control.TypewriterTextOperations = new[]
            {
                PdfTypewriterTextOperation.Create(1, new PdfRectangle(40, 250, 240, 290), initialText),
            };
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var layer = control.FindControl<Canvas>("TypewriterLayer");
            layer.Should().NotBeNull();
            var textBox = layer!.GetVisualDescendants().OfType<TextBox>().Single();

            textBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Route = RoutingStrategies.Bubble,
                Key = Key.Escape,
            });
        });

        deletedFired.Should().Be(expectDeleted,
            expectDeleted
                ? "Esc on an empty box removes it"
                : "Esc must never silently drop typed text");

        doc?.Dispose();
    }
}
