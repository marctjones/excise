using System.Globalization;
using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>
/// A small subset of XFA SOM expressions for <c>bind match="dataRef"</c>:
/// <c>$</c> (current data scope), <c>$record</c> (the root data group),
/// <c>$data</c> (its parent), <c>!</c> (the datasets packet), dotted names,
/// <c>..</c> for descendants, and <c>[n]</c> / <c>[*]</c> indexes.
/// </summary>
/// <remarks>
/// Anything else (predicates, <c>$template</c>, script calls) resolves to
/// nothing, so the field shows its template value.
/// </remarks>
internal static class XfaSom
{
    public static List<XElement> Evaluate(string? expression, XElement? scope, XElement dataRoot, XfaBudget budget)
    {
        var empty = new List<XElement>();
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 1024)
            return empty;

        var expr = expression.Trim();
        List<XElement> current;
        string rest;

        if (expr.StartsWith("$record", StringComparison.Ordinal))
        {
            current = new List<XElement> { dataRoot };
            rest = expr["$record".Length..];
        }
        else if (expr.StartsWith("$data", StringComparison.Ordinal))
        {
            if (dataRoot.Parent is not { } data)
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
            if (dataRoot.Parent?.Parent is not { } datasets)
                return empty;
            current = new List<XElement> { datasets };
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
}
