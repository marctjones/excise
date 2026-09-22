using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1551 follow-up: every document window's viewer draws on one tile budget
/// (<see cref="PdfViewerTileBudget"/>). The budget arithmetic (a lone viewer
/// behaves as without one, N viewers stay inside ONE budget, background viewers
/// give way first, detaching stops counting a viewer) moved to
/// Excise.Avalonia.Tests/SharedTileBudgetTests (#1773). What stays here needs
/// the App or a really rendered page: a background viewer's band is taken only
/// for the foreground viewer and its composite stays, and a main window gives
/// the shared budget its Preferences tile budget.
/// </summary>
[Collection("AvaloniaTests")]
public class SharedTileBudgetTests
{
    private const int Side = 10;

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task ABackgroundBand_IsTakenOnlyForTheForeground_AndItsCompositeStays()
    {
        var (foreWindow, fore, foreItems) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 1);
        var (backWindow, back, backItems) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 1);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(foreWindow, fore, foreItems, pageNumber: 1);
            var backComposite = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                backWindow, back, backItems, pageNumber: 1);
            // Keep only the bands, so every tile the budget could take is a band tile.
            fore.TrimCaches(PdfViewerCacheTrimLevel.Background);
            back.TrimCaches(PdfViewerCacheTrimLevel.Background);
            var backBand = back.ContinuousRequiredKeysForTests.ToHashSet();
            Keys(back).Should().BeEquivalentTo(backBand, "fixture: the background viewer holds only its band");
            Keys(back).Count.Should().BeGreaterThan(2, "fixture: more band tiles than the LRU minimum");

            var budget = new PdfViewerTileBudget(
                fore.ContinuousTileCacheResidentBytes + back.ContinuousTileCacheResidentBytes);
            fore.SharedTileBudget = budget;
            back.SharedTileBudget = budget;

            // No window focused: a band is never taken, so the budget is exceeded.
            fore.AddToContinuousCache(Key(0), NewTile());
            Keys(back).Should().BeEquivalentTo(backBand, "a background band is only ever taken for the foreground");
            budget.ResidentBytes.Should().BeGreaterThan(budget.ByteBudget);

            budget.Foreground = fore;
            fore.AddToContinuousCache(Key(1), NewTile());
            Keys(back).Count.Should().BeLessThan(backBand.Count, "the focused window's needs come first");
            Dispatcher.UIThread.RunJobs();
            backItems.ItemsSource!.Cast<PdfPageSlot>().Single().Bitmap.Should().BeSameAs(backComposite,
                "a taken band tile was already composited: the background window shows the same pixels");
            Keys(fore).Should().Contain(new[] { Key(0), Key(1) });
        }
        finally
        {
            fore.SharedTileBudget = null;
            back.SharedTileBudget = null;
            foreWindow.Close();
            backWindow.Close();
            Dispatcher.UIThread.RunJobs();
            fore.Document?.Dispose();
            back.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact]
    public void AMainWindow_GivesTheSharedBudgetItsPerformanceTileBudget_AndLeavesItOnNull()
    {
        var budget = new PdfViewerTileBudget(1);
        var window = new MainWindow();
        try
        {
            var (viewer, _) = window.CacheTrimTarget();
            viewer.Should().NotBeNull();
            window.UseSharedTileBudget(budget);
            budget.ByteBudget.Should().Be(PerformanceSettings.Balanced.TileCacheBudgetMb * 1024L * 1024L,
                "a lone window's shared budget is its own Preferences value, so it behaves as before");
            viewer!.SharedTileBudget.Should().BeSameAs(budget);
            budget.ViewerCount.Should().Be(1);

            window.UseSharedTileBudget(null);
            viewer.SharedTileBudget.Should().BeNull();
            budget.ViewerCount.Should().Be(0);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static PdfViewerControl.ContinuousTileKey Key(int col) =>
        new(Page: int.MaxValue, Dpi: 1, PageWidthDip: 1, PageHeightDip: 1, Col: col, Row: 0);

    private static WriteableBitmap NewTile() =>
        new(new PixelSize(Side, Side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static List<PdfViewerControl.ContinuousTileKey> Keys(PdfViewerControl viewer) =>
        viewer.ContinuousCacheEntriesForTests().Select(e => e.Key).ToList();
}
