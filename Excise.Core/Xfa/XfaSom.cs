using System.Globalization;
using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>
/// A small subset of XFA SOM expressions for <c>bind match="dataRef"</c> and
/// <c>bindItems</c>: <c>$</c> (current data scope), <c>$record</c> (the root
/// data group), <c>$data</c> (its parent), <c>!</c> or <c>xfa.datasets.</c>
/// (the datasets packet), dotted names, <c>..</c> for descendants, and
/// <c>[n]</c> / <c>[*]</c> indexes.
/// </summary>
/// <remarks>
/// Anything else (predicates, <c>$template</c>, script calls) resolves to
/// nothing, so the field shows its template value.
/// </remarks>
internal static class XfaSom
{
    /// <param name="datasets">
    /// The <c>&lt;xfa:datasets&gt;</c> element that <c>!</c> and <c>xfa.datasets</c> start from; when
    /// null, the grandparent of <paramref name="dataRoot"/>.
    /// </param>
    public static List<XElement> Evaluate(
        string? expression, XElement? scope, XElement? dataRoot, XfaBudget budget, XElement? datasets = null)
    {
        var empty = new List<XElement>();
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 1024)
            return empty;

        var expr = expression.Trim();
        List<XElement> current;
        string rest;

        // "!" is the short form of "xfa.datasets." (XFA 3.3 p125); IRCC's bindItems use the long one.
        const string datasetsPrefix = "xfa.datasets.";
        if (expr.StartsWith(datasetsPrefix, StringComparison.Ordinal))
            expr = "!" + expr[datasetsPrefix.Length..];

        if (expr.StartsWith("$record", StringComparison.Ordinal))
        {
            if (dataRoot == null)
                return empty;
            current = new List<XElement> { dataRoot };
            rest = expr["$record".Length..];
        }
        else if (expr.StartsWith("$data", StringComparison.Ordinal))
        {
            if (dataRoot?.Parent is not { } data)
                return empty;
            current = new List<XElement> { data };
            rest = expr["$data".Length..];
        }
        else if (expr.StartsWith('$'))
        {
            if (expr.Length > 1 && expr[1] != '.')
                return empty;
            if (scope == null)
                return empty;
            current = new List<XElement> { scope };
            rest = expr[1..];
        }
        else if (expr.StartsWith('!'))
        {
            if ((datasets ?? dataRoot?.Parent?.Parent) is not { } root)
                return empty;
            current = new List<XElement> { root };
            rest = "." + expr[1..];
        }
        else
        {
            if (scope == null)
                return empty;
            current = new List<XElement> { scope };
            rest = "." + expr;
        }

        int pos = 0;
        while (pos < rest.Length)
        {
            budget.Tick();
            bool descendant;
            if (string.CompareOrdinal(rest, pos, "..", 0, 2) == 0)
            {
                descendant = true;
                pos += 2;
            }
            else if (rest[pos] == '.')
            {
                descendant = false;
                pos += 1;
            }
            else
            {
                return empty;
            }

            int nameStart = pos;
            while (pos < rest.Length && rest[pos] != '.' && rest[pos] != '[')
                pos++;
            var name = rest[nameStart..pos].TrimStart('#');
            if (name.Length == 0)
                return empty;

            string index = "0";
            if (pos < rest.Length && rest[pos] == '[')
            {
                int close = rest.IndexOf(']', pos);
                if (close < 0)
                    return empty;
                index = rest[(pos + 1)..close].Trim();
                pos = close + 1;
            }

            var next = new List<XElement>();
            foreach (var node in current)
            {
                var candidates = descendant ? node.Descendants() : node.Elements();
                var matches = new List<XElement>();
                foreach (var candidate in candidates)
                {
                    budget.Tick();
                    if (name == "*" || candidate.Name.LocalName == name)
                        matches.Add(candidate);
                }

                if (index == "*")
                {
                    next.AddRange(matches);
                }
                else if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                         && i >= 0 && i < matches.Count)
                {
                    next.Add(matches[i]);
                }
                else if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    // A predicate or relative index: not supported.
                    return empty;
                }
            }

            current = next;
            if (current.Count == 0)
                return current;
        }

        return current;
    }

    /// <summary>
    /// The text of the data value <paramref name="relative"/> names inside <paramref name="node"/>, for
    /// <c>bindItems labelRef</c> and <c>valueRef</c> (XFA 3.3 p212, p624). <c>$</c> is the node itself.
    /// A plain name may be an XML attribute: the data loader loads attributes as data values by default
    /// and places them before the element's content (p142), so an attribute wins over a same-named
    /// child element. Null when nothing matches.
    /// </summary>
    public static string? ItemText(XElement node, string? relative, XfaBudget budget)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        var expr = relative.Trim();
        if (expr == "$")
            return node.Value;
        if (expr.StartsWith("$.", StringComparison.Ordinal))
            expr = expr[2..];

        bool plainName = expr.Length > 0 && expr.IndexOfAny(new[] { '.', '[', '$', '!', '#' }) < 0;
        if (plainName)
        {
            if (node.Attributes().FirstOrDefault(a => a.Name.NamespaceName.Length == 0 && a.Name.LocalName == expr) is { } attribute)
                return attribute.Value;
            return node.Elements().FirstOrDefault(e => e.Name.LocalName == expr)?.Value;
        }

        return Evaluate(expr, node, dataRoot: null, budget).FirstOrDefault()?.Value;
    }
}
