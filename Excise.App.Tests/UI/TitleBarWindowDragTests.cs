using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The custom title row has to behave like a title bar (#1643).
///
/// <para>Reported live: "Why can't I click on the title bar of the window and
/// drag it to the side to rearrange the window or have it snap to the right
/// side of the screen like any other macOS application?" The window extends its
/// client area into the decorations, and Avalonia does not make that area a
/// title bar on its own — the window has to call <c>BeginMoveDrag</c>. Nothing
/// did, so the row was inert: no move, no snap, no double-click zoom.</para>
///
/// <para>What can be asserted here is the decision, not the outcome:
/// <c>BeginMoveDrag</c> hands the gesture to the window manager, and no
/// headless test can observe what the OS then does with it. So the hit rule is
/// a pure function and the zoom is asserted on the window. That the gesture
/// reaches the OS at all is a live check — step 1 of #1630.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class TitleBarWindowDragTests
{
    [Theory]
    // On macOS the system window buttons own the left inset — the same 86 px
    // the app label is pushed past — and a press there must not start a drag.
    [InlineData(0, true, false)]
    [InlineData(85, true, false)]
    [InlineData(86, true, true)]
    [InlineData(400, true, true)]
    // Elsewhere the whole row is draggable: the buttons are on the right and
    // are separate controls that take their own presses.
    [InlineData(0, false, true)]
    [InlineData(400, false, true)]
    public void TheDragRegion_ExcludesTheSystemWindowButtons(double x, bool isMacOS, bool expected)
    {
        MainWindow.IsWindowDragPoint(x, isMacOS).Should().Be(expected);
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task DoubleClickingTheTitleBar_ZoomsTheWindow()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await Settle(window);
            var titleBar = window.FindControl<Grid>("TitleBarArea")!;
            titleBar.Bounds.Height.Should().BeGreaterThan(0, "the title row must be laid out to be clickable");

            // Right of the macOS window-button inset, so the press is a drag
            // point on every platform.
            var point = titleBar.TranslatePoint(new Point(400, titleBar.Bounds.Height / 2), window)!.Value;

            window.WindowState.Should().Be(WindowState.Normal);
            DoubleClick(titleBar, window, point);
            await Settle(window);
            window.WindowState.Should().Be(WindowState.Maximized,
                "double-clicking a title bar zooms the window, on every platform excise ships to");

            DoubleClick(titleBar, window, point);
            await Settle(window);
            window.WindowState.Should().Be(WindowState.Normal, "and un-zooms it");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A left press with ClickCount 2, raised on the title row. The headless
    /// MouseDown helper has no click-count parameter, so the event is built
    /// directly — a second single click would not be a double click.
    /// </summary>
    private static void DoubleClick(Control target, Window window, Point point)
    {
        var args = new PointerPressedEventArgs(
            target,
            new Pointer(0, PointerType.Mouse, isPrimary: true),
            window,
            point,
            timestamp: 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            clickCount: 2);
        target.RaiseEvent(args);
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            await Task.Delay(20);
        }
    }
}
