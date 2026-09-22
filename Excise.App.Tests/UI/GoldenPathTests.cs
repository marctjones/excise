using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.TestSupport;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Golden path end-to-end tests for Excise.App.
/// Each test exercises a complete user workflow from start to finish,
/// verifying external state at every major step.
///
/// These tests prove the product works as a whole for realistic scenarios:
/// - Open → Search → Navigate → Close
/// - Open → Redact via area → Save → Reopen → Verify text gone
/// - Multi-page redaction across 3 pages
/// - Large PDF navigation responsiveness
/// - Recent files management with pinning
///
/// Tests are designed to be fast (under 10 seconds each) and self-contained.
/// </summary>
[Collection("AvaloniaTests")]
public class GoldenPathTests
{
    private readonly ITestOutputHelper _out;
    private readonly string _tempDir;

    public GoldenPathTests(ITestOutputHelper output)
    {
        _out = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppGoldenPath", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    private string CreateTestPdf(string nameHint = "test.pdf")
        => Path.Combine(_tempDir, nameHint);

    /// <summary>
    /// Drain queued Background-priority dispatcher posts (the auto-navigate to
    /// the first search match) so they cannot fire after a later assertion.
    /// </summary>
    private static async Task SettleDispatcher()
    {
        for (var i = 0; i < 6; i++)
            await Task.Delay(100);
    }

    #region Golden Path 1: Open → Search → Navigate → Close

    /// <summary>
    /// Golden Path 1: User opens a PDF, searches for text, navigates to the
    /// match, and closes the document. Tests that:
    /// - Document loads with correct page count
    /// - Search finds matches
    /// - Search results navigate to correct pages
    /// - Document can be closed cleanly
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_OpenSearchNavigateClose()
    {
        // Arrange
        var pdfPath = CreateTestPdf("search_navigate.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        // Step 1: Open document
        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Assert step 1: Document is open
        vm.TotalPages.Should().Be(5, "should load 5-page PDF");
        vm.CurrentPageIndex.Should().Be(0, "should start at first page");
        vm.DocumentName.Should().NotBeNullOrEmpty("document name should be set");
        vm.IsDocumentLoaded.Should().BeTrue("document should be loaded");
        vm.LastDocumentOpenTiming.Should().NotBeNull("open-to-first-page-visible timing should be captured");
        vm.LastDocumentOpenTiming!.FilePath.Should().Be(pdfPath);
        vm.LastDocumentOpenTiming.PageCount.Should().Be(5);
        vm.LastDocumentOpenTiming.FirstPageVisibleElapsedMs.Should()
            .BeGreaterThanOrEqualTo(vm.LastDocumentOpenTiming.DocumentInstancesLoadedElapsedMs);
        vm.LastDocumentOpenTiming.TotalLoadElapsedMs.Should()
            .BeGreaterThanOrEqualTo(vm.LastDocumentOpenTiming.FirstPageVisibleElapsedMs);

        // Step 2: Search for text present on multiple pages
        vm.SearchText = "Secret";

        // Poll for search completion with reasonable timeout
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);
        await Task.Delay(100);

        // Assert step 2: Search found matches
        vm.SearchMatches.Should().NotBeEmpty("should find 'Secret' on pages");
        vm.SearchMatches.Count.Should().BeGreaterThanOrEqualTo(3,
            "should find at least one match per page (3+ total)");

        // Step 3: Navigate to a match
        var firstMatch = vm.SearchMatches.FirstOrDefault();
        firstMatch.Should().NotBeNull("should have at least one match");
        vm.JumpToSearchMatch(firstMatch!);
        await SettleDispatcher();

        // Assert step 3: the viewer is on the match's OWN page. The previous
        // pair of assertions ("index >= 0" and "index < TotalPages") is true of
        // a build whose jump does nothing at all (#1769).
        vm.CurrentPageIndex.Should().Be(firstMatch!.PageIndex,
            "jumping to a search match must navigate the viewer to that match's page");

        // Step 4: Close document via command
        // Note: ReactiveCommand cannot be awaited directly; use Invoke() pattern
        try
        {
            vm.CloseDocumentCommand?.Execute().Subscribe();
        }
        catch { /* command may fail if no document open, that's ok */ }
        await Task.Delay(100);

        // Assert step 4: Document is closed
        vm.IsDocumentLoaded.Should().BeFalse("document should be closed");
        vm.TotalPages.Should().Be(0, "page count should reset");
        vm.SearchMatches.Count.Should().Be(0, "search results should clear");
    }

    #endregion

    #region Golden Path 2: Open → Redact → Apply → Verify Text Gone

    /// <summary>
    /// Golden Path 2: Security-critical workflow. User opens a PDF, marks an
    /// area over known text, applies, and saves the redacted copy — then the
    /// SAVED BYTES are searched for the secret in every carrier.
    ///
    /// <para>#1769: this test was named <c>VerifyTextGone</c> and verified no
    /// such thing. The apply ran inside <c>try { } catch { }</c> — and in
    /// headless, with no desktop-lifetime MainWindow and no save picker, it bailed
    /// before the pipeline ran at all — after which the only assertions were
    /// "the document is still open" and "it still has pages". Both are true of a
    /// build that redacts nothing, so a redaction regression could not redden
    /// it.</para>
    ///
    /// <para>The save destination now comes through the test seam so the command
    /// runs end to end, the apply is awaited rather than swallowed, and removal
    /// is proven by <see cref="SavedPdfLeakScanner.FindTerm"/> over the saved
    /// file — raw bytes and inflated streams — with the secret's presence in the
    /// INPUT as the control that the scan can see this file's text at all, and
    /// the survivor token as the control that it can still see text in the
    /// OUTPUT. (mutool on this same command path is covered by
    /// <c>RedactionAndSearchCommandTests.ApplyAllRedactionsCommand_RedactedSecret_NotReadableByIndependentExtractor</c>;
    /// repeating it here would pad the gate without adding an oracle.)</para>
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_OpenRedactApplyVerifyTextGone()
    {
        // Arrange. CreateMultiPagePdf draws "Page 1 Content" at PDF y=692 and
        // "Secret on Page 1" at y=592 — 100pt apart, so one rectangle can cover
        // the secret with the survivor comfortably outside it.
        const string Secret = "Secret on Page 1";
        const string Survivor = "Page 1 Content";
        var pdfPath = CreateTestPdf("redact_verify.pdf");
        var outputPath = CreateTestPdf("redact_verify_output.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);

        var sourceBytes = File.ReadAllBytes(pdfPath);
        SavedPdfLeakScanner.FindTerm(sourceBytes, Secret).Should().NotBeEmpty(
            "input-side control: the carrier scan must be able to find the secret BEFORE redaction, " +
            "or its absence afterwards would prove only that the scan is blind to this file");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            // Step 1: Open document
            await vm.LoadDocumentAsync(pdfPath);
            await Task.Delay(100);

            vm.IsDocumentLoaded.Should().BeTrue("document should be open");
            vm.TotalPages.Should().Be(1);
            vm.CurrentPageText.Should().Contain(Secret, "the secret is extractable before redaction");

            // Step 2: Mark the secret's area, in content coordinates.
            vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(outputPath));
            vm.IsRedactionMode = true;
            vm.RedactionWorkflow.MarkArea(
                PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 570, 500, 620)), Secret);
            vm.RedactionWorkflow.PendingCount.Should().Be(1, "should have 1 pending redaction");

            // Step 3: Apply. Awaited, not swallowed — a throw here is the test
            // failing, which is the whole point of a security workflow test.
            await vm.ApplyAllRedactionsCommand!.Execute();

            // Step 4: the redacted copy exists and the secret is gone from it.
            vm.RedactionWorkflow.PendingCount.Should().Be(0, "applied redactions leave the pending list");
            File.Exists(outputPath).Should().BeTrue("Apply All must write the redacted copy");

            var savedBytes = File.ReadAllBytes(outputPath);
            SavedPdfLeakScanner.FindTerm(savedBytes, Secret).Should().BeEmpty(
                "the redacted secret must be gone from every carrier of the saved file, " +
                "compressed streams included");
            SavedPdfLeakScanner.AllCarriersText(savedBytes).Should().Contain(Survivor,
                "output-side control: text outside the marked area must survive, which also proves " +
                "the scan can still read text out of the redacted file");

            vm.IsDocumentLoaded.Should().BeTrue("document should still be open after redaction");
        }
        finally
        {
            window.Close();
        }
    }

    #endregion

    #region Golden Path 4: Large PDF Responsiveness

    /// <summary>
    /// <summary>
    /// Golden Path 4: User opens a moderately large PDF (20+ pages) and verifies
    /// the application remains responsive. Tests:
    /// - Document loads within time budget (< 5 seconds)
    /// - Navigation to last page is fast (< 2 seconds)
    /// - Page rendering is quick
    ///
    /// Uses TestPdfGenerator to create a 20-page PDF on-the-fly.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_LargePdfResponsiveness()
    {
        // Arrange: Create a moderately large PDF (20 pages)
        var pdfPath = CreateTestPdf("large_pdf.pdf");
        var startGen = DateTime.UtcNow;
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 20);
        var genTime = DateTime.UtcNow - startGen;
        _out.WriteLine($"Test PDF generation took {genTime.TotalMilliseconds:F1}ms");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        // Step 1: Open document, measure time
        var startOpen = DateTime.UtcNow;
        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(200);  // Allow any async rendering to settle
        var openTime = DateTime.UtcNow - startOpen;
        _out.WriteLine($"Document open took {openTime.TotalMilliseconds:F1}ms");

        // Assert step 1: Open is responsive
        openTime.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "opening a 20-page PDF should be fast (< 5s)");
        vm.TotalPages.Should().Be(20, "should load all 20 pages");
        vm.CurrentPageIndex.Should().Be(0, "should start at page 1");
        vm.IsDocumentLoaded.Should().BeTrue("document should be loaded");

        // Step 2: Navigate to middle page
        var startNav1 = DateTime.UtcNow;
        vm.CurrentPageIndex = 10;
        await Task.Delay(100);
        var nav1Time = DateTime.UtcNow - startNav1;
        _out.WriteLine($"Navigation to page 11 took {nav1Time.TotalMilliseconds:F1}ms");

        // Assert step 2
        vm.CurrentPageIndex.Should().Be(10);

        // Step 3: Navigate to last page, measure time
        var startNav2 = DateTime.UtcNow;
        vm.CurrentPageIndex = 19;
        await Task.Delay(100);
        var nav2Time = DateTime.UtcNow - startNav2;
        _out.WriteLine($"Navigation to last page (20) took {nav2Time.TotalMilliseconds:F1}ms");

        // Assert step 3: Last page navigation is responsive
        vm.CurrentPageIndex.Should().Be(19, "should be on last page");
        nav2Time.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "navigating to last page should be fast (< 2s)");
    }

    #endregion

    #region Golden Path 5: Recent Files Management

    /// <summary>
    /// Golden Path 5: Recent files management. User opens two files and verifies
    /// they both appear in recent files.
    ///
    /// Tests:
    /// - Opening a file adds it to recent files
    /// - Multiple files are tracked
    /// - Current document reflects the opened file
    ///
    /// Note: Recent files ordering depends on implementation;
    /// this test verifies files are tracked, not sorting order.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_RecentFilesRoundTrip()
    {
        // Arrange: Create two test PDFs
        var pdfA = CreateTestPdf("recent_a.pdf");
        var pdfB = CreateTestPdf("recent_b.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfA, pageCount: 2);
        TestPdfGenerator.CreateMultiPagePdf(pdfB, pageCount: 3);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        // Step 1: Open file A
        await vm.LoadDocumentAsync(pdfA);
        await Task.Delay(100);

        // Assert step 1: File A appears in recent files
        vm.RecentFiles.Should().Contain(pdfA, "recent files should contain A");
        vm.IsDocumentLoaded.Should().BeTrue("document A should be loaded");

        // Step 2: Open file B
        await vm.LoadDocumentAsync(pdfB);
        await Task.Delay(100);

        // Assert step 2: File B is now current, document loaded
        vm.IsDocumentLoaded.Should().BeTrue("document B should be loaded");
        vm.DocumentName.Should().Contain("recent_b", "current document should be B");

        // Step 3: BOTH files are tracked. "A or B" was satisfied by step 1's
        // own assertion and so could never fail here (#1769); ordering is still
        // deliberately not asserted.
        vm.RecentFiles.Should().Contain(pdfA, "opening A must have recorded it");
        vm.RecentFiles.Should().Contain(pdfB, "opening B must have recorded it too");
    }

    #endregion

    #region Golden Path 6: Malformed PDF Graceful Failure

    /// <summary>
    /// Golden Path 6: Robustness test. User attempts to open a file with
    /// invalid PDF structure. Application should:
    /// - Handle gracefully (may log errors but not crash)
    /// - Keep the application in a usable state
    /// - Reject the file and leave no stale document session behind
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_MalformedPdfGracefulFailure()
    {
        // Arrange: Create a "PDF" file with random garbage
        var invalidPdfPath = CreateTestPdf("invalid.pdf");
        File.WriteAllBytes(invalidPdfPath, new byte[] {
            0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8, 0xF7, 0xF6
        });

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(invalidPdfPath);
        await Task.Delay(200);

        vm.IsDocumentLoaded.Should().BeFalse("invalid PDF should not load");
        vm.TotalPages.Should().Be(0, "page count should be 0 if load failed");
        vm.PdfCoreDocument.Should().BeNull("a failed open must not retain a parser instance");
        vm.PageThumbnails.Should().BeEmpty("a failed open must not retain thumbnail state");
        vm.OutlineNodes.Should().BeEmpty("a failed open must not retain outline state");
        vm.OperationStatus.Should().BeEmpty("the failed operation has completed");
    }

    [FixedAvaloniaFact]
    public async Task GoldenPath_FailedReplacementClearsPreviousDocumentSession()
    {
        var validPdfPath = CreateTestPdf("valid-before-failure.pdf");
        var invalidPdfPath = CreateTestPdf("invalid-replacement.pdf");
        TestPdfGenerator.CreateMultiPagePdf(validPdfPath, pageCount: 2);
        File.WriteAllBytes(invalidPdfPath, [0x25, 0x50, 0x44, 0x46, 0x2D]);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(validPdfPath);
        vm.IsDocumentLoaded.Should().BeTrue();
        vm.TotalPages.Should().Be(2);

        await vm.LoadDocumentAsync(invalidPdfPath);

        vm.IsDocumentLoaded.Should().BeFalse("the disposed previous service document must not remain visible");
        vm.TotalPages.Should().Be(0);
        vm.PdfCoreDocument.Should().BeNull();
        vm.PageThumbnails.Should().BeEmpty();
        vm.OutlineNodes.Should().BeEmpty();
        vm.LastDocumentOpenTiming.Should().BeNull();
    }

    #endregion

    #region Golden Path 7: Search Result Navigation

    /// <summary>
    /// Golden Path 7: User searches for a term that appears multiple times,
    /// then navigates by jumping to a specific result. Tests:
    /// - Search finds multiple matches
    /// - Jumping to a result navigates the page correctly
    /// - CurrentSearchMatchIndex updates
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_SearchResultNavigation()
    {
        // Arrange: Create PDF with repeated text across multiple pages
        var pdfPath = CreateTestPdf("search_nav.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        // Step 1: Open document and search
        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        vm.SearchText = "Page";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);
        await Task.Delay(100);

        // Assert step 1: Multiple matches found
        var totalMatches = vm.SearchMatches.Count;
        totalMatches.Should().BeGreaterThanOrEqualTo(5,
            "'Page' should appear on all 5 pages = 5+ matches");

        // Step 2: Navigate to a match in the middle
        var targetMatch = vm.SearchMatches.ElementAtOrDefault(totalMatches / 2);
        targetMatch.Should().NotBeNull("should have middle match");

        // A find auto-navigates to the FIRST match on a Background-priority
        // post; drain that before jumping, or a late-firing navigate could
        // clobber the assertion below.
        await SettleDispatcher();
        vm.JumpToSearchMatch(targetMatch!);
        await SettleDispatcher();

        // Assert step 2: the viewer is on the TARGET match's page. The previous
        // "index in bounds" pair could not fail (#1769).
        vm.CurrentPageIndex.Should().Be(targetMatch!.PageIndex,
            "jumping to the middle match must navigate to that match's page");
    }

    #endregion

    #region Golden Path 8: Document State Cleanup

    /// <summary>
    /// Golden Path 8: When closing a document, all related state is cleared.
    /// Tests:
    /// - Search results cleared
    /// - Pending redactions cleared
    /// - Page count reset to 0
    /// - Document marked as closed
    /// </summary>
    [FixedAvaloniaFact]
    public async Task GoldenPath_DocumentStateCleanup()
    {
        // Arrange
        var pdfPath = CreateTestPdf("state_cleanup.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        // Step 1: Open document, add search and redaction
        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        vm.SearchText = "Secret";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);

        vm.RedactionWorkflow.MarkArea(pageNumber: 1, area: new Rect(50, 100, 100, 30), previewText: "Test");
        await Task.Delay(50);

        // Assert step 1: State populated
        vm.IsDocumentLoaded.Should().BeTrue();
        vm.TotalPages.Should().Be(3);
        vm.SearchMatches.Count.Should().BeGreaterThan(0);
        vm.RedactionWorkflow.PendingCount.Should().Be(1);

        // Step 2: Close document via command
        try
        {
            vm.CloseDocumentCommand?.Execute().Subscribe(_ => { });
        }
        catch { /* expected; command safe to fail */ }
        await Task.Delay(100);

        // Assert step 2: All state cleaned up
        vm.IsDocumentLoaded.Should().BeFalse("should be closed");
        vm.TotalPages.Should().Be(0, "page count should reset");
        vm.SearchMatches.Count.Should().Be(0, "search results should clear");
        vm.RedactionWorkflow.PendingCount.Should().Be(0, "redactions should clear");
        vm.CurrentPageIndex.Should().Be(0, "page index should reset");
    }

    #endregion
}
