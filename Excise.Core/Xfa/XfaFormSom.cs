using System.Globalization;
using System.Text;

namespace Excise.Core.Xfa;

/// <summary>
/// SOM names of merged form nodes, as AcroForm fields name XFA fields (XFA 3.3 p72-74; ISO 32000-2
/// Annex K.2). The naming rule (#2035, decided from the specification):
/// <list type="bullet">
/// <item>A named object is written in the normal syntax, <c>name[i]</c>, with <c>i</c> its index among
/// the same-named objects its SOM parent sees (p73-74).</item>
/// <item>Transparent objects are not written: a nameless subform or exclusion group (p95), an area even
/// when named (p95), a subform with <c>scope="none"</c> (p849). Their children are counted as children
/// of the nearest non-transparent ancestor (p95: "as though the nameless subform was removed").</item>
/// <item>An object the normal syntax cannot reach is written by class, <c>#field[n]</c>, with <c>n</c>
/// its index among ALL its true siblings of that class, named or not (p96 note, p119-120); its
/// transparent ancestors up to the nearest written one are then written by class too. That covers a
/// nameless field (HSBC has 54) and a name containing a dot, which XML allows (p75) but a PDF partial
/// name does not (ISO 32000-2 §12.7.4.2; p73 waives that only for global pointer fields).</item>
/// <item>No leading <c>form.</c>: the resolver accepts it (p72-73) and Designer omits it.</item>
/// </list>
/// Each segment is one partial name, so a hierarchical AcroForm field tree whose full names are these
/// paths is valid PDF. Static-XFA write-back (#2013) resolves names with <see cref="ResolveChainLenient"/>,
/// the field map (#2027) and the generated widgets (#2028) are named by <see cref="Paths"/>.
/// </summary>
internal static class XfaFormSom
{
    /// <summary>
    /// The full SOM name of every node under <paramref name="root"/>. With
    /// <paramref name="rootIsEntered"/> the root is always a written segment (page areas, whose
    /// content the field map rebases under a page-set prefix); otherwise a transparent root is not
    /// written (p109: <c>$form.Receipt.Tax</c> under a nameless root) and maps to "".
    /// </summary>
    internal static Dictionary<XfaFormNode, string> Paths(XfaFormNode root, XfaBudget budget, bool rootIsEntered = false)
    {
        var paths = new Dictionary<XfaFormNode, string>(ReferenceEqualityComparer.Instance);
        var classRoot = ClassSegment(root, 0);
        if (!rootIsEntered && IsTransparent(root))
        {
            paths[root] = string.Empty;
            Visit(root, string.Empty, classRoot, paths, budget, depth: 1);
        }
        else
        {
            var rootPath = IsNameWritable(root) ? NamedSegment(root.Element.Attr("name")!, 0) : classRoot;
            paths[root] = rootPath;
            Visit(root, rootPath, rootPath, paths, budget, depth: 1);
        }
        return paths;
    }

    /// <summary>An entered (written) node: name its SOM children, then walk its physical subtree.</summary>
    private static void Visit(
        XfaFormNode entered, string enteredPath, string explicitPath,
        Dictionary<XfaFormNode, string> paths, XfaBudget budget, int depth)
    {
        XfaBudget.CheckDepth(depth);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (child, _) in SomChildren(entered, depth))
        {
            budget.Tick();
            if (!IsNameWritable(child))
                continue;
            var name = child.Element.Attr("name")!;
            seen.TryGetValue(name, out var index);
            seen[name] = index + 1;
            paths[child] = Join(enteredPath, NamedSegment(name, index));
        }
        AssignPhysical(entered, explicitPath, paths, budget, depth);
    }

    private static void AssignPhysical(
        XfaFormNode parent, string explicitParent, Dictionary<XfaFormNode, string> paths, XfaBudget budget, int depth)
    {
        XfaBudget.CheckDepth(depth);
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in parent.Children)
        {
            budget.Tick();
            var cls = child.Element.Name.LocalName;
            classes.TryGetValue(cls, out var index);
            classes[cls] = index + 1;
            var explicitChild = Join(explicitParent, ClassSegment(child, index));
            if (!IsNameWritable(child))
                paths[child] = explicitChild;
            if (!child.IsContainer)
                continue;
            if (IsTransparent(child))
                AssignPhysical(child, explicitChild, paths, budget, depth + 1);
            else
                Visit(child, paths[child], IsNameWritable(child) ? paths[child] : explicitChild, paths, budget, depth + 1);
        }
    }

    /// <summary>
    /// Resolve a full name written by excise (<see cref="Paths"/>) or by any producer that follows the
    /// specification: normal-syntax segments looked up through transparent objects (p95, p849),
    /// <c>#class[n]</c> segments among all true siblings of that class (p96), an optional leading
    /// <c>form.</c> (p72-73). Returns the physical chain from the root to the object, transparent
    /// containers included; null when a segment does not resolve.
    /// </summary>
    internal static List<XfaFormNode>? ResolveChain(XfaFormNode root, string fullName)
        => Resolve(root, fullName, legacyClassIndex: false);

    /// <summary>
    /// <see cref="ResolveChain"/> for names another producer wrote (static forms, #2013). Before #2035
    /// excise counted a <c>#subform[n]</c> index among the NAMELESS siblings only; whether Designer does
    /// is unverified (no corpus file has a <c>#</c> segment). A name both readings resolve to the same
    /// object, or that only one reading resolves, is accepted; a name the two readings resolve to
    /// different objects is refused (<paramref name="ambiguous"/>), so a value is never written to the
    /// wrong field.
    /// </summary>
    internal static List<XfaFormNode>? ResolveChainLenient(XfaFormNode root, string fullName, out bool ambiguous)
    {
        ambiguous = false;
        var spec = Resolve(root, fullName, legacyClassIndex: false);
        var legacy = Resolve(root, fullName, legacyClassIndex: true);
        if (spec != null && legacy != null && !ReferenceEquals(spec[^1], legacy[^1]))
        {
            ambiguous = true;
            return null;
        }
        return spec ?? legacy;
    }

    private static List<XfaFormNode>? Resolve(XfaFormNode root, string fullName, bool legacyClassIndex)
    {
        var segments = SplitSom(fullName);
        if (segments == null || segments.Count == 0)
            return null;

        int i = 0;
        if (segments[0].Name == "form" && root.Element.Attr("name") != "form" && segments.Count > 1)
            i = 1;   // "form.root.page.field" (XFA 3.3 p72-73)

        // The root's own segment, unless the root is transparent and the name starts below it.
        var chain = new List<XfaFormNode> { root };
        var (firstName, firstIndex) = segments[i];
        bool rootMatches = firstIndex == 0 && (firstName.StartsWith('#')
            ? root.Element.Name.LocalName == firstName[1..] && (!legacyClassIndex || string.IsNullOrEmpty(root.Element.Attr("name")))
            : !IsTransparent(root) && root.Element.Attr("name") == firstName);
        if (rootMatches)
            i++;
        else if (!IsTransparent(root) || firstName.StartsWith('#'))
            return null;

        var current = root;
        for (; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            var step = name.StartsWith('#')
                ? ByClass(current, name[1..], index, legacyClassIndex)
                : ByName(current, name, index, legacyClassIndex);
            if (step == null)
                return null;
            chain.AddRange(step);
            current = chain[^1];
        }
        return chain;
    }

    /// <summary>The <paramref name="index"/>-th child of class <paramref name="cls"/> (p96), as a one-step chain.</summary>
    private static List<XfaFormNode>? ByClass(XfaFormNode parent, string cls, int index, bool legacy)
    {
        if (legacy)
        {
            // Before #2035: nameless objects of the class only, looking through scope="none".
            int n = 0;
            foreach (var (child, via) in LegacySomChildren(parent))
            {
                if (child.Element.Name.LocalName == cls && string.IsNullOrEmpty(child.Element.Attr("name")) && n++ == index)
                    return new List<XfaFormNode>(via) { child };
            }
            return null;
        }

        int seen = 0;
        foreach (var child in parent.Children)
        {
            if (child.Element.Name.LocalName == cls && seen++ == index)
                return new List<XfaFormNode> { child };
        }
        return null;
    }

    /// <summary>The <paramref name="index"/>-th same-named SOM child, with the transparent containers on the way.</summary>
    private static List<XfaFormNode>? ByName(XfaFormNode parent, string name, int index, bool legacy)
    {
        int seen = 0;
        var children = legacy ? LegacySomChildren(parent) : SomChildren(parent, depth: 1);
        foreach (var (child, via) in children)
        {
            if (child.Element.Attr("name") == name && seen++ == index)
                return new List<XfaFormNode>(via) { child };
        }
        return null;
    }

    /// <summary>
    /// The children SOM sees under <paramref name="node"/>: transparent containers are replaced by their
    /// own SOM children (p95, p849). Each comes with the transparent containers it was reached through.
    /// </summary>
    internal static IEnumerable<(XfaFormNode Node, IReadOnlyList<XfaFormNode> Via)> SomChildren(XfaFormNode node, int depth)
    {
        XfaBudget.CheckDepth(depth);
        foreach (var child in node.Children)
        {
            if (child.IsContainer && IsTransparent(child))
            {
                foreach (var (inner, via) in SomChildren(child, depth + 1))
                    yield return (inner, Prepend(child, via));
            }
            else
            {
                yield return (child, Array.Empty<XfaFormNode>());
            }
        }
    }

    private static IEnumerable<(XfaFormNode Node, IReadOnlyList<XfaFormNode> Via)> LegacySomChildren(XfaFormNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == XfaNodeKind.Subform && child.Element.Attr("scope") == "none")
            {
                foreach (var (inner, via) in LegacySomChildren(child))
                    yield return (inner, Prepend(child, via));
            }
            else
            {
                yield return (child, Array.Empty<XfaFormNode>());
            }
        }
    }

    private static IReadOnlyList<XfaFormNode> Prepend(XfaFormNode first, IReadOnlyList<XfaFormNode> rest)
    {
        var list = new List<XfaFormNode>(rest.Count + 1) { first };
        list.AddRange(rest);
        return list;
    }

    /// <summary>
    /// Transparent to the normal SOM syntax: a nameless subform or exclusion group (p95), any area
    /// (p95), a subform with <c>scope="none"</c> (p849).
    /// </summary>
    internal static bool IsTransparent(XfaFormNode node) => node.Kind switch
    {
        XfaNodeKind.Area => true,
        XfaNodeKind.Subform => string.IsNullOrEmpty(node.Element.Attr("name")) || node.Element.Attr("scope") == "none",
        XfaNodeKind.ExclGroup => string.IsNullOrEmpty(node.Element.Attr("name")),
        _ => false,
    };

    /// <summary>The normal syntax can write this object: named, not transparent, no dot in the name.</summary>
    private static bool IsNameWritable(XfaFormNode node)
        => !IsTransparent(node)
           && node.Element.Attr("name") is { Length: > 0 } name
           && !name.Contains('.', StringComparison.Ordinal);

    private static string NamedSegment(string name, int index)
        => name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    private static string ClassSegment(XfaFormNode node, int index)
        => "#" + node.Element.Name.LocalName + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    private static string Join(string parent, string segment) => parent.Length == 0 ? segment : parent + "." + segment;

    /// <summary>Split on unescaped dots; <c>\.</c> is a literal dot in a name (accepted, never generated).</summary>
    private static List<(string Name, int Index)>? SplitSom(string fullName)
    {
        var result = new List<(string, int)>();
        var current = new StringBuilder();
        for (int i = 0; i <= fullName.Length; i++)
        {
            if (i < fullName.Length && fullName[i] == '\\' && i + 1 < fullName.Length && fullName[i + 1] == '.')
            {
                current.Append('.');
                i++;
                continue;
            }
            if (i < fullName.Length && fullName[i] != '.')
            {
                current.Append(fullName[i]);
                continue;
            }

            var segment = current.ToString();
            current.Clear();
            int index = 0;
            int open = segment.LastIndexOf('[');
            if (open >= 0 && segment.EndsWith(']'))
            {
                if (!int.TryParse(segment.AsSpan(open + 1, segment.Length - open - 2),
                        NumberStyles.None, CultureInfo.InvariantCulture, out index))
                {
                    return null;
                }
                segment = segment[..open];
            }
            if (segment.Length == 0)
                return null;
            result.Add((segment, index));
        }
        return result;
    }
}
