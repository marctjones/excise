using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.App.Tests.Utilities;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// Basic GUI verification tests for PdfViewerControl.
/// Ensures the control can be instantiated, loaded with documents,
/// and responds correctly to zoom/navigation commands.
/// </summary>
[Collection("AvaloniaTests")]
public class PdfViewerControlTests
{
    private const string TestPdfPath = "TestData/simple.pdf";

    #region Instantiation Tests

    [FixedAvaloniaFact]
    public void PdfViewerControl_CanBeInstantiated()
    {
        // Act
        var control = new PdfViewerControl();

        // Assert
        control.Should().NotBeNull();
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_HasDefaultProperties()
    {
        // Arrange & Act
        var control = new PdfViewerControl();

        // Assert
        control.ZoomLevel.Should().Be(1.0);
        control.CurrentPage.Should().Be(1);
        control.InteractionMode.Should().Be(InteractionMode.None);
        control.Document.Should().BeNull();
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_IsKeyboardFocusable_AndExposesAutomationName()
    {
        var control = new PdfViewerControl();

        control.Focusable.Should().BeTrue();
        AutomationProperties.GetName(control).Should().Be("PDF viewer, no document loaded");
        AutomationProperties.GetHelpText(control).Should().Contain("Use Page Up and Page Down");
    }

    #endregion

    #region Document Loading Tests

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_CanLoadDocument()
    {
        // Arrange
        var pdfPath = TestPdfGenerator.CreateSimplePdf("PdfViewerControlTests_LoadDoc");
        var control = new PdfViewerControl();

        // Act
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(pdfPath);
            control.Document = doc;
        });

        // Give time for rendering
        await Task.Delay(500);

        // Assert
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.Document.Should().NotBeNull();
            control.Document!.PageCount.Should().BeGreaterThan(0);
        });

        // Cleanup
        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_LoadMultiPageDocument_ShowsPageCount()
    {
        // Arrange
        var pdfPath = TestPdfGenerator.CreateMultiPagePdf("PdfViewerControlTests_MultiPage", pageCount: 3);
        var control = new PdfViewerControl();

        // Act
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(pdfPath);
            control.Document = doc;
        });

        await Task.Delay(500);

        // Assert
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.Document.Should().NotBeNull();
            control.Document!.PageCount.Should().Be(3);
        });

        // Cleanup
        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_DocumentAutomationHelpText_IncludesPageTextPreview()
    {
        var pdfPath = TestPdfGenerator.CreateSimpleTextPdf("PdfViewerControlTests_AutomationPreview", "Accessible Preview Text");
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.Document = PdfCoreDocument.Open(pdfPath);
        });

        await Task.Delay(500);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AutomationProperties.GetName(control).Should().Be("PDF viewer, page 1 of 1");
            AutomationProperties.GetHelpText(control).Should().Contain("Accessible Preview Text");
        });

        control.Document?.Dispose();
    }

    #endregion

    #region Zoom Tests

    [FixedAvaloniaFact]
    public void PdfViewerControl_ZoomIn_IncreasesZoomLevel()
    {
        // Arrange
        var control = new PdfViewerControl();
        var initialZoom = control.ZoomLevel;

        // Act
        Dispatcher.UIThread.Invoke(() =>
        {
            control.ZoomLevel = initialZoom * 1.25; // Simulate zoom in
        });

        // Assert
        control.ZoomLevel.Should().BeGreaterThan(initialZoom);
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_ZoomOut_DecreasesZoomLevel()
    {
        // Arrange
        var control = new PdfViewerControl();
        control.ZoomLevel = 2.0;

        // Act
        Dispatcher.UIThread.Invoke(() =>
        {
            control.ZoomLevel = 2.0 / 1.25; // Simulate zoom out
        });

        // Assert
        control.ZoomLevel.Should().BeLessThan(2.0);
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_ZoomLevel_CanBeSetDirectly()
    {
        // Arrange
        var control = new PdfViewerControl();

        // Act
        Dispatcher.UIThread.Invoke(() =>
        {
            control.ZoomLevel = 1.5;
        });

        // Assert
        control.ZoomLevel.Should().Be(1.5);
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_KeyboardZoomShortcuts_UpdateZoom()
    {
        var control = new PdfViewerControl();

        RaiseViewerKey(control, Key.OemPlus, KeyModifiers.Control);

        control.ZoomLevel.Should().Be(1.25);
        AutomationProperties.GetItemStatus(control).Should().Contain("zoom 125");

        RaiseViewerKey(control, Key.D0, KeyModifiers.Control);

        control.ZoomLevel.Should().Be(1.0);
    }

    #endregion

    #region Page Navigation Tests

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_CurrentPage_CanBeChanged()
    {
        // Arrange
        var pdfPath = TestPdfGenerator.CreateMultiPagePdf("PdfViewerControlTests_Navigation", pageCount: 3);
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(pdfPath);
            control.Document = doc;
        });

        await Task.Delay(500);

        // Act
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.CurrentPage = 2;
        });

        // Assert
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.CurrentPage.Should().Be(2);
        });

        // Cleanup
        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_CanNavigateToLastPage()
    {
        // Arrange
        var pdfPath = TestPdfGenerator.CreateMultiPagePdf("PdfViewerControlTests_LastPage", pageCount: 5);
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(pdfPath);
            control.Document = doc;
        });

        await Task.Delay(500);

        // Act
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.CurrentPage = control.Document!.PageCount;
        });

        // Assert
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.CurrentPage.Should().Be(5);
        });

        // Cleanup
        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_KeyboardPageShortcuts_UpdateCurrentPage()
    {
        var pdfPath = TestPdfGenerator.CreateMultiPagePdf("PdfViewerControlTests_AccessibleKeyboard", pageCount: 3);
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.Document = PdfCoreDocument.Open(pdfPath);

            RaiseViewerKey(control, Key.PageDown);
            control.CurrentPage.Should().Be(2);

            RaiseViewerKey(control, Key.End);
            control.CurrentPage.Should().Be(3);

            RaiseViewerKey(control, Key.PageUp);
            control.CurrentPage.Should().Be(2);

            RaiseViewerKey(control, Key.Home);
            control.CurrentPage.Should().Be(1);
        });

        control.Document?.Dispose();
    }

    #endregion

    private static void RaiseViewerKey(PdfViewerControl control, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            Key = key,
            KeyModifiers = modifiers,
        };
        control.RaiseEvent(args);
        args.Handled.Should().BeTrue();
    }

    #region Interaction Mode Tests

    [FixedAvaloniaFact]
    public void PdfViewerControl_InteractionMode_CanBeChanged()
    {
        // Arrange
        var control = new PdfViewerControl();

        // Act
        Dispatcher.UIThread.Invoke(() =>
        {
            control.InteractionMode = InteractionMode.Redaction;
        });

        // Assert
        control.InteractionMode.Should().Be(InteractionMode.Redaction);
    }

    [FixedAvaloniaFact]
    public void PdfViewerControl_InteractionMode_CanBeSwitched()
    {
        // Arrange
        var control = new PdfViewerControl();
        control.InteractionMode = InteractionMode.Redaction;

        // Act
        Dispatcher.UIThread.Invoke(() =>
        {
            control.InteractionMode = InteractionMode.TextSelection;
        });

        // Assert
        control.InteractionMode.Should().Be(InteractionMode.TextSelection);
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_TypewriterOperations_ShowEditorForCurrentPage()
    {
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.CreateNew();
            doc.Pages.AddBlank(300, 400);
            doc.Pages.AddBlank(300, 400);
            control.Document = doc;
            control.CurrentPage = 1;
            control.InteractionMode = InteractionMode.Typewriter;
            control.TypewriterTextOperations = new[]
            {
                PdfTypewriterTextOperation.Create(
                    1,
                    new PdfRectangle(40, 250, 240, 290),
                    "Visible note"),
                PdfTypewriterTextOperation.Create(
                    2,
                    new PdfRectangle(40, 250, 240, 290),
                    "Other page note"),
            };
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var layer = control.FindControl<Canvas>("TypewriterLayer");

            layer.Should().NotBeNull();
            layer!.Children.Should().HaveCount(1);
        });

        control.Document?.Dispose();
    }

    #endregion

    #region Event Tests

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_PageChanged_FiresEvent()
    {
        // Arrange
        var pdfPath = TestPdfGenerator.CreateMultiPagePdf("PdfViewerControlTests_PageEvent", pageCount: 3);
        var control = new PdfViewerControl();
        int? observedPageNumber = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(pdfPath);
            control.Document = doc;
            // Subscribe inside the same UI-thread queue to guarantee the
            // handler is wired before any later property change can route
            // through it. We filter on PageNumber == 2 so the page-1 render
            // that the Document setter kicks off doesn't trip us.
            control.PageChanged += (s, e) =>
            {
                if (e.PageNumber == 2) observedPageNumber = e.PageNumber;
            };
        });

        // Act
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.CurrentPage = 2;
        });

        // Poll for the event with a long deadline. The PageChanged chain is:
        //   property hook → async void OnCurrentPageChanged
        //                 → await RenderCurrentPageAsync (Task.Run + dispatcher hop)
        //                 → PageChanged?.Invoke(...)
        // Under shared-dispatcher load (other AvaloniaFact tests sharing the
        // process) the threadpool render and the dispatcher round-trip can
        // run several seconds. Polling lets us yield back to the dispatcher
        // repeatedly so its work queue actually drains.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && observedPageNumber == null)
        {
            await Task.Delay(50);
        }

        // Assert
        observedPageNumber.Should().Be(2,
            "PageChanged should fire after CurrentPage advances; if this times out, " +
            "the OnCurrentPageChanged → RenderCurrentPageAsync → event chain stalled.");

        // Cleanup
        control.Document?.Dispose();
    }

    #endregion

    #region Annotation Overlay Tests

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_LoadDocumentWithAnnotations_AnnotationsLayerHasChildren()
    {
        // Build a minimal in-memory PDF with a single Highlight annotation.
        // NOT /Text (#1797) — see PdfViewerControl_TextAnnotation_ProducesNoOverlayRectangle
        // just below for why that subtype is deliberately excluded from this layer.
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Highlight /Rect [72 720 108 756] /Contents (test) >>");
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(new System.IO.MemoryStream(pdf), false);
            control.Document = doc;
        });

        // Poll until AnnotationsLayer acquires children (driven by document-load callback).
        Canvas? layer = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            bool hasChildren = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                layer = control.FindControl<Canvas>("AnnotationsLayer");
                hasChildren = layer?.Children.Count > 0;
            });
            if (hasChildren) break;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var annotLayer = control.FindControl<Canvas>("AnnotationsLayer");
            annotLayer.Should().NotBeNull("AnnotationsLayer canvas must exist");
            annotLayer!.Children.Count.Should().BeGreaterThan(0,
                "one annotation should produce at least one rectangle in the overlay");
            annotLayer.Children.OfType<Rectangle>().Should().NotBeEmpty(
                "annotations are rendered as Rectangle controls");
        });

        control.Document?.Dispose();
    }

    /// <summary>
    /// #1797: a /Text (sticky note) annotation already gets a complete,
    /// fully-styled card from SkiaRenderer.RenderStickyNoteDefault, baked
    /// into the page raster this overlay sits ON TOP OF. An additional
    /// translucent rect here duplicated it — filling the note's card-sized
    /// /Rect with a semi-transparent tint over the SAME card SkiaRenderer
    /// already drew solid ("messed up text display"). Every other subtype
    /// still gets one (see the test above and below).
    /// </summary>
    [FixedAvaloniaFact]
    public async Task PdfViewerControl_TextAnnotation_ProducesNoOverlayRectangle()
    {
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Text /Rect [72 720 108 756] /Contents (test) >>");
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(new System.IO.MemoryStream(pdf), false);
            control.Document = doc;
        });

        // Give the (deliberately absent) overlay every chance to appear.
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var annotLayer = control.FindControl<Canvas>("AnnotationsLayer");
            annotLayer.Should().NotBeNull("AnnotationsLayer canvas must exist");
            annotLayer!.Children.Should().BeEmpty(
                "a /Text annotation's card is already fully rendered by SkiaRenderer; " +
                "this overlay must not also draw a translucent duplicate over it");
        });

        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_SetAnnotationsDirectly_LayerMatchesCount()
    {
        // Set the Annotations property with 2 known annotations and verify
        // that the layer renders exactly 2 rectangles.
        // Highlight + Square (not /Text — #1797's overlay exemption, covered
        // by its own dedicated test above), so "N annotations -> N rects"
        // stays an exact count.
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Highlight /Rect [10 10 200 30] >>" +
            "<< /Type /Annot /Subtype /Square    /Rect [50 50 100 80] >>");
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(new System.IO.MemoryStream(pdf), false);
            control.Document = doc;
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ready = control.FindControl<Canvas>("AnnotationsLayer")?.Children.Count == 2;
            });
            if (ready) break;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var rects = control.FindControl<Canvas>("AnnotationsLayer")!
                               .Children.OfType<Rectangle>().ToList();
            rects.Should().HaveCount(2,
                "two inline annotations should produce exactly two overlay rectangles");
        });

        control.Document?.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_AnnotationsCleared_WhenDocumentSetToNull()
    {
        // NOT /Text (#1797) — that subtype no longer produces an overlay
        // rectangle at all, which would make "wait for it to appear" below
        // wait out its own timeout for nothing.
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Highlight /Rect [0 0 100 20] >>");
        var control = new PdfViewerControl();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = PdfCoreDocument.Open(new System.IO.MemoryStream(pdf), false);
            control.Document = doc;
        });

        // Wait for annotation to appear.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            bool has = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
                has = control.FindControl<Canvas>("AnnotationsLayer")?.Children.Count > 0);
            if (has) break;
        }

        // Now clear the document.
        PdfCoreDocument? old = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            old = control.Document;
            control.Document = null;
        });
        old?.Dispose();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var layer = control.FindControl<Canvas>("AnnotationsLayer");
            layer?.Children.Count.Should().Be(0,
                "annotations must be cleared when the document is removed");
        });
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>Build a minimal single-page PDF with one or more inline annotation dicts in /Annots.</summary>
    private static byte[] MakePdfWithAnnotation(string annotsDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long o1, o2, o3, o4;

        o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{annotsDef}] >>");
        sb.AppendLine("endobj");

        o4 = sb.Length;
        sb.AppendLine("4 0 obj << /Length 0 >> stream\nendstream\nendobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref\n0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine("trailer\n<< /Size 5 /Root 1 0 R >>");
        sb.AppendLine($"startxref\n{xrefPos}\n%%%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    #endregion

    #region Continuous View Mode (#371)

    [FixedAvaloniaFact]
    public void ViewMode_DefaultsToSinglePage()
    {
        new PdfViewerControl().ViewMode.Should().Be(PdfViewMode.SinglePage);
    }

    [FixedAvaloniaFact]
    public void ViewMode_TogglesScrollViewerVisibility()
    {
        var control = new PdfViewerControl();
        var single = control.FindControl<ScrollViewer>("PdfScrollViewer")!;
        var continuous = control.FindControl<ScrollViewer>("ContinuousScrollViewer")!;

        single.IsVisible.Should().BeTrue("single-page is the default view");
        continuous.IsVisible.Should().BeFalse();

        control.ViewMode = PdfViewMode.Continuous;

        single.IsVisible.Should().BeFalse();
        continuous.IsVisible.Should().BeTrue();
    }

    /// <summary>
    /// #1473: in continuous view, opening a document, bumping RenderVersion and
    /// toggling annotations must not render the hidden single-page Image. The
    /// single page still renders the moment it becomes visible. Each continuous
    /// path that drops the single-page cache also disposes the bitmap the hidden
    /// Image last showed, so the Image must let go of it: switching back to
    /// single-page must lay out and render frames without an
    /// ObjectDisposedException.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ContinuousView_DoesNotRenderHiddenSinglePage_AndRendersItWhenShown()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise_hidden_single_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);

        var viewer = new PdfViewerControl { ViewMode = PdfViewMode.Continuous };
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        var dispatcherErrors = new System.Collections.Generic.List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        window.Show();
        var image = viewer.FindControl<Image>("PdfImage")!;
        try
        {
            viewer.Document = PdfCoreDocument.Open(bytes);
            var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);

            SinglePageRenderAttempts(viewer).Should().Be(0,
                "opening a document in continuous view must not render the hidden single page");
            image.Source.Should().BeNull();

            // Shown: the single page renders.
            viewer.ViewMode = PdfViewMode.SinglePage;
            await PumpUntilAsync(window, () => image.Source != null);
            RenderFrames(window);
            long attemptsAfterShow = SinglePageRenderAttempts(viewer);
            attemptsAfterShow.Should().BeGreaterThan(0, "switching to single-page renders the current page");

            // Hidden again: a content rewrite and an annotation toggle drop the
            // single-page cache without rendering the hidden page.
            viewer.ViewMode = PdfViewMode.Continuous;
            viewer.RenderVersion++;
            RenderFrames(window);
            image.Source.Should().BeNull("RenderVersion disposed the bitmap the hidden Image showed");
            viewer.ShowAnnotations = !viewer.ShowAnnotations;
            RenderFrames(window);
            image.Source.Should().BeNull();

            // A second document opened in continuous view: still no hidden render.
            var previous = viewer.Document;
            viewer.Document = PdfCoreDocument.Open(bytes);
            previous?.Dispose();
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            SinglePageRenderAttempts(viewer).Should().Be(attemptsAfterShow,
                "no continuous-view path may render the hidden single page");

            // Shown again: renders, and the Image never measures a disposed bitmap.
            viewer.ViewMode = PdfViewMode.SinglePage;
            RenderFrames(window);
            await PumpUntilAsync(window, () => image.Source != null);
            RenderFrames(window);
            ((global::Avalonia.Media.Imaging.Bitmap)image.Source!).PixelSize.Width.Should().BeGreaterThan(0);
            dispatcherErrors.Should().BeEmpty();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }

        static long SinglePageRenderAttempts(PdfViewerControl v)
        {
            var d = v.GetRenderDiagnostics();
            return d.SinglePageHits + d.SinglePageMisses;
        }
    }

    /// <summary>
    /// #1473 follow-up: a continuous-view document change resets the single-page
    /// logical DPI (ClearDisplay), and the ZoomHost scale (zoom × 96 / logical DPI)
    /// must follow. RenderCurrentPageAsync refreshes the scale only when a page's
    /// logical DPI differs from the field, so a reset field with a stale scale
    /// showed the next ordinary page at a huge page's clamped scale.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ContinuousDocumentChange_AfterAClampedPage_KeepsTheSinglePageDisplayScale()
    {
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        var image = viewer.FindControl<Image>("PdfImage")!;
        var zoomHost = viewer.FindControl<LayoutTransformControl>("ZoomHost")!;
        try
        {
            // A 7200 x 7200 pt page exceeds the single-page pixel budget at 120 DPI,
            // so its logical DPI clamps (to 81). Opening it sets that DPI and scale
            // synchronously; the switch and the second document in the same turn
            // cancel its render.
            viewer.Document = PdfCoreDocument.Open(SquarePagePdf(7200));
            var clampedScale = ((global::Avalonia.Media.ScaleTransform)zoomHost.LayoutTransform!).ScaleX;
            clampedScale.Should().BeGreaterThan(viewer.ZoomLevel * 96.0 / 120.0 + 0.1,
                "fixture: the huge page must clamp its logical DPI below 120");

            viewer.ViewMode = PdfViewMode.Continuous;
            var huge = viewer.Document;
            viewer.Document = PdfCoreDocument.Open(TestPdfGenerator.CreateSimplePdf("ordinary page"));
            huge?.Dispose();

            viewer.ViewMode = PdfViewMode.SinglePage;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (image.Source == null && DateTime.UtcNow < deadline)
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            image.Source.Should().NotBeNull("switching to single-page renders the ordinary page");

            ((global::Avalonia.Media.ScaleTransform)zoomHost.LayoutTransform!).ScaleX
                .Should().BeApproximately(viewer.ZoomLevel * 96.0 / 120.0, 1e-9,
                    "an ordinary page renders at the 120 logical DPI, so it must display at zoom x 96/120");
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }

        static byte[] SquarePagePdf(int sizePt)
        {
            var sb = new StringBuilder();
            sb.Append("%PDF-1.7\n");
            var offsets = new int[4];
            offsets[1] = sb.Length;
            sb.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
            offsets[2] = sb.Length;
            sb.Append("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
            offsets[3] = sb.Length;
            sb.Append($"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {sizePt} {sizePt}] >> endobj\n");
            int xref = sb.Length;
            sb.Append("xref\n0 4\n0000000000 65535 f \n");
            for (int i = 1; i <= 3; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
            sb.Append($"trailer << /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }

    private static void RenderFrames(Window w)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    private static async Task PumpUntilAsync(Window w, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not reached within 30s");
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    [FixedAvaloniaFact]
    public void EnteringEditingMode_InContinuous_AutoSwitchesToSinglePage()
    {
        // Text selection is deliberately excluded (#815): it is a read affordance
        // that now works in the continuous view, so it must NOT force single-page.
        foreach (var editing in new[] { InteractionMode.Redaction, InteractionMode.FormAuthoring, InteractionMode.Typewriter })
        {
            var control = new PdfViewerControl { ViewMode = PdfViewMode.Continuous };
            control.ViewMode.Should().Be(PdfViewMode.Continuous);

            control.InteractionMode = editing;

            control.ViewMode.Should().Be(PdfViewMode.SinglePage,
                $"{editing} is an editing interaction and must force single-page");
        }
    }

    [FixedAvaloniaFact]
    public void EnteringTextSelection_InContinuous_StaysInContinuous()
    {
        // #815: selecting text is a reading-view affordance, not an edit. Entering
        // it must keep the continuous reading view rather than snapping to
        // single-page (which is what made it undiscoverable).
        var control = new PdfViewerControl { ViewMode = PdfViewMode.Continuous };

        control.InteractionMode = InteractionMode.TextSelection;

        control.ViewMode.Should().Be(PdfViewMode.Continuous,
            "text selection works in the continuous view and must not force single-page");
    }

    [FixedAvaloniaFact]
    public void ContinuousMode_BuildsOneSlotPerPage()
    {
        // Open from bytes and delete the temp file up front: opening by
        // path keeps a live file stream, and deleting a still-open file
        // throws IOException on Windows (fine on Unix — which is why this
        // only surfaced when #647's Windows CI leg first got this far).
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise_cont_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);

        var control = new PdfViewerControl { Document = PdfCoreDocument.Open(bytes) };
        control.ViewMode = PdfViewMode.Continuous;

        var items = control.FindControl<ItemsControl>("ContinuousItems")!;
        items.ItemsSource.Should().NotBeNull();
        items.ItemsSource!.Cast<PdfPageSlot>().Select(s => s.PageNumber)
            .Should().Equal(1, 2, 3);
    }

    [FixedAvaloniaFact]
    public void ContinuousSlot_DisplayWidthScalesWithZoom()
    {
        var control = new PdfViewerControl { Document = PdfCoreDocument.Open(TestPdfGenerator.CreateSimplePdf("zoom")) };
        control.ViewMode = PdfViewMode.Continuous;

        var slot = control.FindControl<ItemsControl>("ContinuousItems")!
            .ItemsSource!.Cast<PdfPageSlot>().First();
        var widthAt1x = slot.DisplayWidth;
        widthAt1x.Should().BeGreaterThan(0);

        control.ZoomLevel = 2.0;

        slot.DisplayWidth.Should().BeApproximately(widthAt1x * 2.0, 0.5,
            "page slots resize with zoom so the reading view scales");
    }

    [FixedAvaloniaFact]
    public void ContinuousMode_CachesSlotTopOffsetsForScrollLookup()
    {
        // Bytes-based open + upfront delete: see ContinuousMode_BuildsOneSlotPerPage.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise_cont_offsets_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var bytes = System.IO.File.ReadAllBytes(path);
        System.IO.File.Delete(path);

        var control = new PdfViewerControl { Document = PdfCoreDocument.Open(bytes) };
        control.ViewMode = PdfViewMode.Continuous;

        var slots = control.FindControl<ItemsControl>("ContinuousItems")!
            .ItemsSource!.Cast<PdfPageSlot>().ToArray();

        slots[0].TopDip.Should().Be(0);
        slots[1].TopDip.Should().BeApproximately(
            slots[0].DisplayHeight + 12.0,
            0.01,
            "slot offsets should be precomputed once instead of summed on every scroll event");
        PdfViewerControl.FindTopVisibleContinuousPage(slots, slots[1].TopDip + 1)
            .Should().Be(2);

        // #1650: the page a "current page" command acts on is the page with the
        // MOST VISIBLE AREA, which is a different question. Scroll so that only
        // the last 2 dip of page 1 are on screen and the rest of the viewport
        // is page 2: top-visible says 1, most-visible says 2, and the commands
        // must follow most-visible. Before this, Remove Current Page there
        // removed page 1.
        var sliver = slots[0].TopDip + slots[0].DisplayHeight - 2;
        var viewport = slots[1].DisplayHeight;

        PdfViewerControl.FindTopVisibleContinuousPage(slots, sliver)
            .Should().Be(1, "page 1 still owns the top edge of the viewport");
        PdfViewerControl.FindMostVisibleContinuousPage(slots, sliver, viewport)
            .Should().Be(2, "page 2 fills the viewport; page 1 has 2 dip of it");

        // At the top of the document, both agree.
        PdfViewerControl.FindMostVisibleContinuousPage(slots, 1, viewport)
            .Should().Be(1);

        // No viewport yet (a measure pass before the scroll viewer is sized):
        // fall back rather than answer page 1 regardless of the offset.
        PdfViewerControl.FindMostVisibleContinuousPage(slots, slots[1].TopDip + 1, 0)
            .Should().Be(2);
    }

    [FixedAvaloniaFact]
    public void ContinuousGrid_AlignsToQuantumAndExpandsBeyondViewportForScrollCoalescing()
    {
        // #848: the single sliding band was replaced by a grid of quantum-aligned
        // cells. Overscan + quantum alignment still hold, now per cell: nearby
        // scroll offsets require the same (Col, Row) cells → same cache keys.
        var slot = new PdfPageSlot(pageNumber: 1, widthPt: 612, heightPt: 792, zoom: 2.0);

        var cells = PdfViewerControl.RequiredTileCells(
            slot.DisplayWidth, slot.DisplayHeight, pageTopDip: 0,
            viewportOffset: new Vector(0, 600), viewport: new Size(900, 500),
            PdfViewerControl.ContinuousTileQuantumDip, PdfViewerControl.ContinuousTileOverscanDip);

        cells.Should().NotBeEmpty();

        foreach (var c in cells)
        {
            (c.XDip % PdfViewerControl.ContinuousTileQuantumDip).Should().Be(0,
                "cell starts are on the quantum lattice so nearby scroll offsets reuse the same cache key");
            (c.YDip % PdfViewerControl.ContinuousTileQuantumDip).Should().Be(0);
        }

        double coverTop = cells.Min(c => c.YDip);
        double coverBottom = cells.Max(c => c.YDip + c.HeightDip);
        coverTop.Should().BeLessThanOrEqualTo(600,
            "the grid should extend before the visible viewport to absorb small scroll deltas");
        coverBottom.Should().BeGreaterThanOrEqualTo(1_100,
            "the grid should cover the visible viewport plus overscan");
    }

    [Fact]
    public void ContinuousMosaic_TilesCellsEdgeToEdge_NoGapNoOverlap()
    {
        // #848 seam fix: cells are composited into ONE bitmap by laying them out
        // edge-to-edge at cumulative integer pixel offsets. This pins that the
        // mosaic tiles the buffer exactly — every cell abuts its neighbours with no
        // gap (which would be a seam) and no overlap, and the total equals the sum
        // of column widths / row heights. A 2x3 grid with uneven (edge) cell sizes.
        var cells = new[]
        {
            (Col: 0, Row: 0, PxW: 640, PxH: 640),
            (Col: 1, Row: 0, PxW: 640, PxH: 640),
            (Col: 2, Row: 0, PxW: 385, PxH: 640),  // right edge column: narrower
            (Col: 0, Row: 1, PxW: 640, PxH: 206),  // bottom edge row: shorter
            (Col: 1, Row: 1, PxW: 640, PxH: 206),
            (Col: 2, Row: 1, PxW: 385, PxH: 206),
        };

        var (totalW, totalH, offsets) = PdfViewerControl.ComputeMosaic(cells);

        totalW.Should().Be(640 + 640 + 385, "columns tile the full width with no gap/overlap");
        totalH.Should().Be(640 + 206, "rows tile the full height with no gap/overlap");

        // Every cell's rect stays inside the buffer and abuts its right/bottom
        // neighbour exactly (this cell's right edge == next column's left edge).
        var byCell = cells.ToDictionary(c => (c.Col, c.Row));
        foreach (var c in cells)
        {
            var (x, y) = offsets[(c.Col, c.Row)];
            (x + c.PxW).Should().BeLessThanOrEqualTo(totalW);
            (y + c.PxH).Should().BeLessThanOrEqualTo(totalH);

            if (byCell.TryGetValue((c.Col + 1, c.Row), out var right))
                offsets[(right.Col, right.Row)].Item1.Should().Be(x + c.PxW,
                    "the right neighbour starts exactly where this cell ends — no seam, no overlap");
            if (byCell.TryGetValue((c.Col, c.Row + 1), out var below))
                offsets[(below.Col, below.Row)].Item2.Should().Be(y + c.PxH,
                    "the cell below starts exactly where this cell ends — no seam, no overlap");
        }
    }

    #endregion
}
