using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #2069: a key in the viewer arms the post-input render-loop hold, repeated input within
/// the resend interval does not commit a message per event, and detaching drops the
/// keep-alive visual (the next touch recreates it).
/// </summary>
public class RenderLoopWarmthTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    [Fact]
    public async Task KeyInViewer_ArmsTheHold_OncePerResendInterval_AndDetachDropsTheVisual()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var window = new Window { Width = 320, Height = 240, Content = viewer };
            window.Show();
            Settle(window);
            var warmth = viewer.RenderLoopWarmthForTests;
            ElementComposition.GetElementChildVisual(viewer).Should().BeNull("fixture: nothing armed before input");

            for (int i = 0; i < 5; i++)
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown, Source = viewer });

            warmth.MessagesSent.Should().Be(1, "five keys inside the resend interval extend one hold");
            ElementComposition.GetElementChildVisual(viewer).Should().BeOfType<CompositionCustomVisual>(
                "the hold is a custom visual on the viewer");

            window.Content = null;
            Settle(window);
            ElementComposition.GetElementChildVisual(viewer).Should().BeNull("detaching drops the keep-alive visual");

            window.Content = viewer;
            Settle(window);
            viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown, Source = viewer });
            warmth.MessagesSent.Should().Be(2, "the first key after re-attaching arms a new hold at once");

            window.Close();
            return true;
        });
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
