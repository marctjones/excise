using System.Text;

namespace Excise.Core.Xfa;

/// <summary>
/// SOM names of merged form nodes, the way AcroForm fields name XFA fields (XFA 3.3 p72-74):
/// <c>root[0].Page1[0].f1_01[0]</c>, an optional leading <c>form.</c>, unnamed containers by class
/// (<c>#subform[0]</c>), and <c>scope="none"</c> subforms looked through (p849). Static-XFA
/// write-back (#2013) resolves names with it; the field map (#2027) generates them with the same rules.
/// </summary>
internal static class XfaFormSom
{
    /// <summary>
    /// The full SOM name of every node <see cref="ResolveChain"/> can reach from <paramref name="root"/>,
    /// written so that <c>ResolveChain(root, name)</c> ends at that node: each segment is the node's
    /// name (dots escaped as <c>\.</c>) or <c>#class</c> when unnamed, indexed among the siblings
    /// <see cref="Matches"/> would count. No leading <c>form.</c>.
    /// </summary>
    internal static Dictionary<XfaFormNode, string> Paths(XfaFormNode root, XfaBudget budget)
    {
        var paths = new Dictionary<XfaFormNode, string>(ReferenceEqualityComparer.Instance);
        var rootPath = Segment(root, 0);
        paths[root] = rootPath;
        AddChildren(root, rootPath, paths, budget, depth: 1);
        return paths;
    }

    private static void AddChildren(
        XfaFormNode parent, string parentPath, Dictionary<XfaFormNode, string> paths, XfaBudget budget, int depth)
    {
        XfaBudget.CheckDepth(depth);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in SomChildren(parent))
        {
            budget.Tick();
            var key = Key(child);
            seen.TryGetValue(key, out var index);
            seen[key] = index + 1;
            var path = parentPath + "." + Segment(child, index);
            paths[child] = path;
            if (child.IsContainer)
                AddChildren(child, path, paths, budget, depth + 1);
        }
    }

    /// <summary>What <see cref="Matches"/> compares: the name, or <c>#class</c> for an unnamed node.</summary>
    private static string Key(XfaFormNode node)
        => node.Element.Attr("name") is { Length: > 0 } name ? name : "#" + node.Element.Name.LocalName;

    private static string Segment(XfaFormNode node, int index)
        => Key(node).Replace(".", "\\.", StringComparison.Ordinal) + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    /// <summary>
    /// Resolve an AcroForm full name (<c>root[0].Page1[0].f1_01[0]</c>, an
    /// optional leading <c>form.</c>) to the chain of merged form nodes from
    /// the root to the field. Unnamed containers are addressed by class
    /// (<c>#subform[0]</c>); a subform with <c>scope="none"</c> takes no part
    /// in SOM names (XFA 3.3 p849) and is looked through. Null when any
    /// segment does not resolve.
    /// </summary>
    internal static List<XfaFormNode>? ResolveChain(XfaFormNode root, string fullName)
    {
        var segments = SplitSom(fullName);
        if (segments == null || segments.Count == 0)
            return null;

        int i = 0;
        var rootName = root.Element.Attr("name");
        if (segments[0].Name == "form" && rootName != "form" && segments.Count > 1)
            i = 1;   // "form.root.page.field" (XFA 3.3 p72-73)

        if (segments[i].Index != 0 || !Matches(root, segments[i].Name))
            return null;

        var chain = new List<XfaFormNode> { root };
        var current = root;
        for (i++; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            int seen = 0;
            XfaFormNode? next = null;
            foreach (var child in SomChildren(current))
            {
                if (!Matches(child, name))
                    continue;
                if (seen++ == index)
                {
                    next = child;
                    break;
                }
            }
            if (next == null)
                return null;
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    internal static IEnumerable<XfaFormNode> SomChildren(XfaFormNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == XfaNodeKind.Subform && child.Element.Attr("scope") == "none")
            {
                foreach (var inner in SomChildren(child))
                    yield return inner;
            }
            else
            {
                yield return child;
            }
        }
    }

    internal static bool Matches(XfaFormNode node, string segment)
    {
        var name = node.Element.Attr("name");
        if (segment.StartsWith('#'))
            return string.IsNullOrEmpty(name) && node.Element.Name.LocalName == segment[1..];
        return name == segment;
    }

    /// <summary>Split on unescaped dots; <c>\.</c> is a literal dot in a name.</summary>
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
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out index))
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
