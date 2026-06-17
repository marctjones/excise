using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using Pdfe.Core.Editing;
using PdfEditor.Tests.Utilities;
using Xunit;
using PdfCoreDocument = Pdfe.Core.Document.PdfDocument;

namespace PdfEditor.Tests.Controls;

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
        // Build a minimal in-memory PDF with a single Text annotation.
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Text /Rect [72 720 108 756] /Contents (test) >>");
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

    [FixedAvaloniaFact]
    public async Task PdfViewerControl_SetAnnotationsDirectly_LayerMatchesCount()
    {
        // Set the Annotations property with 2 known annotations and verify
        // that the layer renders exactly 2 rectangles.
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Highlight /Rect [10 10 200 30] >>" +
            "<< /Type /Annot /Subtype /Text    /Rect [50 50 100 80] >>");
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
        var pdf = MakePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Text /Rect [0 0 100 20] >>");
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

    [FixedAvaloniaFact]
    public void EnteringEditingMode_InContinuous_AutoSwitchesToSinglePage()
    {
        foreach (var editing in new[] { InteractionMode.Redaction, InteractionMode.TextSelection, InteractionMode.FormAuthoring })
        {
            var control = new PdfViewerControl { ViewMode = PdfViewMode.Continuous };
            control.ViewMode.Should().Be(PdfViewMode.Continuous);

            control.InteractionMode = editing;

            control.ViewMode.Should().Be(PdfViewMode.SinglePage,
                $"{editing} is an editing interaction and must force single-page");
        }
    }

    [FixedAvaloniaFact]
    public void ContinuousMode_BuildsOneSlotPerPage()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfe_cont_{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        try
        {
            var control = new PdfViewerControl { Document = PdfCoreDocument.Open(path) };
            control.ViewMode = PdfViewMode.Continuous;

            var items = control.FindControl<ItemsControl>("ContinuousItems")!;
            items.ItemsSource.Should().NotBeNull();
            items.ItemsSource!.Cast<PdfPageSlot>().Select(s => s.PageNumber)
                .Should().Equal(1, 2, 3);
        }
        finally { System.IO.File.Delete(path); }
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

    #endregion
}
