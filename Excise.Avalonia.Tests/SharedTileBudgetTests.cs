using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1551 follow-up: every document window's viewer draws on one tile budget
/// (<see cref="PdfViewerTileBudget"/>). Pinned here: a lone viewer behaves
/// exactly as without one, N viewers stay inside ONE budget, background viewers
/// give way first, the foreground viewer never gives way to a background one,
/// and detaching a viewer stops counting it.
/// </summary>
/// <remarks>
/// The four budget-arithmetic tests moved here from Excise.App.Tests (#1773):
/// they only insert stub tiles into <see cref="PdfViewerControl"/> caches and
/// read the budget back, so they need neither the App nor a rendered page.
/// Two siblings stay in Excise.App.Tests because they do need them - the band
/// test renders a real continuous page and compares composites, and the
/// main-window test constructs <c>MainWindow</c>. Runs on the shared headless
/// session (no Skia backend); the tiles are stub <c>WriteableBitmap</c>s.
/// </remarks>
public class SharedTileBudgetTests
{
    private const int Side = 10;
    private static readonly long Tile = PdfViewerControl.ContinuousTileByteSize(Side, Side);

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    [Fact]
    public async Task ALoneViewer_EvictsExactlyAsWithoutASharedBudget()
    {
        await OnUiThread(() =>
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
            return true;
        });
    }

    [Fact]
    public async Task TheForegroundViewer_TakesFromTheBackground_RenderAheadFirst_ThenLeastRecentlyUsed()
    {
        await OnUiThread(() =>
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
            return true;
        });
    }

    [Fact]
    public async Task ABackgroundViewer_NeverTakesFromTheForeground_ButDoesFromOtherBackgroundViewers()
    {
        await OnUiThread(() =>
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
            return true;
        });
    }

    [Fact]
    public async Task Detaching_StopsCountingTheViewer_AndLoweringTheBudgetEvictsAtOnce()
    {
        await OnUiThread(() =>
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
            return true;
        });
    }

    private static PdfViewerControl.ContinuousTileKey Key(int col) =>
        new(Page: int.MaxValue, Dpi: 1, PageWidthDip: 1, PageHeightDip: 1, Col: col, Row: 0);

    private static WriteableBitmap NewTile() =>
        new(new PixelSize(Side, Side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static List<PdfViewerControl.ContinuousTileKey> Keys(PdfViewerControl viewer) =>
        viewer.ContinuousCacheEntriesForTests().Select(e => e.Key).ToList();
}
