using System.Collections.Generic;
using Excise.Core.Primitives;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Which image and mask streams each continuous-view page has read, so the
/// viewer can release a page's decoded samples once nothing on screen needs
/// them (#1492). UI thread only.
/// </summary>
/// <remarks>
/// <para><b>Why the viewer needs this.</b> An image XObject's decoded samples
/// live on its stream object for as long as the document is open (#1468). A
/// continuous-view page is drawn as several band renders, so the viewer cannot
/// release after each render the way thumbnails and the CLI do — every band
/// would inflate a large image again. Instead each band render reports the
/// streams it read (<c>RenderOptions.ImageSampleStreamSink</c>), they are
/// merged here per page, and a page's samples are released once the page is
/// no longer kept.</para>
///
/// <para><b>What is never released.</b> A stream any kept page has read — a
/// kept page is realized, has a render in flight, or (for a cache trim) is in
/// the current bands; that set is the caller's. Releasing a shared image
/// anyway would still be byte-safe, only a wasted re-decode. And the stream
/// itself refuses to release bytes that anything but its decoder wrote (a
/// redacted image, an edited or decrypted stream).</para>
///
/// <para><b>Never waits.</b> A decode runs inside the stream's lock and can take
/// seconds on a large image; a release that would have to wait for it is
/// skipped (the samples are in use right now) and tried again on a later
/// release.</para>
/// </remarks>
internal sealed class DecodedImageSampleRetention
{
    private readonly Dictionary<int, HashSet<PdfStream>> _streamsByPage = new();

    /// <summary>True when no page has a recorded stream.</summary>
    public bool IsEmpty => _streamsByPage.Count == 0;

    /// <summary>The streams recorded for <paramref name="page"/> (tests and diagnostics).</summary>
    public IReadOnlyCollection<PdfStream> StreamsOf(int page) =>
        _streamsByPage.TryGetValue(page, out var set) ? set : [];

    /// <summary>Merge the streams one render of <paramref name="page"/> read.</summary>
    public void Record(int page, IEnumerable<PdfStream> streams)
    {
        HashSet<PdfStream>? set = null;
        foreach (var stream in streams)
        {
            if (set == null && !_streamsByPage.TryGetValue(page, out set))
            {
                set = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
                _streamsByPage[page] = set;
            }
            set.Add(stream);
        }
    }

    /// <summary>Forget every record without releasing anything (document change).</summary>
    public void Clear() => _streamsByPage.Clear();

    /// <summary>
    /// The streams to release when only <paramref name="keepPages"/> are kept:
    /// every stream recorded for a page outside it, minus every stream recorded
    /// for a page inside it.
    /// </summary>
    internal static HashSet<PdfStream> SelectReleasable(
        IReadOnlyDictionary<int, HashSet<PdfStream>> streamsByPage,
        IReadOnlySet<int> keepPages)
    {
        var pinned = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var (page, streams) in streamsByPage)
        {
            if (keepPages.Contains(page))
                pinned.UnionWith(streams);
        }

        var releasable = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var (page, streams) in streamsByPage)
        {
            if (keepPages.Contains(page))
                continue;
            foreach (var stream in streams)
            {
                if (!pinned.Contains(stream))
                    releasable.Add(stream);
            }
        }
        return releasable;
    }

    /// <summary>
    /// Release the decoded samples of every page outside
    /// <paramref name="keepPages"/> that no kept page shares, and drop those
    /// pages' records. A stream whose lock is busy stays recorded for a later
    /// attempt; a stream a kept page shares is dropped from the released page's
    /// record, since the kept page's own record now covers it.
    /// </summary>
    /// <returns>How many streams released samples, and how many decoded bytes.</returns>
    /// <param name="evictObject">
    /// F3 for the viewer (#1207/#1461): called with every stream whose samples
    /// were just released, so the caller can also offer the object itself for
    /// eviction and let its ENCODED bytes go. gcdump at the altona-scroll peak
    /// (2026-09-19): ~100 MB of live Byte[] were encoded image bytes of pages
    /// no longer in the keep set, pinned only by PdfDocumentObjectStore's
    /// cache. Whether the object may actually be forgotten is the store's
    /// decision (an edited one may not). Null keeps the old behaviour.
    /// </param>
    public (int Streams, long Bytes) ReleaseAllExcept(IReadOnlySet<int> keepPages, Action<PdfStream>? evictObject = null)
    {
        if (_streamsByPage.Count == 0)
            return default;

        var releasable = SelectReleasable(_streamsByPage, keepPages);
        var busy = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        int released = 0;
        long bytes = 0;
        foreach (var stream in releasable)
        {
            switch (stream.TryReleaseDecodedWithoutWaiting(out long streamBytes))
            {
                case DecodedReleaseOutcome.Released:
                    released++;
                    bytes += streamBytes;
                    // Samples gone and nothing of this render's still reads
                    // them: the object can go too. A later draw of that page
                    // re-resolves it, and in the GUI that is a copy out of the
                    // in-memory file bytes, not a disk read.
                    evictObject?.Invoke(stream);
                    break;
                case DecodedReleaseOutcome.Busy:
                    busy.Add(stream);
                    break;
            }
        }

        List<int>? emptied = null;
        foreach (var (page, streams) in _streamsByPage)
        {
            if (keepPages.Contains(page))
                continue;
            if (busy.Count == 0)
                streams.Clear();
            else
                streams.IntersectWith(busy);
            if (streams.Count == 0)
                (emptied ??= new List<int>()).Add(page);
        }
        if (emptied != null)
        {
            foreach (var page in emptied)
                _streamsByPage.Remove(page);
        }

        return (released, bytes);
    }

    /// <summary>The live record map (tests only).</summary>
    internal IReadOnlyDictionary<int, HashSet<PdfStream>> RecordsForTests => _streamsByPage;
}
