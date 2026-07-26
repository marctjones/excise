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
