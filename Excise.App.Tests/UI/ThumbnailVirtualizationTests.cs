using System.Reactive.Linq;
using AwesomeAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>Large-document sidebar construction and recycling (#1911).</summary>
[Collection("AvaloniaTests")]
public class ThumbnailVirtualizationTests
{
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TwentyThousandThumbnails_RealizesOnlyTheViewport_AndRebindsWhenSwitchingDocuments()
    {
        var first = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        for (var index = 0; index < 20_000; index++)
            first.PageThumbnails.Add(new PageThumbnail { PageNumber = index + 1, PageIndex = index });
        var last = first.PageThumbnails[^1];
        last.IsMarkedForPageOperation = true;

        var second = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        second.PageThumbnails.Add(new PageThumbnail { PageNumber = 1, PageIndex = 0 });
        var window = new MainWindow { DataContext = first, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await SettleAsync(window);
            var items = window.FindControl<ItemsControl>("ThumbnailsItemsControl")!;
            AssertBoundedContainers(items);
            items.GetRealizedContainers().Should().Contain(c => ReferenceEquals(c.DataContext, first.PageThumbnails[0]));

            var scroll = items.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await SettleAsync(window);
            AssertBoundedContainers(items);
            var lastContainer = items.GetRealizedContainers().Single(c => ReferenceEquals(c.DataContext, last));
            var checkbox = lastContainer.GetVisualDescendants().OfType<CheckBox>().Single();
            checkbox.IsChecked.Should().BeTrue("recycling must bind the final page's own selection");
            checkbox.IsChecked = false;
            last.IsMarkedForPageOperation.Should().BeFalse();

            window.DataContext = second;
            await SettleAsync(window);
            items.GetRealizedContainers().Should().ContainSingle()
                .Which.DataContext.Should().BeSameAs(second.PageThumbnails[0]);

            window.DataContext = first;
            await SettleAsync(window);
            AssertBoundedContainers(items);
            items.GetRealizedContainers().Should().OnlyContain(c => first.PageThumbnails.Contains((PageThumbnail)c.DataContext!));
        }
        finally
        {
            window.Close();
            first.PageThumbnails.Clear();
            second.PageThumbnails.Clear();
        }
    }

    private static void AssertBoundedContainers(ItemsControl items)
    {
        items.GetRealizedContainers().Should().NotBeEmpty()
            .And.HaveCountLessThan(30,
                "a 900 DIP sidebar should construct only a viewport of thumbnails, not 20,000 controls (#1911)");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ScrollingPastRecycledThumbnails_EvictsTheirImages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-thumbnail-virtualization-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 90);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            await SettleAsync(window);
            await vm.EnsureThumbnailLoadedAsync(0);
            vm.PageThumbnails[0].ThumbnailImage.Should().NotBeNull();

            var items = window.FindControl<ItemsControl>("ThumbnailsItemsControl")!;
            var scroll = items.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            await SettleAsync(window);
            if (vm.ThumbnailPrefetchTask is { } prefetch)
                await prefetch.WaitAsync(TimeSpan.FromSeconds(30));
            await SettleAsync(window);

            vm.PageThumbnails[^1].ThumbnailImage.Should().NotBeNull("the newly realized viewport must load thumbnails");
            vm.PageThumbnails[0].ThumbnailImage.Should().BeNull(
                "a recycled container must leave the visible window, so distant images remain evictable (#1911)");
        }
        finally
        {
            window.Close();
            await vm.CloseDocumentCommand.Execute();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static async Task SettleAsync(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }
}
