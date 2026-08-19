using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1074 — an annotation's <c>/Contents</c> was unreachable anywhere in the
/// GUI. For a <c>/Text</c> annotation, whose icon is the ONLY thing it draws,
/// that meant the note could not be read at all: the reviewer saw a coloured
/// square, while <c>PdfDocumentSanitizer</c> could see the text perfectly well
/// and scrubbed it on redaction (#608). A redaction reviewer being unable to
/// read a carrier they are deciding about is the reason this is not cosmetic.
///
/// <para>These raise REAL pointer moves at the annotation's own rect and assert
/// the ViewModel surfaced the text — never calling the hover handler directly.
/// Both view modes are covered because their coordinate paths differ, and
/// exactly one of them being wired is the failure this control's own
/// "no new coordinate math" comment exists to prevent.</para>
///
/// <para>Read and display only: nothing here creates, edits or deletes an
/// annotation, so it does not touch the frozen authoring work.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class AnnotationHoverReadingTests : IDisposable
{
    private const int RenderDpi = 96;
    private const string NoteText = "Budget figures need review before release.";
    private const string Author = "M. Jones";

    private readonly System.Collections.Generic.List<string> _temp = new();

    [FixedAvaloniaFact]
    public async Task HoveringAStickyNote_ShowsItsAuthorAndContents()
    {
        var (window, vm, viewer) = await OpenWithNote();
        try
        {
            await MoveOverTheNote(window, vm, viewer);

            vm.HoveredAnnotationInfo.Should().NotBeNull(
                "hovering a /Text annotation must surface its note — before #1074 the " +
                "GUI read no annotation's /Contents anywhere at all");
            vm.HoveredAnnotationInfo.Should().Contain(NoteText);
            vm.HoveredAnnotationInfo.Should().Contain(Author,
                "the reviewer needs to know WHO left the note, not only what it says");
            vm.StatusBarText.Should().Contain(NoteText,
                "the status bar is where it becomes readable; a property nothing displays " +
                "would leave the note exactly as unreachable as before");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The negative control. Without it this suite would pass on a build that
    /// reported the note no matter where the pointer was.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task MovingAwayFromTheNote_ClearsTheHoverText()
    {
        var (window, vm, viewer) = await OpenWithNote();
        try
        {
            await MoveOverTheNote(window, vm, viewer);
            vm.HoveredAnnotationInfo.Should().NotBeNull("precondition: the hover must register first");

            // Somewhere on the page that is not the note. The note sits at PDF
            // (300,500); the page origin corner is nowhere near it at any zoom.
            var surface = PageSurface(viewer);
            var away = surface.TranslatePoint(new Point(2, 2), window);
            if (away is { } pt) await Move(window, pt);

            vm.HoveredAnnotationInfo.Should().BeNull(
                "moving off the annotation must clear the status text, or the note would " +
                "follow the pointer around the document forever");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Continuous mode maps the pointer through a different coordinate path
    /// (page slots, ContinuousDips). Both go through the same
    /// TryMapPointerToContent helper — this is what proves it.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task HoveringAStickyNote_WorksInContinuousMode()
    {
        var (window, vm, viewer) = await OpenWithNote(PdfViewMode.Continuous);
        try
        {
            await MoveOverTheNote(window, vm, viewer);

            vm.HoveredAnnotationInfo.Should().NotBeNull(
                "continuous mode must read annotations too — a hover wired only for the " +
                "paged path is the exact drift the shared mapping exists to stop");
            vm.HoveredAnnotationInfo.Should().Contain(NoteText);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A degenerate <c>/Rect</c> is NORMAL for /Text (§12.5.6.4 — the icon is a
    /// fixed size regardless), and the renderer normalises it to
    /// <see cref="PdfAnnotation.TextIconSize"/> before drawing. If the hit-test
    /// used the raw rect instead, such a note would draw an icon the user can
    /// see and can never hover.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ANoteWithADegenerateRect_IsStillHoverable()
    {
        var (window, vm, viewer) = await OpenWithNote(degenerateRect: true);
        try
        {
            await MoveOverTheNote(window, vm, viewer);

            vm.HoveredAnnotationInfo.Should().NotBeNull(
                "the icon is drawn at a normalised 17pt box, so the hit-test must use the " +
                "same box — otherwise the marker is visible but unreachable");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// /Contents has no length limit and a status bar does, and a multi-line
    /// note must not break the line. Asserted through the real hover path
    /// rather than against an exposed formatting helper — the property that
    /// matters is what reaches the status bar, not what a private method
    /// returns.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ALongMultiLineNote_IsFlattenedAndTruncatedForTheStatusBar()
    {
        var (window, vm, viewer) = await OpenWithNote(longMultiLineNote: true);
        try
        {
            await MoveOverTheNote(window, vm, viewer);

            var shown = vm.HoveredAnnotationInfo;
            shown.Should().NotBeNull("precondition: the long note must register as hovered");
            shown!.Should().NotContain("\n", "a newline would break the status line");
            shown.Should().NotContain("\r");
            shown.Length.Should().BeLessThan(300,
                "/Contents can be kilobytes; the status bar shows a summary, not the file");
            shown.Should().Contain("First line",
                "truncation must keep the BEGINNING — the end of a long note is the " +
                "part a reviewer can afford to lose");
        }
        finally { window.Close(); }
    }

    // ── fixture / driving ────────────────────────────────────────────────────

    private async Task<(Window Window, MainWindowViewModel Vm, PdfViewerControl Viewer)>
        OpenWithNote(PdfViewMode mode = PdfViewMode.SinglePage, bool degenerateRect = false,
                     bool longMultiLineNote = false)
    {
        var path = WriteTempPdf(NotePdf(degenerateRect, longMultiLineNote));
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 800 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        vm.ViewMode = mode;
        for (int i = 0; i < 10; i++) { await Task.Delay(100); window.UpdateLayout(); }
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        return (window, vm, viewer);
    }

    /// <summary>
    /// Sweep the pointer over the laid-out PAGE surface until the note is
    /// found.
    ///
    /// <para>The basis matters and cost an hour: sweeping the VIEWER's own
    /// bounds finds nothing, because the page overlay lives inside a zoom host
    /// and is far larger than the viewport (530x669 viewer, 1022x1321 overlay).
    /// The sweep therefore runs over the biggest laid-out page surface —
    /// OverlayCanvas in single-page mode, ContinuousItems in continuous — which
    /// is the surface the annotation's coordinates actually map onto.</para>
    ///
    /// <para>A sweep rather than one computed point, deliberately: computing it
    /// needs the viewer's private render DPI, and a test that hard-codes a DPI
    /// asserts the layout rather than the behaviour. What is under test is
    /// "hovering the note reads it", and the sweep says exactly that without
    /// pinning either mode's scale.</para>
    /// </summary>
    private static async Task MoveOverTheNote(
        Window window, MainWindowViewModel vm, PdfViewerControl viewer)
    {
        var surface = PageSurface(viewer);
        var b = surface.Bounds;
        const int steps = 40;
        for (int i = 0; i <= steps && vm.HoveredAnnotationInfo == null; i++)
            for (int j = 0; j <= steps && vm.HoveredAnnotationInfo == null; j++)
            {
                var p = surface.TranslatePoint(
                    new Point(b.Width * i / steps, b.Height * j / steps), window);
                if (p is { } pt) await Move(window, pt);
            }
    }

    /// <summary>The laid-out page surface: OverlayCanvas paged, ContinuousItems continuous.</summary>
    private static Control PageSurface(PdfViewerControl viewer) =>
        viewer.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.Name is "OverlayCanvas" or "ContinuousItems")
            .Where(c => c.Bounds.Width > 1 && c.Bounds.Height > 1)
            .OrderByDescending(c => c.Bounds.Width * c.Bounds.Height)
            .FirstOrDefault()
        ?? viewer;

    private static async Task Move(Window window, Point p)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(p));
        window.UpdateLayout();
    }

    private static byte[] NotePdf(bool degenerateRect, bool longMultiLineNote = false)
    {
        var rect = degenerateRect ? "[300 500 300 500]" : "[300 500 324 524]";
        // \n inside a PDF literal string is a real newline (§7.3.4.2), which is
        // exactly the input the status line must survive.
        var contents = longMultiLineNote
            ? "First line of the note\\nsecond line\\n" + new string('x', 400)
            : NoteText;
        var annot = $"<< /Type /Annot /Subtype /Text /F 4 /Rect {rect} /Name /Comment " +
                    $"/T ({Author}) /Contents ({contents}) /C [1 0.85 0.2] >>";
        var objs = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
        };
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objs) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private string WriteTempPdf(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-note-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
