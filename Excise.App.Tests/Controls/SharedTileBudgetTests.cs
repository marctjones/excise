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
/// (<see cref="PdfViewerTileBudget"/>). Pinned here: a lone viewer behaves
/// exactly as without one, N viewers stay inside ONE budget, background viewers
/// give way first, the foreground viewer never gives way to a background one,
/// and a background viewer's band is taken only for the foreground viewer.
/// </summary>
[Collection("AvaloniaTests")]
public class SharedTileBudgetTests
{
    private const int Side = 10;
    private static readonly long Tile = PdfViewerControl.ContinuousTileByteSize(Side, Side);

    [FixedAvaloniaFact]
    public void ALoneViewer_EvictsExactlyAsWithoutASharedBudget()
    {
        var budget = new PdfViewerTileBudget(5 * Tile);
        var shared = new PdfViewerControl { ContinuousTileCacheByteBudget = 5 * Tile };
        shared.SharedTileBudget = budget;
        var alone = new PdfViewerControl { ContinuousTileCacheByteBudget = 5 * Tile };

        // Plain inserts, re-inserts and render-ahead inserts in a fixed mix.
        for (int i = 0; i < 40; i++)
        {
            var key = Key(i % 9);
            bool lookAhead = i % 4 == 3;
            bool keptShared = shared.AddToContinuousCache(key, NewTile(), lookAhead);
            bool keptAlone = alone.AddToContinuousCache(key, NewTile(), lookAhead);
            keptShared.Should().Be(keptAlone, $"insert {i}");
            Keys(shared).Should().Equal(Keys(alone), $"after insert {i}");
            shared.ContinuousLookAheadTilesForTests.Should().BeEquivalentTo(alone.ContinuousLookAheadTilesForTests);
        }

        // The Preferences path sets both values together.
        shared.ContinuousTileCacheByteBudget = 3 * Tile;
        budget.ByteBudget = 3 * Tile;
        alone.ContinuousTileCacheByteBudget = 3 * Tile;
        Keys(shared).Should().Equal(Keys(alone));
        budget.CrossViewerEvictionCount.Should().Be(0);
        budget.ResidentBytes.Should().Be(shared.ContinuousTileCacheResidentBytes);
    }

    [FixedAvaloniaFact]
    public void TheForegroundViewer_TakesFromTheBackground_RenderAheadFirst_ThenLeastRecentlyUsed()
    {
        var budget = new PdfViewerTileBudget(6 * Tile);
        var (fore, back) = (new PdfViewerControl(), new PdfViewerControl());
        fore.SharedTileBudget = budget;
        back.SharedTileBudget = budget;
        budget.Foreground = fore;

        back.AddToContinuousCache(Key(0), NewTile());
        back.AddToContinuousCache(Key(1), NewTile());
        back.AddToContinuousCache(Key(2), NewTile());
        back.AddToContinuousCache(Key(3), NewTile(), lookAhead: true).Should().BeTrue();
        fore.AddToContinuousCache(Key(10), NewTile());
        fore.AddToContinuousCache(Key(11), NewTile());
        budget.ResidentBytes.Should().Be(6 * Tile, "fixture: exactly at the budget");
        budget.CrossViewerEvictionCount.Should().Be(0);

        fore.AddToContinuousCache(Key(12), NewTile());
        Keys(back).Should().Equal(new[] { Key(2), Key(1), Key(0) }, "the background render-ahead tile goes first");

        fore.AddToContinuousCache(Key(13), NewTile());
        Keys(back).Should().Equal(new[] { Key(2), Key(1) }, "then the background's least recently used tile");

        // The background is down to the LRU minimum: the foreground pays for itself.
        fore.AddToContinuousCache(Key(14), NewTile());
        Keys(back).Should().Equal(new[] { Key(2), Key(1) });
        Keys(fore).Should().Equal(new[] { Key(14), Key(13), Key(12), Key(11) });
        budget.ResidentBytes.Should().Be(6 * Tile, "N viewers hold ONE budget");
        budget.CrossViewerEvictionCount.Should().Be(2);
    }

    [FixedAvaloniaFact]
    public void ABackgroundViewer_NeverTakesFromTheForeground_ButDoesFromOtherBackgroundViewers()
    {
        var budget = new PdfViewerTileBudget(6 * Tile);
        var (fore, back, other) = (new PdfViewerControl(), new PdfViewerControl(), new PdfViewerControl());
        fore.SharedTileBudget = budget;
        back.SharedTileBudget = budget;
        budget.Foreground = fore;
        for (int i = 0; i < 5; i++)
            fore.AddToContinuousCache(Key(10 + i), NewTile());

        for (int i = 0; i < 4; i++)
            back.AddToContinuousCache(Key(i), NewTile());
        Keys(fore).Should().HaveCount(5, "the focused window's tiles never go to a background window");
        Keys(back).Should().Equal(new[] { Key(3), Key(2) }, "the background viewer lives on what is left, down to its LRU minimum");
        budget.CrossViewerEvictionCount.Should().Be(0);

        budget.ResidentBytes.Should().Be(7 * Tile, "fixture: the LRU minimum put the background one tile over");

        // With no window focused, everyone is background and takes from the
        // others. Attaching applies the budget, which takes the tile over.
        budget.Foreground = null;
        other.SharedTileBudget = budget;
        budget.ViewerCount.Should().Be(3);
        budget.CrossViewerEvictionCount.Should().Be(1);
        other.AddToContinuousCache(Key(20), NewTile());
        other.AddToContinuousCache(Key(21), NewTile());
        budget.CrossViewerEvictionCount.Should().Be(3);
        Keys(fore).Should().Equal(new[] { Key(14), Key(13) }, "the viewer that was focused gives way like any other now");
        Keys(back).Should().Equal(new[] { Key(3), Key(2) }, "back is at its LRU minimum");
        Keys(other).Should().Equal(new[] { Key(21), Key(20) });
        budget.ResidentBytes.Should().Be(6 * Tile);
    }

    [FixedAvaloniaFact]
    public void Detaching_StopsCountingTheViewer_AndLoweringTheBudgetEvictsAtOnce()
    {
        var budget = new PdfViewerTileBudget(8 * Tile);
        var (fore, back) = (new PdfViewerControl(), new PdfViewerControl());
        fore.SharedTileBudget = budget;
        back.SharedTileBudget = budget;
        budget.Foreground = fore;
        for (int i = 0; i < 4; i++)
        {
            back.AddToContinuousCache(Key(i), NewTile());
            fore.AddToContinuousCache(Key(10 + i), NewTile());
        }

        budget.ByteBudget = 5 * Tile;
        budget.ResidentBytes.Should().BeLessThanOrEqualTo(5 * Tile, "a lower shared budget applies at once");
        Keys(back).Should().HaveCount(2, "the background gave what it could first");
        Keys(fore).Should().HaveCount(3);

        back.SharedTileBudget = null;
        budget.ViewerCount.Should().Be(1);
        budget.Foreground.Should().BeSameAs(fore);
        budget.ResidentBytes.Should().Be(fore.ContinuousTileCacheResidentBytes);
        fore.AddToContinuousCache(Key(20), NewTile());
        fore.AddToContinuousCache(Key(21), NewTile());
        Keys(fore).Should().HaveCount(5, "alone again, the foreground has the whole budget");
        Keys(back).Should().HaveCount(2, "a detached viewer is not touched");

        back.SharedTileBudget = budget;
        budget.Foreground = new PdfViewerControl();
        budget.Foreground.Should().BeNull("a viewer that is not attached cannot be the foreground");
    }

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
