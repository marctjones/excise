using Pdfe.Core.Document;

namespace Pdfe.Core.Operations;

/// <summary>
/// Splits a single document into multiple output documents by one of
/// several page-grouping policies (#628).
/// </summary>
/// <remarks>
/// Reuses <see cref="PdfDocumentMerger.ClonePagesInto"/> (phases 1-2 of the
/// merge pipeline: reserve, then fill) for each output fragment — so a
/// link between two pages that land in the <em>same</em> fragment resolves
/// correctly, a free correctness improvement over the old single-page
/// clone path. Deliberately does not splice outlines or AcroForm fields
/// (#628's split scope does not ask for it, and a bookmark tree or field
/// list built for the whole source document is not meaningfully
/// splittable per-fragment without inventing behavior nobody asked for).
/// </remarks>
public static class PdfDocumentSplitter
{
    /// <summary>Split into fixed-size chunks of <paramref name="pagesPerChunk"/> pages each; the last chunk may be smaller.</summary>
    public static IReadOnlyList<PdfDocument> SplitEveryNPages(PdfDocument source, int pagesPerChunk)
    {
        if (pagesPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(pagesPerChunk), "Must split into at least 1 page per chunk.");

        var groups = new List<IReadOnlyList<int>>();
        for (int start = 0; start < source.PageCount; start += pagesPerChunk)
        {
            int count = Math.Min(pagesPerChunk, source.PageCount - start);
            groups.Add(Enumerable.Range(start, count).ToList());
        }
        return BuildFragments(source, groups);
    }

    /// <summary>
    /// Split at explicit 0-based page boundaries: each entry in
    /// <paramref name="startIndices"/> begins a new fragment that runs up
    /// to (but not including) the next start index, or the end of the
    /// document for the last one. <c>[0]</c> is implied if the first
    /// entry isn't 0.
    /// </summary>
    public static IReadOnlyList<PdfDocument> SplitAtPageBoundaries(PdfDocument source, IReadOnlyList<int> startIndices)
    {
        if (startIndices == null || startIndices.Count == 0)
            throw new ArgumentException("At least one boundary is required.", nameof(startIndices));

        var boundaries = startIndices.Distinct().Where(i => i >= 0 && i < source.PageCount).OrderBy(i => i).ToList();
        if (boundaries.Count == 0 || boundaries[0] != 0)
            boundaries.Insert(0, 0);

        var groups = new List<IReadOnlyList<int>>();
        for (int i = 0; i < boundaries.Count; i++)
        {
            int start = boundaries[i];
            int end = i + 1 < boundaries.Count ? boundaries[i + 1] : source.PageCount;
            groups.Add(Enumerable.Range(start, end - start).ToList());
        }
        return BuildFragments(source, groups);
    }

    /// <summary>Burst into one single-page document per page, in order.</summary>
    public static IReadOnlyList<PdfDocument> SplitToSinglePages(PdfDocument source)
    {
        var groups = Enumerable.Range(0, source.PageCount)
            .Select(i => (IReadOnlyList<int>)new List<int> { i })
            .ToList();
        return BuildFragments(source, groups);
    }

    /// <summary>
    /// Split at each root-level outline (bookmark) entry: every top-level
    /// bookmark's destination page starts a new fragment. Pages before the
    /// first bookmark (if any) form a leading fragment. Falls back to a
    /// single fragment containing the whole document if the source has no
    /// resolvable root-level outline destinations.
    /// </summary>
    public static IReadOnlyList<PdfDocument> SplitAtBookmarks(PdfDocument source)
    {
        var rootItems = PdfOutlineParser.Parse(source);
        var boundaries = rootItems
            .Where(item => item.PageNumber is > 0)
            .Select(item => item.PageNumber!.Value - 1)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (boundaries.Count == 0)
            return BuildFragments(source, [Enumerable.Range(0, source.PageCount).ToList()]);

        return SplitAtPageBoundaries(source, boundaries);
    }

    private static IReadOnlyList<PdfDocument> BuildFragments(PdfDocument source, IReadOnlyList<IReadOnlyList<int>> pageGroups)
    {
        var fragments = new List<PdfDocument>();
        try
        {
            foreach (var indices in pageGroups)
            {
                if (indices.Count == 0) continue;
                var target = PdfDocument.CreateNew();
                PdfDocumentMerger.ClonePagesInto(target, [(source, indices)]);
                fragments.Add(target);
            }
        }
        catch
        {
            foreach (var fragment in fragments) fragment.Dispose();
            throw;
        }
        return fragments;
    }
}
