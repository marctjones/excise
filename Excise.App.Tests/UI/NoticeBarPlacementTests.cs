using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using FluentAvalonia.UI.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The toast, XFA and attachments notice bars must open below the toolbar. The window extends
/// its client area into the title bar (ExtendClientAreaToDecorationsHint), so a
/// bar placed above the custom title row is drawn under the macOS window buttons
/// and pushes the title and toolbar down. The #1547 live check found exactly that.
/// </summary>
[Collection("AvaloniaTests")]
public class NoticeBarPlacementTests
{
    [FixedAvaloniaFact]
    public async Task OpeningANoticeBar_FloatsOverTheDocument_AndDoesNotMoveIt()
    {
        // #1646, reported live: "All notification banners should pop over the
        // content, not push the window content down." They used to be a layout
        // row, so every open and close re-laid out the window and moved the
        // page under the reader — #1619 was that exact defect, three seconds
        // after a document had settled.
        //
        // Both halves matter and the second is the one a naive fix misses: a
        // banner that does not move the document but sits ABOVE it has just
        // become a thinner layout row.
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            await Settle(window);
            // The document AREA, not the viewer control: with no document open
            // the viewer collapses to nothing while the area it lives in is
            // what the banner must not displace.
            var documentArea = window.FindControl<Border>("DocumentArea")!;
            var bars = new[]
            {
                window.FindControl<FAInfoBar>("ToastInfoBar")!,
                window.FindControl<FAInfoBar>("XfaFormInfoBar")!,
                window.FindControl<FAInfoBar>("AttachmentsInfoBar")!,
            };

            var documentTopBefore = documentArea.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var documentHeightBefore = documentArea.Bounds.Height;

            foreach (var bar in bars)
            {
                bar.Title = "notice";
                bar.Message = "something worth saying about this document";
                bar.IsOpen = true;
            }
            await Settle(window);

            documentArea.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().Be(documentTopBefore,
                "a banner must not push the document down");
            documentArea.Bounds.Height.Should().Be(documentHeightBefore,
                "nor shrink it — that re-lays out the page just as visibly");

            foreach (var bar in bars)
            {
                var barTop = bar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                barTop.Should().BeGreaterThanOrEqualTo(documentTopBefore,
                    $"{bar.Name} must be OVER the document, not above it");
                barTop.Should().BeLessThan(documentTopBefore + documentHeightBefore,
                    $"{bar.Name} must overlap the document area");
            }

            foreach (var bar in bars)
                bar.IsOpen = false;
            await Settle(window);
            documentArea.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().Be(documentTopBefore,
                "closing them must not move it either");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task NoticeBars_OpenBelowTheToolbar()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            var toolbar = window.FindControl<Border>("ToolbarBorder")!;
            var toast = window.FindControl<FAInfoBar>("ToastInfoBar")!;
            var xfa = window.FindControl<FAInfoBar>("XfaFormInfoBar")!;
            // #1619: the attachments warning is a bar in this row too, not a toast.
            var attachments = window.FindControl<FAInfoBar>("AttachmentsInfoBar")!;
            toast.Title = "toast";
            toast.IsOpen = true;
            xfa.IsOpen = true;
            attachments.IsOpen = true;
            await Settle(window);

            var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var toolbarBottom = toolbarTop + toolbar.Bounds.Height;

            foreach (var bar in new[] { toast, xfa, attachments })
            {
                var top = bar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                top.Should().BeGreaterThanOrEqualTo(toolbarBottom,
                    $"{bar.Name} must not sit in the title-bar area above the toolbar");
            }

            // Opening the bars must not move the toolbar down.
            toast.IsOpen = false;
            xfa.IsOpen = false;
            attachments.IsOpen = false;
            await Settle(window);
            toolbar.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().Be(toolbarTop);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(10);
        }
    }
}
