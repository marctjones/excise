using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// The continuous-selection letter cache's bound (#1485). It replaced an
/// unbounded dictionary, so what is pinned here is both halves of the trade:
/// the cache stays small, and the pages a live selection holds letters from
/// by reference are never the ones evicted.
/// </summary>
public sealed class BoundedPageCacheTests
{
    [Fact]
    public void Add_PastCapacity_EvictsTheLeastRecentlyUsedPage()
    {
        var cache = new BoundedPageCache<string>(capacity: 3);
        cache.Add(1, "p1", pinnedFirst: 1, pinnedLast: 0);
        cache.Add(2, "p2", pinnedFirst: 1, pinnedLast: 0);
        cache.Add(3, "p3", pinnedFirst: 1, pinnedLast: 0);
        cache.TryGet(1, out _).Should().BeTrue("a read makes page 1 the most recently used");

        cache.Add(4, "p4", pinnedFirst: 1, pinnedLast: 0);

        cache.Count.Should().Be(3);
        cache.Contains(2).Should().BeFalse("page 2 was the least recently used");
        cache.Contains(1).Should().BeTrue();
        cache.Contains(3).Should().BeTrue();
        cache.Contains(4).Should().BeTrue();
    }

    [Fact]
    public void Add_ReadingManyPages_NeverHoldsMoreThanCapacity()
    {
        var cache = new BoundedPageCache<string>(capacity: 4);
        for (int page = 1; page <= 126; page++)
        {
            cache.Add(page, $"p{page}", pinnedFirst: 1, pinnedLast: 0);
            cache.Count.Should().BeLessThanOrEqualTo(4);
        }
    }

    [Fact]
    public void Add_WithPinnedSelectionSpan_KeepsEveryPageInTheSpan()
    {
        // A selection dragged from page 2 to page 7: its anchor and focus are
        // Letter objects from those pages' cached lists, found by reference.
        var cache = new BoundedPageCache<string>(capacity: 2);
        for (int page = 2; page <= 7; page++)
            cache.Add(page, $"p{page}", pinnedFirst: 2, pinnedLast: 7);
        cache.Add(9, "p9", pinnedFirst: 2, pinnedLast: 7);
        cache.Add(10, "p10", pinnedFirst: 2, pinnedLast: 7);

        for (int page = 2; page <= 7; page++)
            cache.Contains(page).Should().BeTrue($"page {page} is inside the live selection span");
        cache.Contains(9).Should().BeFalse("page 9 is outside the span and older than page 10");
        cache.Contains(10).Should().BeTrue("the page just added is about to be used");
    }

    [Fact]
    public void Add_AfterTheSelectionEnds_ShrinksBackToCapacity()
    {
        var cache = new BoundedPageCache<string>(capacity: 2);
        for (int page = 1; page <= 5; page++)
            cache.Add(page, $"p{page}", pinnedFirst: 1, pinnedLast: 5);
        cache.Count.Should().Be(5);

        cache.Add(6, "p6", pinnedFirst: 1, pinnedLast: 0);

        cache.Count.Should().Be(2);
        cache.Contains(6).Should().BeTrue();
        cache.Contains(5).Should().BeTrue("page 5 was the most recently used before page 6");
    }
}
