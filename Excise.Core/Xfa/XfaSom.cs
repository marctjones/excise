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
    /// The data node a <c>bind match="dataRef"</c> expression names, created when it does not exist
    /// (XFA 3.3 p199, #2021): "the binding process creates one with default properties", and any
    /// missing ancestor "so that the referenced data node can link into the Data DOM". A step with
    /// <c>..</c> refuses ("such an expression is ambiguous"); <c>[*]</c> adds one more instance, or
    /// index 0 when nothing matches. Excise also refuses what it cannot create without inventing
    /// other data: an index past the next free one, a predicate, a <c>*</c> name, or a start other
    /// than <c>$record</c>, <c>$data</c>, <c>$</c> or a bare relative name.
    /// </summary>
    /// <param name="scope">The data scope <c>$</c> and a bare relative name start from.</param>
    /// <param name="newChild">Creates (and appends) a child data node of the given name under a parent.</param>
    /// <param name="created">True when at least one node was created.</param>
    /// <param name="refusal">Why nothing was created, when the result is null.</param>
    public static XElement? EvaluateOrCreate(
        string? expression, XElement? scope, XElement dataRoot, XfaBudget budget,
        Func<XElement, string, XElement> newChild, out bool created, out string? refusal)
    {
        created = false;
        refusal = null;
        if (Evaluate(expression, scope, dataRoot, budget).FirstOrDefault() is { } existing)
            return existing;

        var expr = (expression ?? string.Empty).Trim();
        XElement? current;
        string rest;
        if (expr.StartsWith("$record", StringComparison.Ordinal))
        {
            current = dataRoot;
            rest = expr["$record".Length..];
        }
        else if (expr.StartsWith("$data", StringComparison.Ordinal))
        {
            current = dataRoot.Parent;
            rest = expr["$data".Length..];
        }
        else if (expr.StartsWith('$') && (expr.Length == 1 || expr[1] == '.'))
        {
            current = scope;
            rest = expr[1..];
        }
        else if (expr.Length > 0 && (char.IsLetter(expr[0]) || expr[0] == '_'))
        {
            current = scope;
            rest = "." + expr;
        }
        else
        {
            refusal = $"its dataRef '{expression}' does not start at $record, $data or $";
            return null;
        }
        if (current == null)
        {
            refusal = $"its dataRef '{expression}' starts at a data node that does not exist";
            return null;
        }
        if (rest.Length == 0)
        {
            refusal = $"its dataRef '{expression}' names a scope, not a data node";
            return null;
        }

        // Validate the whole path before creating anything, so a refusal leaves the data as it was.
        var steps = new List<(string Name, string Index)>();
        int pos = 0;
        while (pos < rest.Length)
        {
            budget.Tick();
            if (string.CompareOrdinal(rest, pos, "..", 0, 2) == 0)
            {
                refusal = $"its dataRef '{expression}' contains '..', which is ambiguous, so no node is created (XFA 3.3 p199)";
                return null;
            }
            if (rest[pos] != '.')
            {
                refusal = $"its dataRef '{expression}' is not a dotted path excise can create";
                return null;
            }
            pos++;
            int nameStart = pos;
            while (pos < rest.Length && rest[pos] != '.' && rest[pos] != '[')
                pos++;
            var name = rest[nameStart..pos];
            string index = "0";
            if (pos < rest.Length && rest[pos] == '[')
            {
                int close = rest.IndexOf(']', pos);
                if (close < 0)
                {
                    refusal = $"its dataRef '{expression}' has an unclosed index";
                    return null;
                }
                index = rest[(pos + 1)..close].Trim();
                pos = close + 1;
            }
            if (name.Length == 0 || name == "*" || !IsXmlName(name))
            {
                refusal = $"its dataRef '{expression}' has a step '{name}' that is not a data node name";
                return null;
            }
            if (index != "*" && !(int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                refusal = $"its dataRef '{expression}' has an index '[{index}]' excise does not create";
                return null;
            }
            steps.Add((name, index));
        }

        // Plan, then create: the first step whose node is missing and every step after it.
        var plan = new List<(XElement? Node, string Name)>();
        XElement? walk = current;
        foreach (var (name, index) in steps)
        {
            budget.Tick();
            if (walk == null)
            {
                if (index != "*" && index != "0")
                {
                    refusal = $"its dataRef '{expression}' indexes [{index}] under a node that does not exist yet";
                    return null;
                }
                plan.Add((null, name));
                continue;
            }
            var matches = walk.Elements().Where(e => e.Name.LocalName == name).ToList();
            if (index == "*")
            {
                plan.Add((null, name));     // p199: one more instance of the object matching "*"
                walk = null;
                continue;
            }
            int i = int.Parse(index, NumberStyles.None, CultureInfo.InvariantCulture);
            if (i < matches.Count)
            {
                plan.Add((matches[i], name));
                walk = matches[i];
            }
            else if (i == matches.Count)
            {
                plan.Add((null, name));
                walk = null;
            }
            else
            {
                refusal = $"its dataRef '{expression}' indexes {name}[{i}] but only {matches.Count} exist, " +
                          "and excise does not invent the ones between";
                return null;
            }
        }

        var node = current;
        foreach (var (existingNode, name) in plan)
        {
            if (existingNode != null)
            {
                node = existingNode;
                continue;
            }
            node = newChild(node, name);
            created = true;
        }
        return node;
    }

    private static bool IsXmlName(string name)
    {
        try
        {
            System.Xml.XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
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
