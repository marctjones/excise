using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #816 batch 3: execute the REAL redaction-button and search-bar ReactiveCommands
/// (not the scripting methods, not RedactionWorkflow directly) and assert their
/// real effects.
///
/// The prior coverage gap (see GoldenPathTests.GoldenPath_OpenRedactApplyVerifyTextGone):
/// ApplyAllRedactionsCommand was executed but, because headless has no
/// desktop-lifetime MainWindow and no interactive save picker, the command bailed
/// before the pipeline ran — the test only asserted "document still open". Here the
/// save destination is supplied via the SetRedactedSavePathProviderForTests seam so
/// the command runs end-to-end, and removal is proven with an INDEPENDENT oracle
/// (saved bytes + mutool), never excise's own extractor (CLAUDE.md no-self-oracle).
/// </summary>
[Collection("AvaloniaTests")]
public class RedactionAndSearchCommandTests
{
    private readonly string _tempDir;

    public RedactionAndSearchCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "excise-816-batch3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // ---------------------------------------------------------------------
    // Redaction button commands
    // ---------------------------------------------------------------------

    // ApplyAllRedactionsCommand (sidebar "Apply All"): the security-critical one.
    // Mark a pending over known secret text, execute the COMMAND, then assert the
    // secret is GONE from the SAVED FILE via a CARRIER-AGNOSTIC independent oracle
    // (raw saved bytes, ASCII + UTF-16BE — never excise's own extractor). The
    // survivor token doubles as an in-file negative control: it proves the
    // byte-scan can actually detect plaintext in this (uncompressed) redacted
    // content stream, so the "secret absent" assertion means removal, not
    // compression. This test ALWAYS runs (no tool dependency).
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ApplyAllRedactionsCommand_RemovesSecretFromSavedFile_SavedBytesOracle()
    {
        var (outputPath, savedText, _) = await RunApplyAllRedactionAsync("apply-all");

        // NEGATIVE CONTROL: the survivor is really visible to this byte-scan, so a
        // "not found" for the secret below means removal — not compression/encoding.
        savedText.Should().Contain(Survivor,
            "the un-redacted survivor token must be detectable by the byte-scan (negative control: proves the oracle can fail)");

        // INDEPENDENT, CARRIER-AGNOSTIC ORACLE (saved bytes, ASCII + UTF-16BE).
        savedText.Should().NotContain(Secret,
            "the redacted secret must be gone from every carrier in the saved bytes");

        File.Exists(outputPath).Should().BeTrue();
    }

    // Second, STRONGER oracle: an independent extractor (mutool) — a tool that is
    // not excise — must not be able to read the redacted secret back out, and must
    // still read the survivor. Skips cleanly when mutool isn't installed (tool-less
    // CI); allow-listed in tests/skip-allowlist/Excise.App.Tests.txt.
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ApplyAllRedactionsCommand_RedactedSecret_NotReadableByIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var (outputPath, _, _) = await RunApplyAllRedactionAsync("apply-all-mutool");

        var extracted = MutoolTextExtractor.ExtractPage(outputPath, 1);
        extracted.Should().NotBeNull("mutool must be able to read the redacted copy");
        extracted!.Should().NotContain(Secret,
            "an independent extractor must not read the redacted secret out of the saved file");
        extracted.Should().Contain(Survivor,
            "redaction must not destroy content outside the marked area");
    }

    private const string Secret = "TARGETSECRETXYZ816";
    private const string Survivor = "SURVIVORTOKEN816";

    /// <summary>
    /// Shared driver: load a two-token PDF, mark a pending over the top secret,
    /// stub the save-path dialog, execute the REAL ApplyAllRedactionsCommand, and
    /// return the output path plus the saved bytes as ASCII+UTF-16BE text.
    /// </summary>
    private async Task<(string OutputPath, string SavedText, byte[] SavedBytes)> RunApplyAllRedactionAsync(string tag)
    {
        var sourcePath = Path.Combine(_tempDir, $"{tag}-source.pdf");
        var outputPath = Path.Combine(_tempDir, $"{tag}-output.pdf");
        CreateTwoTokenPdf(sourcePath, Secret, Survivor);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(sourcePath);

        // Stub the save-path dialog (headless has no picker / desktop MainWindow).
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(outputPath));

        // Mark a pending redaction over the top token only, in content space.
        vm.IsRedactionMode = true;
        vm.RedactionWorkflow.MarkArea(
            PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 675, 500, 750)),
            Secret);
        vm.RedactionWorkflow.PendingCount.Should().Be(1, "one area was marked");

        // Execute the REAL command (not the scripting ApplyRedactionsCommand()).
        await vm.ApplyAllRedactionsCommand!.Execute();

        File.Exists(outputPath).Should().BeTrue("the Apply All command must write the redacted copy");
        vm.RedactionWorkflow.PendingCount.Should().Be(0, "applied redactions move out of pending");

        window.Close();
        var savedBytes = File.ReadAllBytes(outputPath);
        var savedText = Encoding.ASCII.GetString(savedBytes) + Encoding.BigEndianUnicode.GetString(savedBytes);
        return (outputPath, savedText, savedBytes);
    }

    // ApplyRedactionCommand (toolbar "Apply"). Trace of ApplyRedactionAsync: in
    // redaction mode with a current area it MARKS a pending and returns (the
    // immediate-redact branch below it is unreachable in that state). So the real
    // effect of the toolbar Apply command here is: a pending redaction is created.
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ApplyRedactionCommand_WithCurrentArea_MarksPendingRedaction()
    {
        var sourcePath = Path.Combine(_tempDir, "apply-toolbar-source.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "TOOLBARSECRET816");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(sourcePath);

        vm.IsRedactionMode = true;
        vm.CurrentRedactionPageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 660, 500, 740));
        vm.RedactionWorkflow.PendingCount.Should().Be(0, "nothing marked yet");

        await vm.ApplyRedactionCommand!.Execute();

        vm.RedactionWorkflow.PendingCount.Should().Be(1,
            "executing the toolbar Apply command in redaction mode marks the drawn area as pending");

        window.Close();
    }

    // ClearAllRedactionsCommand: mark redactions, execute → pending == 0 and no
    // redaction was applied to the document (applied list stays empty).
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ClearAllRedactionsCommand_ClearsPending_WithoutApplying()
    {
        var sourcePath = Path.Combine(_tempDir, "clear-source.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "CLEARSECRET816");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(sourcePath);

        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 660, 300, 740)), "a");
        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 400, 300, 480)), "b");
        vm.RedactionWorkflow.PendingCount.Should().Be(2);

        await vm.ClearAllRedactionsCommand!.Execute();

        vm.RedactionWorkflow.PendingCount.Should().Be(0, "clear removes all pending redactions");
        vm.RedactionWorkflow.AppliedCount.Should().Be(0, "clearing must not apply anything to the document");

        window.Close();
    }

    // RemovePendingRedactionCommand: mark two, execute with one's Id, assert only
    // that one is gone (the other remains).
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task RemovePendingRedactionCommand_RemovesOnlyTheTargetedPending()
    {
        var sourcePath = Path.Combine(_tempDir, "remove-source.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "REMOVESECRET816");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(sourcePath);

        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 660, 300, 740)), "first");
        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 400, 300, 480)), "second");
        var toRemove = vm.RedactionWorkflow.PendingRedactions[0];
        var toKeep = vm.RedactionWorkflow.PendingRedactions[1];

        await vm.RemovePendingRedactionCommand!.Execute(toRemove.Id);

        vm.RedactionWorkflow.PendingCount.Should().Be(1, "exactly one pending should be removed");
        vm.RedactionWorkflow.PendingRedactions.Should().ContainSingle()
            .Which.Id.Should().Be(toKeep.Id, "the pending that was NOT targeted must remain");

        window.Close();
    }

    // ---------------------------------------------------------------------
    // Search bar commands
    // ---------------------------------------------------------------------

    // FindCommand: execute with search text → matches are found/populated.
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task FindCommand_PopulatesMatches()
    {
        var (vm, window) = await OpenMultiPageAsync("find-source.pdf");
        vm.SearchText = "Content"; // "Page N Content" appears once per page

        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);

        vm.SearchMatches.Count.Should().BeGreaterThanOrEqualTo(3,
            "the manual Find command must find the search term on every page");
        vm.CurrentSearchMatchIndex.Should().Be(0, "the first match is selected after a find");

        window.Close();
    }

    // FindNextCommand / FindPreviousCommand: index advances and wraps correctly.
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task FindNextAndPreviousCommands_AdvanceAndWrapCurrentMatchIndex()
    {
        var (vm, window) = await OpenMultiPageAsync("findnav-source.pdf");
        vm.SearchText = "Content";
        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);

        var total = vm.SearchMatches.Count;
        total.Should().BeGreaterThanOrEqualTo(2, "need at least two matches to test navigation");
        vm.CurrentSearchMatchIndex.Should().Be(0);

        await vm.FindNextCommand!.Execute();
        vm.CurrentSearchMatchIndex.Should().Be(1, "Find Next advances to the next match");

        await vm.FindPreviousCommand!.Execute();
        vm.CurrentSearchMatchIndex.Should().Be(0, "Find Previous steps back one match");

        await vm.FindPreviousCommand!.Execute();
        vm.CurrentSearchMatchIndex.Should().Be(total - 1, "Find Previous from the first match wraps to the last");

        await vm.FindNextCommand!.Execute();
        vm.CurrentSearchMatchIndex.Should().Be(0, "Find Next from the last match wraps back to the first");

        window.Close();
    }

    // JumpToSearchMatchCommand: execute with a match on another page → current page
    // changes to that match's page.
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task JumpToSearchMatchCommand_NavigatesToTheMatchPage()
    {
        var (vm, window) = await OpenMultiPageAsync("jump-source.pdf");
        vm.SearchText = "Content";
        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);

        // A find auto-navigates to the FIRST match on a Background-priority post.
        // Let that settle BEFORE we jump, or a late-firing auto-navigate could
        // clobber CurrentPageIndex after our JumpTo and fail the assertion.
        await SettleDispatcher();

        // Pick a match on a page other than the current one.
        var target = vm.SearchMatches.First(m => m.PageIndex != vm.CurrentPageIndex);
        target.PageIndex.Should().NotBe(vm.CurrentPageIndex);

        await vm.JumpToSearchMatchCommand!.Execute(target);

        vm.CurrentPageIndex.Should().Be(target.PageIndex,
            "jumping to a search match must navigate the viewer to that match's page");

        window.Close();
    }

    // CloseSearchCommand: execute → IsSearchVisible == false and results cleared.
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task CloseSearchCommand_HidesSearchAndClearsResults()
    {
        var (vm, window) = await OpenMultiPageAsync("close-source.pdf");
        vm.IsSearchVisible = true;
        vm.SearchText = "Content";
        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);
        vm.SearchMatches.Count.Should().BeGreaterThan(0);

        await vm.CloseSearchCommand!.Execute();

        vm.IsSearchVisible.Should().BeFalse("Close Search hides the search bar");
        vm.SearchMatches.Count.Should().Be(0, "Close Search clears the results");
        vm.CurrentSearchMatchIndex.Should().Be(-1, "no match is selected after closing search");

        window.Close();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenMultiPageAsync(string fileName, int pageCount = 3)
    {
        var sourcePath = Path.Combine(_tempDir, fileName);
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(sourcePath);
        return (vm, window);
    }

    private static async Task WaitForMatches(MainWindowViewModel vm, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (vm.SearchMatches.Count > 0)
                return;
            await Task.Delay(100);
        }
    }

    // Drain queued Background-priority dispatcher posts (e.g. the auto-navigate
    // to the first search match) so they cannot fire after a later assertion.
    private static async Task SettleDispatcher()
    {
        for (var i = 0; i < 6; i++)
            await Task.Delay(100);
    }

    private static void CreateTwoTokenPdf(string path, string topSecret, string bottomSurvivor)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var g = page.GetGraphics();
        var font = PdfFont.Helvetica(18);
        g.DrawString(topSecret, font, PdfBrush.Black, 100, 700);   // near the top (PDF y up)
        g.DrawString(bottomSurvivor, font, PdfBrush.Black, 100, 120); // near the bottom, outside the marked area
        g.Flush();
        doc.Save(path);
    }
}
