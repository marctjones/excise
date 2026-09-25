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
