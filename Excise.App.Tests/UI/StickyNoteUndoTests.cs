using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Sticky-note text edits and card moves are undoable (#1810). A note's text is not page text, so
/// the independent reader is qpdf's JSON dump of the SAVED file, not excise's own parser.
/// </summary>
[Collection("AvaloniaTests")]
public class StickyNoteUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-noteundo-{Guid.NewGuid():N}");
    public StickyNoteUndoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string QpdfJson(string pdf)
    {
        var psi = new ProcessStartInfo("qpdf", $"--json=2 \"{pdf}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        // #1068/#1516: drain both pipes concurrently and bound the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qpdf did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>The /Rect of every /Popup annotation in the qpdf JSON.</summary>
    private static double[][] PopupRects(string json)
    {
        var found = new System.Collections.Generic.List<double[]>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("/Subtype", out var st) && st.GetString() == "/Popup" &&
                    e.TryGetProperty("/Rect", out var rect) && rect.ValueKind == JsonValueKind.Array)
                    found.Add(rect.EnumerateArray().Select(x => x.GetDouble()).ToArray());
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x);
        }
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement);
        return found.ToArray();
    }

    private async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenWithNoteAsync(string firstText)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        await vm.PlaceStickyNoteAsync(1, 100, 700);
        vm.StickyNotePopup.Should().NotBeNull("placing a note opens its card for typing");
        vm.StickyNotePopup!.Text = firstText;
        vm.CommitOpenStickyNotePopup();
        for (var i = 0; i < 40 && vm.StickyNotePopup != null; i++) await Task.Delay(50);
        return (vm, window);
    }

    private static void RequireQpdf() => Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task EditingANotesText_CanBeUndone_AndTheSavedFileFollows()
    {
        RequireQpdf();
        var (vm, window) = await OpenWithNoteAsync("FIRSTTEXT");
        vm.UndoMenuHeader.Should().Be("_Undo Edit sticky note");

        await vm.UndoCommand.Execute();

        var saved = Path.Combine(_dir, "undone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfJson(saved).Should().NotContain("FIRSTTEXT", "the undone text must not reach the saved file");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task EditingANotesText_UndoThenRedo_RestoresTheEdit()
    {
        RequireQpdf();
        var (vm, window) = await OpenWithNoteAsync("SECONDTEXT");

        await vm.UndoCommand.Execute();
        await vm.RedoCommand.Execute();

        var saved = Path.Combine(_dir, "redone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfJson(saved).Should().Contain("SECONDTEXT");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task MovingANotesCard_CanBeUndone_AndTheSavedPopupRectIsBack()
    {
        RequireQpdf();
        var (vm, window) = await OpenWithNoteAsync("MOVEME");
        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        var original = note.PopupRect!.Value;
        var iconRect = note.Rect;

        var moved = new PdfRectangle(original.Left + 150, original.Bottom - 100, original.Right + 150, original.Top - 100);
        await vm.MoveStickyNoteAsync(1, iconRect, moved);
        vm.UndoMenuHeader.Should().Be("_Undo Move sticky note");

        await vm.UndoCommand.Execute();

        var saved = Path.Combine(_dir, "moved-undone.pdf");
        await vm.SaveFileAsAsync(saved);
        var rects = PopupRects(QpdfJson(saved));
        rects.Should().ContainSingle("one note, one linked popup");
        rects[0][0].Should().BeApproximately(original.Left, 0.01);
        rects[0][1].Should().BeApproximately(original.Bottom, 0.01);
        rects[0][2].Should().BeApproximately(original.Right, 0.01);
        rects[0][3].Should().BeApproximately(original.Top, 0.01);
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ClickingAwayWithoutChangingTheText_AddsNoUndoEntry()
    {
        var (vm, window) = await OpenWithNoteAsync("UNCHANGED");   // history: add note, edit text
        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Single(a => a.Subtype == PdfAnnotationSubtype.Text);

        vm.ReopenStickyNote(1, note.Rect);
        vm.StickyNotePopup.Should().NotBeNull();
        vm.CommitOpenStickyNotePopup();                           // collapse with the SAME text
        for (var i = 0; i < 40 && vm.StickyNotePopup != null; i++) await Task.Delay(50);

        // Count entries by popping them: an extra "Edit sticky note" has the same label as the real
        // one, so comparing the menu header could never see it.
        var entries = 0;
        while (vm.CanUndo && entries < 10)
        {
            await vm.UndoCommand.Execute();
            entries++;
        }
        entries.Should().Be(2, "add note + one real text edit; a collapse with unchanged text is not an edit");
        window.Close();
    }
}
