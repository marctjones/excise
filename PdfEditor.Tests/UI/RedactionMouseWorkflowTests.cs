using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

[Collection("AvaloniaTests")]
public class RedactionMouseWorkflowTests
{
    private readonly string _tempDir;

    public RedactionMouseWorkflowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pdfe-redaction-mouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task MouseDragRedaction_CorpusScenarios_RedactExpectedContentAndSave()
    {
        foreach (var scenario in RedactionScenarios())
        {
            var sourcePdf = Path.Combine(_tempDir, $"{scenario.Name}-source.pdf");
            var outputPdf = Path.Combine(_tempDir, $"{scenario.Name}-output.pdf");
            scenario.CreatePdf(sourcePdf);

            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 2000, Height = 1600 };
            window.Show();

            await vm.LoadDocumentAsync(sourcePdf);
            await WaitForIdleLayout(window);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            viewer.Should().NotBeNull($"{scenario.Name}: viewer must be available");

            vm.ZoomActualSizeCommand.Execute().Subscribe();
            vm.ZoomInCommand.Execute().Subscribe();
            vm.IsRedactionMode = true;
            await WaitForIdleLayout(window);

            var page = vm.PdfCoreDocument!.GetPage(1);
            var overlay = FindNamedDescendant<Canvas>(viewer!, "OverlayCanvas")!;
            var (start, end) = ToWindowDragPoints(scenario.DragArea, page, overlay, window);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                window.MouseDown(start, MouseButton.Left));
            await Task.Delay(50);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                window.MouseMove(end));
            await Task.Delay(50);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                window.MouseUp(end, MouseButton.Left));
            await WaitForIdleLayout(window);

            vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle(
                $"{scenario.Name}: one mouse drag should create one pending redaction");
            var pending = vm.RedactionWorkflow.PendingRedactions.Single();
            pending.PageArea.Space.Should().Be(PdfCoordinateSpace.ViewerDips,
                $"{scenario.Name}: mouse-drawn redactions must carry their viewer coordinate space");
            pending.RenderDpi.Should().Be(120,
                $"{scenario.Name}: the viewer renders single-page editing overlays at 120 DPI");

            if (!string.IsNullOrEmpty(scenario.ExpectedPreviewContains))
            {
                pending.PreviewText.Should().Contain(scenario.ExpectedPreviewContains,
                    $"{scenario.Name}: pending-redaction preview must describe extractable text under the user's mouse drag");
            }

            foreach (var term in scenario.PreviewMustNotContain)
            {
                pending.PreviewText.Should().NotContain(term,
                    $"{scenario.Name}: preview must not include content outside the selected mouse area");
            }

            await vm.ApplyRedactionsCommand();
            await vm.SaveDocumentCommand(outputPdf);

            File.Exists(outputPdf).Should().BeTrue(
                $"{scenario.Name}: saving after a mouse redaction must write an output PDF");
            var savedBytes = File.ReadAllBytes(outputPdf);
            using var saved = PdfDocument.Open(savedBytes);
            var savedText = string.Concat(saved.GetPage(1).Letters.Select(l => l.Value));

            foreach (var removed in scenario.TextMustBeRemoved)
            {
                savedText.Should().NotContain(removed,
                    $"{scenario.Name}: selected visible content must be removed from the saved PDF structure");
            }

            foreach (var kept in scenario.TextMustRemain)
            {
                savedText.Should().Contain(kept,
                    $"{scenario.Name}: content outside the selected mouse-redaction area must remain extractable");
            }

            var savedLatin1 = Encoding.Latin1.GetString(savedBytes);
            foreach (var removedBytes in scenario.SavedBytesMustNotContain)
            {
                savedLatin1.Should().NotContain(removedBytes,
                    $"{scenario.Name}: redacted literal bytes must not leak in the saved PDF");
            }

            var content = saved.GetPage(1).GetContentStream().Operators;
            content.Any(op => string.Equals(op.Name, "re", StringComparison.Ordinal))
                .Should().BeTrue($"{scenario.Name}: redaction must add a visual black rectangle as confirmation");
            content.Any(op => string.Equals(op.Name, "f", StringComparison.Ordinal))
                .Should().BeTrue($"{scenario.Name}: redaction must fill the visual black rectangle");

            scenario.ExtraAssertions?.Invoke(saved);
            window.Close();
        }
    }

    private static IEnumerable<RedactionScenario> RedactionScenarios()
    {
        yield return new RedactionScenario(
            Name: "simple-text",
            CreatePdf: CreateSeparatedTextPdf,
            DragArea: new PdfRectangle(90, 375, 280, 430),
            ExpectedPreviewContains: "TARGETSECRET",
            PreviewMustNotContain: ["TOPKEEP", "BOTTOMKEEP"],
            TextMustBeRemoved: ["TARGETSECRET"],
            TextMustRemain: ["TOPKEEP", "BOTTOMKEEP"]);

        yield return new RedactionScenario(
            Name: "text-with-nearby-vector-graphics",
            CreatePdf: CreateTextWithNearbyVectorPdf,
            DragArea: new PdfRectangle(90, 375, 305, 430),
            ExpectedPreviewContains: "VECTORSECRET",
            PreviewMustNotContain: ["VECTORKEEP"],
            TextMustBeRemoved: ["VECTORSECRET"],
            TextMustRemain: ["VECTORKEEP"],
            ExtraAssertions: saved =>
            {
                saved.GetPage(1).GetContentStream().Operators.Count(op => op.Name == "re")
                    .Should().BeGreaterThanOrEqualTo(2,
                        "the original vector rectangle and the redaction marker should both remain as rectangles");
            });

        yield return new RedactionScenario(
            Name: "form-xobject-text",
            CreatePdf: CreateFormXObjectPdf,
            DragArea: new PdfRectangle(90, 690, 280, 725),
            PreviewMustNotContain: ["FORMKEEP", "FORMOUTSIDE"],
            TextMustBeRemoved: ["FORMSECRET"],
            TextMustRemain: ["FORMKEEP", "FORMOUTSIDE"],
            SavedBytesMustNotContain: ["FORMSECRET"]);

        yield return new RedactionScenario(
            Name: "image-xobject",
            CreatePdf: CreateImageXObjectPdf,
            DragArea: new PdfRectangle(90, 590, 210, 710),
            PreviewMustNotContain: ["IMAGEKEEP"],
            TextMustRemain: ["IMAGEKEEP"],
            ExtraAssertions: saved =>
            {
                saved.GetPage(1).GetContentStream().Operators.Should().NotContain(op => op.Name == "Do",
                    "the selected image XObject invocation should be removed, not merely covered");
            });
    }

    private static void CreateSeparatedTextPdf(string path)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        var font = PdfFont.Helvetica(18);
        graphics.DrawString("TOPKEEP", font, PdfBrush.Black, 100, 700);
        graphics.DrawString("TARGETSECRET", font, PdfBrush.Black, 100, 400);
        graphics.DrawString("BOTTOMKEEP", font, PdfBrush.Black, 100, 160);
        graphics.Flush();
        doc.Save(path);
    }

    private static void CreateTextWithNearbyVectorPdf(string path)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        var font = PdfFont.Helvetica(18);
        graphics.DrawString("VECTORKEEP", font, PdfBrush.Black, 100, 700);
        graphics.DrawString("VECTORSECRET", font, PdfBrush.Black, 100, 400);
        graphics.DrawRectangle(350, 580, 120, 80, PdfBrush.Red, PdfPen.Black);
        graphics.DrawLine(350, 540, 500, 500, PdfPen.Black);
        graphics.Flush();
        doc.Save(path);
    }

    private static void CreateFormXObjectPdf(string path)
    {
        File.WriteAllBytes(path, BuildPdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>"),
            PdfStream("", "BT /F1 12 Tf 100 500 Td (FORMKEEP) Tj ET q /Fm0 Do Q"),
            Obj(HelveticaFont),
            PdfStream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                      "/Resources << /Font << /F1 5 0 R >> >>",
                      "BT /F1 12 Tf 100 700 Td (FORMSECRET) Tj ET " +
                      "BT /F1 12 Tf 100 600 Td (FORMOUTSIDE) Tj ET")));
    }

    private static void CreateImageXObjectPdf(string path)
    {
        File.WriteAllBytes(path, BuildPdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>"),
            PdfStream("", "q 100 0 0 100 100 600 cm /Im0 Do Q " +
                          "BT /F1 12 Tf 100 400 Td (IMAGEKEEP) Tj ET"),
            Obj(HelveticaFont),
            PdfStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                      "/ColorSpace /DeviceGray /BitsPerComponent 8",
                      new byte[] { 0x00, 0x80, 0x80, 0xFF })));
    }

    private static (Point Start, Point End) ToWindowDragPoints(
        PdfRectangle contentRect,
        PdfPage page,
        Canvas overlay,
        Window window)
    {
        var viewerRect = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(page.PageNumber, contentRect),
            120);
        var startDip = new Point(viewerRect.X, viewerRect.Y);
        var endDip = new Point(viewerRect.Right, viewerRect.Y2);

        var start = overlay.TranslatePoint(startDip, window) ?? default;
        var end = overlay.TranslatePoint(endDip, window) ?? default;
        return (start, end);
    }

    private static async Task WaitForIdleLayout(Window window)
    {
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100);
            window.UpdateLayout();
        }
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
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

    private sealed record RedactionScenario(
        string Name,
        Action<string> CreatePdf,
        PdfRectangle DragArea,
        string? ExpectedPreviewContains = null,
        string[]? PreviewMustNotContain = null,
        string[]? TextMustBeRemoved = null,
        string[]? TextMustRemain = null,
        string[]? SavedBytesMustNotContain = null,
        Action<PdfDocument>? ExtraAssertions = null)
    {
        public string[] PreviewMustNotContain { get; init; } = PreviewMustNotContain ?? [];
        public string[] TextMustBeRemoved { get; init; } = TextMustBeRemoved ?? [];
        public string[] TextMustRemain { get; init; } = TextMustRemain ?? [];
        public string[] SavedBytesMustNotContain { get; init; } = SavedBytesMustNotContain ?? [];
    }

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static byte[] Obj(string dict) => Encoding.Latin1.GetBytes(dict);

    private static byte[] PdfStream(string dictExtra, string content) =>
        PdfStream(dictExtra, Encoding.Latin1.GetBytes(content));

    private static byte[] PdfStream(string dictExtra, byte[] data)
    {
        var head = Encoding.Latin1.GetBytes($"<< {dictExtra} /Length {data.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        return head.Concat(data).Concat(tail).ToArray();
    }

    private static byte[] BuildPdf(params byte[][] objects)
    {
        using var ms = new MemoryStream();
        void Write(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n");
        var offsets = new long[objects.Length + 1];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Root 1 0 R /Size {objects.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
