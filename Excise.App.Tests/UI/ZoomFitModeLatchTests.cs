using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1014 — an explicit assignment to <see cref="MainWindowViewModel.ZoomLevel"/>
/// must END fit mode, and an automatic fit must not.
///
/// The defect: <c>_zoomFitMode</c> defaults to <c>FitWidth</c> and the setter
/// never cleared it, so the next viewport change ran
/// <c>ReapplyFitModeIfNeeded()</c> and silently overwrote the value that had
/// just been assigned. A public settable property whose writes were transient.
///
/// It surfaced as an ORDER-DEPENDENT failure of
/// UserFlowAutomationTests.ZoomLevel_CanBeAdjusted — green alone, red in the
/// full unchunked run, because only there did something resize the viewport
/// inside the window between the assignment and the assertion. These two tests
/// pin the behaviour directly instead, so it cannot go back to being a
/// once-per-suite coin flip.
///
/// The pair matters: the first fails without the fix, the second fails if the
/// fix is applied too broadly and stops fit mode latching at all.
/// </summary>
[Collection("AvaloniaTests")]
public class ZoomFitModeLatchTests
{
    [FixedAvaloniaFact]
    public async Task AssigningZoomLevel_SurvivesAViewportChange()
    {
        var src = Path.Combine(Path.GetTempPath(), $"excise-zoomlatch-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 2);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            vm.ViewportWidth = 1000;
            vm.ViewportHeight = 700;

            vm.ZoomLevel = 1.5;

            // The trigger. ViewportWidth's setter calls ReapplyFitModeIfNeeded(),
            // which before #1014 re-ran fit-width and clobbered the 1.5.
            vm.ViewportWidth = 800;

            vm.ZoomLevel.Should().Be(1.5,
                "assigning ZoomLevel is an explicit zoom, so it must end fit mode — " +
                "otherwise the next viewport change silently reverts it (#1014)");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(src);
        }
    }

    [FixedAvaloniaFact]
    public async Task FitWidth_StillReappliesOnAViewportChange()
    {
        var src = Path.Combine(Path.GetTempPath(), $"excise-zoomfit-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 2);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            vm.ViewportWidth = 1000;
            vm.ViewportHeight = 700;

            await vm.ZoomFitWidthCommand.Execute();
            var fittedAt1000 = vm.ZoomLevel;

            // Fit mode is latched, so a narrower viewport must re-fit — the fit
            // routines assign ZoomLevel themselves, and #1014's fix must not let
            // that assignment clear the latch they just set.
            vm.ViewportWidth = 600;

            vm.ZoomLevel.Should().BeLessThan(fittedAt1000,
                "fit-width stays latched until the user zooms manually, so a narrower " +
                "viewport must produce a smaller zoom");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(src);
        }
    }
}
