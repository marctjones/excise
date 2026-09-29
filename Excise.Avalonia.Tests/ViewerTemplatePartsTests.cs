using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1842 Phase B: the viewer's two views are <c>TemplatedControl</c>s whose ControlThemes
/// ship in the viewer's own resources. Pinned here: the views have their parts as soon as
/// the viewer is constructed, with no window and with no theme for them anywhere in the
/// host (this test app loads FluentTheme only, like <c>Excise.App.Tests</c>' TestApp), and
/// the parts the viewer wired at construction are the ones on screen after it is attached,
/// detached and attached again.
/// </summary>
/// <remarks>
/// The first is the trap <see cref="HeadlessTestApp"/> documents: a templated control with no
/// theme in reach silently has no template, and a <c>ScrollViewer</c> in it reports a zero
/// extent. The second is the one assumption the viewer's construction-time wiring (the
/// viewport subscription, the continuous view's scroll and container hooks) rests on.
/// </remarks>
public class ViewerTemplatePartsTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    [Fact]
    public async Task TheViewsHaveTheirParts_WhenTheViewerIsConstructed_WithNoWindowAndNoHostTheme()
    {
        await OnUiThread(() =>
        {
            // The host has no theme for the views: the viewer must bring its own.
            Application.Current!.TryFindResource(typeof(SinglePageView), out _).Should().BeFalse();
            Application.Current!.TryFindResource(typeof(ContinuousPageView), out _).Should().BeFalse();

            var viewer = new PdfViewerControl();

            viewer.TryFindResource(typeof(SinglePageView), out var singleTheme).Should().BeTrue();
            singleTheme.Should().BeOfType<ControlTheme>();
            viewer.TryFindResource(typeof(ContinuousPageView), out var continuousTheme).Should().BeTrue();
            continuousTheme.Should().BeOfType<ControlTheme>();

            var single = viewer.SinglePagePart;
            var continuous = viewer.ContinuousPart;
            TopLevel.GetTopLevel(viewer).Should().BeNull("the viewer is not in a window");

            Control[] singleParts =
            [
                single.PdfScrollViewer, single.ZoomHost, single.ContentGrid, single.PdfImage,
                single.OverlayCanvas, single.AnnotationsLayer, single.SearchHighlightsLayer,
                single.AppliedRedactionsLayer, single.PendingRedactionsLayer, single.TextSelectionLayer,
                single.HiddenTextRevealLayer, single.FormFieldsLayer, single.InteractionLayer,
                single.TypewriterLayer, single.LoadingProgressBar, single.LoadingOverlay,
                single.ErrorOverlay, single.ErrorMessageText,
            ];
            foreach (var part in singleParts)
            {
                part.Should().NotBeNull();
                part.TemplatedParent.Should().BeSameAs(single, $"{part.Name} is a part of the single-page view's template");
            }

            continuous.ContinuousScrollViewer.Should().NotBeNull();
            continuous.ContinuousScrollViewer.TemplatedParent.Should().BeSameAs(continuous);
            continuous.ContinuousItems.Should().NotBeNull();
            continuous.ContinuousItems.TemplatedParent.Should().BeSameAs(continuous);

            // The template's own values arrived with it.
            continuous.ContinuousScrollViewer.IsVisible.Should().BeFalse("the continuous view starts hidden");
            single.LoadingProgressBar.IsVisible.Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task ThePartsWiredAtConstruction_AreTheOnesShown_AcrossDetachAndReattach()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var single = viewer.SinglePagePart.PdfScrollViewer;
            var image = viewer.SinglePagePart.PdfImage;
            var items = viewer.ContinuousPart.ContinuousItems;
            var continuous = viewer.ContinuousPart.ContinuousScrollViewer;

            var first = new Window { Width = 320, Height = 240, Content = viewer };
            first.Show();
            first.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AssertShownIn(first);

            first.Content = null;
            Dispatcher.UIThread.RunJobs();
            var second = new Window { Width = 400, Height = 300, Content = viewer };
            second.Show();
            second.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AssertShownIn(second);

            // The viewport the viewer reports comes from the scroller it subscribed to
            // at construction: a re-templated scroller would leave it reporting nothing.
            viewer.GetVisibleViewportSize().Width.Should().BeApproximately(single.Viewport.Width, 0.5);
            single.Viewport.Width.Should().BeGreaterThan(0);

            first.Close();
            second.Close();
            return true;

            void AssertShownIn(Window window)
            {
                viewer.SinglePagePart.PdfScrollViewer.Should().BeSameAs(single);
                viewer.SinglePagePart.PdfImage.Should().BeSameAs(image);
                viewer.ContinuousPart.ContinuousItems.Should().BeSameAs(items);
                viewer.ContinuousPart.ContinuousScrollViewer.Should().BeSameAs(continuous);
                TopLevel.GetTopLevel(single).Should().BeSameAs(window);
                TopLevel.GetTopLevel(image).Should().BeSameAs(window);
                TopLevel.GetTopLevel(continuous).Should().BeSameAs(window);
            }
        });
    }
}
