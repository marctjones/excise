using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1462: excise idled at 5-12% CPU because a HIDDEN indeterminate ProgressBar
/// kept animating. The theme's indeterminate animation targets the indicator's
/// TranslateTransform, and Avalonia 12 only pauses an animation when its target
/// is a Visual (AnimationInstance.Subscribed) — a Transform is not. So once the
/// viewer's loading bar had been shown for the first page render, hiding it left
/// the animation pulsing the global clock every frame: one composition commit
/// per frame, and the render loop and macOS display link never slept.
///
/// Measured on the live app (dotnet-trace, 2026-09-11):
/// MediaContextClock.Pulse → AnimationInstance&lt;double&gt;.Step →
/// TransformGroup change → RequestCompositionBatchCommitAsync, every frame.
///
/// The check reads Avalonia's global animation clock: while any animation is
/// subscribed, MediaContext keeps scheduling renders. It relies on Avalonia 12
/// internals (MediaContext._clock.HasSubscriptions); if an Avalonia upgrade
/// renames them, fix the reflection — do not delete the test.
/// </summary>
[Collection("AvaloniaTests")]
public class IdleAnimationQuiescenceTests
{
    [FixedAvaloniaFact(Timeout = 60_000)]
    public async Task ViewerLoadingBar_StopsAnimating_OnceThePageHasRendered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-idle-anim-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);
        var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));

        var viewer = new PdfViewerControl { CurrentPage = 1 };
        var window = new Window
        {
            Content = viewer,
            Width = 612,
            Height = 792,
            WindowDecorations = WindowDecorations.None
        };

        try
        {
            window.Show();
            await PumpAsync(window);

            AnimationClockHasSubscriptions().Should().BeFalse(
                "precondition: no animation may already be running in the shared test host, " +
                "or this test cannot tell the loading bar's animation apart from someone else's");

            var loadingBar = viewer.FindControl<ProgressBar>("LoadingProgressBar");
            loadingBar.Should().NotBeNull("PdfViewerControl must expose LoadingProgressBar");

            // Setting the document starts the first page render synchronously up to
            // its background await, so IsLoading is already true here.
            viewer.Document = doc;
            window.UpdateLayout();

            viewer.IsLoading.Should().BeTrue("the first page render must be in flight for this test to mean anything");
            loadingBar!.IsVisible.Should().BeTrue();
            AnimationClockHasSubscriptions().Should().BeTrue(
                "while loading, the indeterminate bar animates — if the clock shows nothing here, the " +
                "test cannot see the animation and a green result below would be vacuous");

            var deadline = Stopwatch.StartNew();
            while (viewer.IsLoading)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("the first page render did not finish within 30 s");
                await PumpAsync(window);
            }
            await PumpAsync(window);

            // Finite transitions legitimately subscribe for a moment after load (e.g. the
            // scroll bars appearing as the extent grows). The headless render timer only
            // advances when forced, so tick it and give them a bounded time to finish. A
            // runaway indeterminate animation never unsubscribes, so this cannot mask it.
            var settle = Stopwatch.StartNew();
            while (AnimationClockHasSubscriptions() && settle.Elapsed < TimeSpan.FromSeconds(3))
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(10);
                await PumpAsync(window);
            }

            loadingBar.IsVisible.Should().BeFalse("the loading bar hides once the page has rendered");
            // The mechanism first, then the implementation detail that fixes it.
            AnimationClockHasSubscriptions().Should().BeFalse(
                "an idle viewer must leave no animation subscribed to the global clock — a subscribed " +
                "animation schedules a render every frame and the app never reaches 0% CPU (#1462)");
            loadingBar.IsIndeterminate.Should().BeFalse(
                "a hidden indeterminate bar keeps animating; IsIndeterminate must follow IsLoading (#1462)");
        }
        finally
        {
            window.Close();
            doc.Dispose();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static async Task PumpAsync(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static bool AnimationClockHasSubscriptions()
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
        var mediaContextType = typeof(Visual).Assembly.GetType("Avalonia.Media.MediaContext", throwOnError: true)!;
        var instance = mediaContextType.GetProperty("Instance", any | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Avalonia.Media.MediaContext.Instance not found (Avalonia internals changed?)");
        var clock = mediaContextType.GetField("_clock", any | BindingFlags.Instance)?.GetValue(instance)
            ?? throw new InvalidOperationException("MediaContext._clock not found (Avalonia internals changed?)");
        var hasSubscriptions = clock.GetType().GetProperty("HasSubscriptions", any | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MediaContextClock.HasSubscriptions not found (Avalonia internals changed?)");
        return (bool)hasSubscriptions.GetValue(clock)!;
    }
}
