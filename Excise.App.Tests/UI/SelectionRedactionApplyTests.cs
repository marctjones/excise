using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1905 and #2066 through the real view model: select a word, right-click Redact
/// (#1659), add a dragged mark, change the page order, Apply, and read the SAVED file
/// with the stream-decompressing leak scanner and with mutool (text and pixels). The
/// selected word's underline goes with it; the next word's underline, the dragged
/// mark's neighbourhood and another occurrence of the same word on another page stay.
/// The font edit's own evidence (outlines, /ToUnicode, identical pixels) is in
/// Excise.Rendering.Tests RedactionFontSubsetCarrierTests; here the redacted-copy
/// dialog must report the decoration removal and no font left unscrubbed.
/// </summary>
[Collection("AvaloniaTests")]
public class SelectionRedactionApplyTests : IDisposable
{
    private const string Secret = "Жуков";
    private const double PageHeight = 792;
    private const int Dpi = 110;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-selredact-{Guid.NewGuid():N}");

    public SelectionRedactionApplyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingDialogService : IUserDialogService
    {
        public System.Collections.Generic.List<string> Messages { get; } = new();
        public Task ShowMessageAsync(string title, string message) { Messages.Add(message); return Task.CompletedTask; }
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    private readonly RecordingDialogService _dialog = new();

    /// <summary>
    /// Page 1 "Signed: Жуков Smith" with an underline under each name, page 2 a sentence,
    /// page 3 "Witness: Жуков": one subset Type0 DejaVu Sans font for all of it.
    /// </summary>
    private (string Path, PdfRectangle SecretBox, PdfRectangle SecretUnderline, PdfRectangle SmithUnderline,
        PdfRectangle ApprovedBox) Fixture()
    {
        var fontPath = TestRepoLayout.FindFile("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
        Assert.SkipWhen(fontPath == null, TestRepoLayout.AbsenceReason(
            "DejaVu Sans fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));
        byte[] drawn;
        {
            var doc = PdfDocument.CreateNew();
            var font = PdfFont.FromTrueType(File.ReadAllBytes(fontPath!), 18);
            foreach (var line in new[] { $"Signed: {Secret} Smith", "Invoice approved by the finance office.", $"Witness: {Secret}" })
            {
                var page = doc.Pages.AddBlank(612, PageHeight);
                using var g = page.GetGraphics();
                g.DrawString(line, font, PdfBrush.Black, 72, 600);
            }
            drawn = doc.SaveToBytes();
        }

        using var withRules = PdfDocument.Open(drawn);
        var first = withRules.GetPage(1);
        var secretBox = Box(first, Secret);
        var smithBox = Box(first, "Smith");
        var approvedBox = Box(withRules.GetPage(2), "approved");
        static PdfRectangle Under(PdfRectangle word) => new(word.Left, word.Bottom - 2.5, word.Right, word.Bottom - 1.3);
        PdfDocumentRedactionExtensions.AppendBlackRectangles(first, new[] { Under(secretBox) });
        PdfDocumentRedactionExtensions.AppendBlackRectangles(first, new[] { Under(smithBox) });
        var path = Path.Combine(_dir, "source.pdf");
        File.WriteAllBytes(path, withRules.SaveToBytes());
        return (path, secretBox, Under(secretBox), Under(smithBox), approvedBox);
    }

    private static PdfRectangle Box(PdfPage page, string text)
    {
        var match = PdfDocumentRedactionExtensions.FindTextMatches(page.Letters, text, caseSensitive: true);
        match.Should().ContainSingle($"fixture sanity: '{text}' is drawn once on page {page.PageNumber}");
        return PdfDocumentRedactionExtensions.BoundingBoxOf(match[0]);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task SelectRedactApplySave_RemovesTheWordAndItsUnderline_KeepsEverythingElse()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var (source, secretBox, secretUnderline, smithUnderline, approvedBox) = Fixture();
        using (var before = MutoolReferenceRenderer.RenderPage(source, 1, Dpi))
        {
            InkFractionIn(before!, secretUnderline).Should().BeGreaterThan(0.5, "fixture sanity: the underline is drawn");
            InkFractionIn(before!, smithUnderline).Should().BeGreaterThan(0.5, "fixture sanity");
        }

        var vm = MainWindowViewModelTestFactory.Create(dialogService: _dialog);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        // The selection the viewer publishes, then the context menu's Redact.
        vm.CurrentTextSelectionPageArea = PdfPageRect.FromContentPoints(1, secretBox);
        vm.SelectedText = Secret;
        vm.HasTextSelection.Should().BeTrue();
        await vm.RedactSelectionCommand.Execute();
        vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle()
            .Which.FromTextSelection.Should().BeTrue("a mark made from a text selection says so");

        // A page operation between the marks: the witness page goes first, so the
        // selection's page is now page 2 and the sentence's page 3.
        await vm.MovePageAsync(2, 0);
        vm.RedactionWorkflow.PendingRedactions.Single().PageNumber.Should().Be(2, "the mark follows its page");

        // A dragged mark over "approved" on the sentence's page.
        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(3, approvedBox), "approved");
        vm.RedactionWorkflow.PendingRedactions.Last().FromTextSelection.Should().BeFalse("a dragged mark is not a selection");
        vm.FileState.PendingRedactionsCount = vm.RedactionWorkflow.PendingCount;

        var output = Path.Combine(_dir, "redacted.pdf");
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(output));
        await vm.ApplyAllRedactionsCommand!.Execute();
        File.Exists(output).Should().BeTrue("Apply must save the redacted copy");

        var bytes = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(bytes, "approved").Should().BeEmpty("the dragged mark's word is out of the file");
        var signedPage = MutoolTextExtractor.ExtractPage(output, 2)!;
        signedPage.Should().Contain("Signed").And.Contain("Smith").And.NotContain(Secret,
            "an independent reader must not see the selected word");
        MutoolTextExtractor.ExtractPage(output, 1)!.Should().Contain(Secret,
            "a selection is that one run: the same word on another page is not redacted");
        MutoolTextExtractor.ExtractPage(output, 3)!.Should().Contain("Invoice").And.NotContain("approved");

        using (var after = MutoolReferenceRenderer.RenderPage(output, 2, Dpi))
        {
            InkFractionIn(after!, secretUnderline).Should().BeLessThan(0.001,
                "the underline sized to the selected word states its width under the box (#1905)");
            InkFractionIn(after!, smithUnderline).Should().BeGreaterThan(0.5, "the next word's underline is not the selection's");
        }

        var dialog = _dialog.Messages.Should().ContainSingle(m => m.Contains("Redacted PDF saved to")).Which;
        dialog.Should().Contain("sized to a redacted word", "every removal is reported to the user");
        dialog.Should().NotContain("Carrier font", "every font of this document is one the edit handles");
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right * scale) - 1);
        int y0 = Math.Max(0, (int)Math.Floor((PageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling((PageHeight - box.Bottom) * scale) - 1);
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                total++;
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }
}
