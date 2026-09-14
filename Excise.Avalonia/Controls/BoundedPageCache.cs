namespace Excise.Avalonia.Controls;

/// <summary>
/// A per-page cache that keeps at most a fixed number of pages, evicting the
/// least recently used — except pages inside a pinned span (#1485).
/// </summary>
/// <remarks>
/// <para>Continuous-view text selection caches each page's letters, reading
/// order and column gap. That cache used to be an unbounded dictionary, so a
/// reader who dragged a selection over a long document ended up holding every
/// page's letters, the same whole-document retention the text index had.</para>
/// <para><b>Why a pinned span.</b> The selection's anchor and focus are
/// <see cref="Excise.Core.Text.Letter"/> objects, and the selection engine
/// finds them in a page's reading order BY REFERENCE. If a page inside the
/// anchor→focus span were evicted and re-extracted, its new letters would be
/// equal but not the same objects, and the highlight and copied text would
/// silently lose their endpoints. So the span the user has selected stays
/// cached whatever its length — it is bounded by the selection — and only
/// pages outside it are evicted. The page just added is never the victim,
/// because its caller is about to use it.</para>
/// <para>UI-thread only, like the selection gestures that use it.</para>
/// </remarks>
internal sealed class BoundedPageCache<T> where T : class
{
    private readonly int _capacity;
    private readonly Dictionary<int, T> _entries = new();
    // Page numbers, least recently used first.
    private readonly List<int> _recency = new();

    internal BoundedPageCache(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal int Count => _entries.Count;

    internal bool Contains(int pageNumber) => _entries.ContainsKey(pageNumber);

    internal bool TryGet(int pageNumber, out T value)
    {
        if (!_entries.TryGetValue(pageNumber, out value!))
            return false;
        Touch(pageNumber);
        return true;
    }

    /// <summary>
    /// Store <paramref name="value"/> for <paramref name="pageNumber"/> and
    /// evict least recently used pages over the bound, never evicting
    /// <paramref name="pageNumber"/> itself or a page in
    /// [<paramref name="pinnedFirst"/>, <paramref name="pinnedLast"/>]. Pass
    /// an empty span (first &gt; last) when nothing is pinned.
    /// </summary>
    internal void Add(int pageNumber, T value, int pinnedFirst, int pinnedLast)
    {
        _entries[pageNumber] = value;
        Touch(pageNumber);

        var i = 0;
        while (_entries.Count > _capacity && i < _recency.Count)
        {
            var candidate = _recency[i];
            if (candidate == pageNumber || (candidate >= pinnedFirst && candidate <= pinnedLast))
            {
                i++;
                continue;
            }
            _entries.Remove(candidate);
            _recency.RemoveAt(i);
        }
    }

    internal void Clear()
    {
        _entries.Clear();
        _recency.Clear();
    }

    private void Touch(int pageNumber)
    {
        var last = _recency.Count - 1;
        if (last >= 0 && _recency[last] == pageNumber)
            return;
        _recency.Remove(pageNumber);
        _recency.Add(pageNumber);
    }
}
