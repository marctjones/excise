using System;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Avalonia.Controls;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

/// <summary>
/// Programmatic page navigation must survive continuous mode.
///
/// Found by the v2.28.0 release gate. Making continuous scroll the default
/// exposed a latent bug in the viewer:
///
///   ScrollToPageContinuous sets ScrollViewer.Offset. A ScrollViewer CLAMPS
///   Offset to its extent, and before layout has run the extent is 0 — so the
///   assignment silently becomes Offset.Y = 0. The scroll handler then computes
///   "topmost visible page = 1" and OVERWRITES CurrentPage back to 1, swallowing
///   the navigation. Worse, when the document had only just loaded, the slots
///   were still null and the navigation was dropped before it was even attempted.
///
/// For a user, that is: open a document, immediately click an outline entry / type
/// a page number / jump to a search hit — and land on page 1 with no feedback.
///
/// It was invisible while single-page was the default, which is exactly why the
/// default change had to be gated. These tests pin the behaviour so it cannot
/// regress the next time the continuous scroll pipeline is touched.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousNavigationRegressionTests
{
    private static string TempPdf(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task NavigatingImmediatelyAfterOpen_IsNotSwallowedByTheScrollSync()
    {
        var path = TempPdf("continuous-nav.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(path);

        vm.IsContinuousView.Should().BeTrue("continuous scroll is the default view mode");

        // The instant after open — before layout has necessarily settled. This is
        // the exact window in which the navigation used to be lost.
        vm.CurrentPageIndex = 4;

        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(4,
            "a page set programmatically must stick. The ScrollViewer clamps Offset to a " +
            "not-yet-computed extent, and the scroll handler then derives CurrentPage from " +
            "that stale offset and snaps the user back to page 1.");
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ScrollDrivenPageSync_StillWorksAfterAPendingNavigationResolves()
    {
        // The inverse guard. The fix suppresses the scroll->CurrentPage sync while a
        // programmatic jump is in flight. If that suppression ever failed to clear,
        // the page number would freeze while the user scrolls — trading a lost jump
        // for a dead page counter.
        var path = TempPdf("continuous-nav-sync.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(path);

        vm.CurrentPageIndex = 3;
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(3);

        // And a subsequent navigation still lands — proving the pending-page latch
        // was released rather than left permanently engaged.
        vm.CurrentPageIndex = 6;
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(6,
            "the pending-navigation latch must clear once the jump lands, or every later " +
            "scroll/navigation is silently ignored");
    }
}
