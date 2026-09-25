using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// The one walk of a name tree (§7.9.6) and, through <see cref="PdfNumberTree"/>, a number
/// tree (§7.9.7): leaves hold a flat key/value array, branches hold /Kids, and /Limits is
/// an index that is ignored. A node reached twice (a /Kids cycle) or deeper than
/// <see cref="MaxDepth"/> is skipped, because recursing into one is an uncatchable
/// stack overflow (#1833).
/// </summary>
internal static class PdfNameTree
{
    private const int MaxDepth = 64;

    /// <summary>
    /// The (key, value) pairs of a name tree in tree order, both unresolved: callers decide
    /// what a non-string key means, and some hand the raw value on. <paramref name="root"/>
    /// may be an unresolved reference or null.
    /// </summary>
    internal static IEnumerable<(PdfObject Key, PdfObject Value)> Enumerate(PdfDocument doc, PdfObject? root) =>
        Pairs(doc, root, "Names");

    internal static IEnumerable<(PdfObject Key, PdfObject Value)> Pairs(PdfDocument doc, PdfObject? root, string leafKey)
    {
        foreach (var node in Nodes(doc, root))
        {
            if (doc.Resolve(node.GetOptional(leafKey) ?? PdfNull.Instance) is not PdfArray pairs) continue;
            for (var i = 0; i + 1 < pairs.Count; i += 2)
                yield return (pairs[i], pairs[i + 1]);
        }
    }

    /// <summary>
    /// Replace the tree under <paramref name="root"/> with one leaf holding
    /// <paramref name="pairs"/>, sorted by key bytes as §7.9.6 requires. A root holds
    /// /Names or /Kids and no /Limits, so the old nodes, and every /Limits that
    /// repeated a key, are no longer reachable and are not saved.
    /// </summary>
    internal static void Rewrite(PdfDocument doc, PdfDictionary root, IEnumerable<(PdfObject Key, PdfObject Value)> pairs)
    {
        var leaf = new PdfArray();
        foreach (var (key, value) in pairs.OrderBy(p => (doc.Resolve(p.Key) as PdfString)?.Bytes ?? [], KeyOrder))
        {
            leaf.Add(key);
            leaf.Add(value);
        }
        root.Remove("Kids");
        root.Remove("Limits");
        root.Set("Names", leaf);
    }

    private static readonly Comparer<byte[]> KeyOrder =
        Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

    /// <summary>Every node of the tree, root first, /Kids in order; for callers that edit a node.</summary>
    internal static IEnumerable<PdfDictionary> Nodes(PdfDocument doc, PdfObject? root)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfObject Node, int Depth)>();
        if (root != null) pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (obj, depth) = pending.Pop();
            if (doc.Resolve(obj) is not PdfDictionary node || depth > MaxDepth || !seen.Add(node)) continue;
            yield return node;
            if (doc.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                for (var i = kids.Count - 1; i >= 0; i--)
                    pending.Push((kids[i], depth + 1));
        }
    }
}
