using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// A gallery of every annotation excise can author, each drawn in context on a real sentence,
/// opened in the real window, with what the viewer actually painted written out as PNGs.
/// It is a look-at-it harness first (set EXCISE_GALLERY_DIR to keep the PDF and the PNGs),
/// and a guard second: every annotation type must still be authored and must still appear.
/// </summary>
[Collection("AvaloniaTests")]
public class AnnotationGalleryTests
{
    private const double PageHeight = 792;
    private const double FontSize = 14;
    private const string Sentence = "The quick brown fox jumps over the lazy dog near the river bank.";

    private static double Width(string text) => PdfGraphics.MeasureString(text, PdfFont.Helvetica(FontSize)).Width;

    /// <summary>The PDF rectangle covering <paramref name="words"/> inside <see cref="Sentence"/> on a baseline.</summary>
    private static PdfRectangle Span(string words, double baselineY)
    {
        var start = Sentence.IndexOf(words, StringComparison.Ordinal);
        var x0 = 72 + Width(Sentence[..start]);
        return new PdfRectangle(x0, baselineY - 4, x0 + Width(words), baselineY + FontSize - 2);
    }

    private static void Label(PdfPage page, string text, double baselineY)
    {
        using var g = page.GetGraphics();
        g.DrawString(text, PdfFont.HelveticaBold(9), PdfBrush.Black, 72, baselineY + 22);
        g.DrawString(Sentence, PdfFont.Helvetica(FontSize), PdfBrush.Black, 72, baselineY);
        g.Flush();
    }

    /// <summary>Build the gallery: markup on page 1, shapes/notes/stamps on page 2. Returns the annotation kinds authored.</summary>
    internal static List<string> Build(string path)
    {
        var kinds = new List<string>();
        using var doc = PdfDocument.CreateNew();
        var p1 = doc.Pages.AddBlank();
        var p2 = doc.Pages.AddBlank();

        double Row(int i) => PageHeight - 110 - i * 90;

        Label(p1, "Highlight", Row(0));
        doc.AddHighlightAnnotation(1, Span("quick brown fox", Row(0)), "highlighted", "gallery");
        kinds.Add("Highlight");

        Label(p1, "Underline", Row(1));
        doc.AddUnderlineAnnotation(1, Span("lazy dog", Row(1)), "underlined", "gallery", 0, 0.4, 1);
        kinds.Add("Underline");

        Label(p1, "StrikeOut", Row(2));
        doc.AddStrikeOutAnnotation(1, Span("jumps over", Row(2)), "struck out", "gallery");
        kinds.Add("StrikeOut");

        Label(p1, "Squiggly", Row(3));
        doc.AddSquigglyAnnotation(1, Span("river bank", Row(3)), "squiggly", "gallery", 1, 0.5, 0);
        kinds.Add("Squiggly");

        Label(p1, "Sticky note (Text + linked Popup)", Row(4));
        doc.AddTextAnnotation(1, new PdfRectangle(72 + Width(Sentence) + 12, Row(4) - 2, 72 + Width(Sentence) + 32, Row(4) + 16),
            "A sticky note with its popup", "gallery", open: true, withPopup: true);
        kinds.Add("Text");

        Label(p1, "Free text", Row(5));
        doc.AddFreeTextAnnotation(1, new PdfRectangle(72, Row(5) - 46, 300, Row(5) - 8), "Free text box in context", "gallery",
            fontSize: 12, borderWidth: 1, backgroundRed: 1, backgroundGreen: 1, backgroundBlue: 0.8);
        kinds.Add("FreeText");

        Label(p2, "Square", Row(0));
        doc.AddSquareAnnotation(2, Span("brown", Row(0)), "square", "gallery", 1, 0, 0, 1.5);
        kinds.Add("Square");

        Label(p2, "Circle", Row(1));
        doc.AddCircleAnnotation(2, Span("fox", Row(1)), "circle", "gallery", 0, 0.6, 0, 1.5);
        kinds.Add("Circle");

        Label(p2, "Line", Row(2));
        doc.AddLineAnnotation(2, 72, Row(2) - 12, 320, Row(2) - 12, "line", "gallery", 0, 0, 1, 1.5);
        kinds.Add("Line");

        Label(p2, "Arrow", Row(3));
        doc.AddArrowAnnotation(2, 72, Row(3) - 14, 320, Row(3) - 14, "arrow", "gallery", 1, 0, 0, 1.5);
        kinds.Add("Arrow");

        Label(p2, "Polygon / PolyLine", Row(4));
        doc.AddPolygonAnnotation(2, new[] { (72.0, Row(4) - 40), (132.0, Row(4) - 40), (102.0, Row(4) - 8) }, "polygon", "gallery", 0.5, 0, 0.5, 1.5);
        doc.AddPolyLineAnnotation(2, new[] { (180.0, Row(4) - 40), (215.0, Row(4) - 8), (250.0, Row(4) - 40), (285.0, Row(4) - 8) }, "polyline", "gallery", 0, 0.5, 0.5, 1.5);
        kinds.Add("Polygon"); kinds.Add("PolyLine");

        Label(p2, "Ink (freehand)", Row(5));
        doc.AddInkAnnotation(2, new IReadOnlyList<(double X, double Y)>[]
        {
            new (double X, double Y)[] { (72, Row(5) - 30), (90, Row(5) - 10), (110, Row(5) - 40), (130, Row(5) - 12), (150, Row(5) - 36) },
        }, "ink", "gallery", 0.9, 0.2, 0.2, 2);
        kinds.Add("Ink");

        Label(p2, "Stamp", Row(6));
        doc.AddStampAnnotation(2, new PdfRectangle(72, Row(6) - 60, 232, Row(6) - 8), "Approved", "stamp", "gallery");
        kinds.Add("Stamp");

        doc.Save(path);
        return kinds;
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task EveryAnnotationType_IsAuthoredAndDrawnByTheViewer()
    {
        var outDir = Environment.GetEnvironmentVariable("EXCISE_GALLERY_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"excise-gallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var pdf = Path.Combine(outDir, "annotation-gallery.pdf");
        var kinds = Build(pdf);

        using (var reopened = PdfDocument.Open(pdf))
        {
            var count = Enumerable.Range(1, reopened.PageCount).Sum(p => reopened.GetPage(p).GetAnnotations().Count);
            count.Should().BeGreaterThanOrEqualTo(kinds.Count,
                "every authored annotation type must survive a save and reload");
        }

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 900, Height = 1000 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(pdf);
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

        foreach (var page in new[] { 1, 2 })
        {
            vm.CurrentPageIndex = page - 1;
            await Task.Delay(1500);
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            frame.Should().NotBeNull("the headless compositor must produce a frame");
            using var png = File.Create(Path.Combine(outDir, $"annotation-gallery-page{page}.png"));
            frame!.Save(png, PngBitmapEncoderOptions.Default);
        }

        File.Exists(Path.Combine(outDir, "annotation-gallery-page1.png")).Should().BeTrue();
        Console.Error.WriteLine($"GALLERY {outDir}");
    }
}
