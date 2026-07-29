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
/// The "Fit" toolbar button (ZoomFitWidthCommand) must fit the page AS DISPLAYED
/// — including its rotation. A portrait Letter page is 612pt wide; rotated 90° it
/// is displayed 792pt wide (landscape), so Fit-Width must compute a SMALLER zoom
/// to fit the wider page. If Fit ignores rotation it keeps computing against the
/// 612pt portrait width and the rotated page overflows the viewport — the reported
/// "Fit doesn't work on rotated pages, only portrait" bug.
/// </summary>
[Collection("AvaloniaTests")]
public class ZoomFitRotatedPageTests
{
    [FixedAvaloniaFact]
    public async Task FitWidth_MixedRotationDoc_FitsTheWidestPage_SoNoPageOverflows()
    {
        // The reported bug: a document with a MIX of portrait and rotated
        // (landscape) pages. Fit-Width fitted only the CURRENT page's width, so
        // when the current page was portrait the wider landscape pages overflowed
        // the shared-zoom continuous view and shifted off-centre. Fit must target
        // the WIDEST page so every page fits and centres.
        var src = Path.Combine(Path.GetTempPath(), $"excise-fitmix-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3); // three portrait Letter pages

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            vm.ViewportWidth = 1000;
            vm.ViewportHeight = 700;

            // Rotate page 2 to landscape, then leave the reader on page 1 (portrait).
            vm.CurrentPageIndex = 1;
            await vm.RotatePageRightCommand.Execute();
            vm.CurrentPageIndex = 0;
            vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be(0, "page 1 stays portrait");
            vm.PdfCoreDocument!.GetPage(2).Rotation.Should().Be(90, "page 2 is now landscape");

            const double dipsPerPoint = 96.0 / 72.0;
            const double margin = 8;
            double landscapeWidthDip = 792 * dipsPerPoint; // widest page = rotated page 2

            await vm.ZoomFitWidthCommand.Execute();

            vm.ZoomLevel.Should().BeApproximately((1000 - margin) / landscapeWidthDip, 0.02,
                "Fit-Width in a mixed-rotation document must fit the WIDEST page (the landscape page 2), " +
                "even though the current page (1) is portrait — otherwise the landscape page overflows and shifts off-centre");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(src);
        }
    }

    [FixedAvaloniaFact]
    public async Task FitWidth_AfterRotating90_FitsTheLandscapeWidth_NotThePortraitWidth()
    {
        var src = Path.Combine(Path.GetTempPath(), $"excise-fitrot-{Guid.NewGuid():N}.pdf");
        // Portrait US Letter: 612 x 792 pt.
        TestPdfGenerator.CreateCustomSizePdf(src, widthPoints: 612, heightPoints: 792);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(src);
            vm.ViewportWidth = 1000;
            vm.ViewportHeight = 700;

            const double dipsPerPoint = 96.0 / 72.0;
            const double margin = 8;
            double portraitWidthDip = 612 * dipsPerPoint;   // 816
            double landscapeWidthDip = 792 * dipsPerPoint;  // 1056

            await vm.ZoomFitWidthCommand.Execute();
            var zoomPortrait = vm.ZoomLevel;
            zoomPortrait.Should().BeApproximately((1000 - margin) / portraitWidthDip, 0.02,
                "fixture sanity: fit-width on the portrait page fits its 612pt width");

            // Rotate the current page 90° → displayed landscape (792pt wide).
            await vm.RotatePageRightCommand.Execute();
            vm.PdfCoreDocument!.GetPage(vm.CurrentPageIndex + 1).Rotation.Should().Be(90,
                "sanity: rotating the page must surface as page.Rotation=90 for the fit math to see it");

            await vm.ZoomFitWidthCommand.Execute();
            var zoomLandscape = vm.ZoomLevel;

            zoomLandscape.Should().BeApproximately((1000 - margin) / landscapeWidthDip, 0.02,
                "Fit-Width must fit the page AS ROTATED (792pt landscape width), not the original 612pt portrait width");
            zoomLandscape.Should().BeLessThan(zoomPortrait - 0.05,
                "the rotated page is wider, so fitting its width must zoom out relative to the portrait fit — " +
                "an equal zoom means Fit ignored the rotation and the landscape page overflows the viewport");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(src);
        }
    }
}
