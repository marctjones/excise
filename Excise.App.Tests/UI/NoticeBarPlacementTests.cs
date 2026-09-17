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
/// The toast and XFA notice bars must open below the toolbar. The window extends
/// its client area into the title bar (ExtendClientAreaToDecorationsHint), so a
/// bar placed above the custom title row is drawn under the macOS window buttons
/// and pushes the title and toolbar down. The #1547 live check found exactly that.
/// </summary>
[Collection("AvaloniaTests")]
public class NoticeBarPlacementTests
{
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
            toast.Title = "toast";
            toast.IsOpen = true;
            xfa.IsOpen = true;
            await Settle(window);

            var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var toolbarBottom = toolbarTop + toolbar.Bounds.Height;

            foreach (var bar in new[] { toast, xfa })
            {
                var top = bar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                top.Should().BeGreaterThanOrEqualTo(toolbarBottom,
                    $"{bar.Name} must not sit in the title-bar area above the toolbar");
            }

            // Opening the bars must not move the toolbar down.
            toast.IsOpen = false;
            xfa.IsOpen = false;
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
