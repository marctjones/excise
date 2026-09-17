using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>
/// The template packet with prototypes (<c>use</c> / <c>usehref</c>) resolved
/// into a fresh element tree.
/// </summary>
/// <remarks>
/// <para>Prototype merge (XFA 3.3, "Prototypes"): attributes the element does
/// not set come from the prototype, and so do single-occurrence property
/// children. A property child present on both is merged recursively.
/// Repeatable children (containers, edges, items, ...) come from the prototype
/// only when the element has none of that name. <c>id</c>, <c>name</c>,
/// <c>use</c> and <c>usehref</c> are never inherited.</para>
/// <para>Only same-document references by id are resolved (<c>#id</c>,
/// <c>.#id</c>). SOM-expression and other-file references are counted in the
/// report and ignored; excise never opens another file or URL.</para>
/// </remarks>
internal sealed class XfaTemplate
{
    private static readonly HashSet<string> RepeatableChildren = new(StringComparer.Ordinal)
    {
        "subform", "subformSet", "field", "draw", "exclGroup", "area", "pageSet",
        "pageArea", "contentArea", "edge", "corner", "items", "event", "breakBefore",
        "breakAfter", "setProperty", "bindItems", "connect", "text", "proto",
    };

    private static readonly HashSet<string> NotInherited = new(StringComparer.Ordinal)
    {
        "id", "name", "use", "usehref",
    };

    private readonly XfaBudget _budget;
    private readonly XfaReport _report;
    private readonly Dictionary<string, XElement> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _resolvedProtos = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    public XfaTemplate(XElement template, XfaBudget budget, XfaReport report)
    {
        _budget = budget;
        _report = report;
        Namespace = template.Name.Namespace;

        foreach (var element in template.Descendants())
        {
            budget.CountElement();
            if (element.Name.Namespace == Namespace
                && element.Attribute("id")?.Value is { Length: > 0 } id)
            {
                _byId.TryAdd(id, element);
            }
        }

        var root = template.Elements(Namespace + "subform").FirstOrDefault()
            ?? throw new XfaLayoutException("The XFA template has no root subform.");
        Root = Resolve(root, 0);
    }

    public XNamespace Namespace { get; }

    /// <summary>The resolved root subform.</summary>
    public XElement Root { get; }

    private XElement Resolve(XElement element, int depth)
    {
        XfaBudget.CheckDepth(depth);
        _budget.CountElement();

        var copy = new XElement(element.Name,
            element.Attributes().Where(a => a.Name.LocalName is not ("use" or "usehref")));

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == Namespace + "proto":
                    // Prototype definitions are not form content.
                    break;
                case XElement child:
                    copy.Add(Resolve(child, depth + 1));
                    break;
                case XText text:
                    copy.Add(new XText(text.Value));
                    break;
            }
        }

        if (FindPrototype(element) is { } proto)
            Merge(copy, proto, depth);

        return copy;
    }

    private XElement? FindPrototype(XElement element)
    {
        var reference = element.Attribute("usehref")?.Value;
        if (string.IsNullOrWhiteSpace(reference))
            reference = element.Attribute("use")?.Value;
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        reference = reference.Trim();
        string? id = null;
        if (reference.StartsWith(".#", StringComparison.Ordinal))
            id = reference[2..];
        else if (reference.StartsWith('#'))
            id = reference[1..];
        else if (reference.Contains('#'))
        {
            _report.Note("prototype in another file not loaded");
            return null;
        }

        if (id == null || id.StartsWith("som(", StringComparison.Ordinal) || id.Contains('#'))
        {
            _report.Note("prototype reference by SOM expression not resolved");
            return null;
        }

        if (!_byId.ContainsKey(id))
        {
            _report.Note("prototype reference to a missing id ignored");
            return null;
        }

        if (_resolvedProtos.TryGetValue(id, out var cached))
            return cached;

        if (_resolving.Count >= XfaBudget.MaxProtoChain)
            throw new XfaLayoutException($"XFA prototype chain longer than {XfaBudget.MaxProtoChain}.");
        if (!_resolving.Add(id))
        {
            _report.Note("circular prototype reference ignored");
            return null;
        }

        try
        {
            var resolved = Resolve(_byId[id], 0);
            _resolvedProtos[id] = resolved;
            return resolved;
        }
        finally
        {
            _resolving.Remove(id);
        }
    }

    private void Merge(XElement target, XElement proto, int depth)
    {
        XfaBudget.CheckDepth(depth);
        _budget.Tick();

        foreach (var attribute in proto.Attributes())
        {
            if (NotInherited.Contains(attribute.Name.LocalName) || attribute.IsNamespaceDeclaration)
                continue;
            if (target.Attribute(attribute.Name) == null)
                target.SetAttributeValue(attribute.Name, attribute.Value);
        }

        if (!target.Elements().Any() && string.IsNullOrWhiteSpace(OwnText(target)))
        {
            foreach (var node in proto.Nodes())
                target.Add(Clone(node));
            return;
        }

        foreach (var group in proto.Elements().GroupBy(e => e.Name))
        {
            if (RepeatableChildren.Contains(group.Key.LocalName))
            {
                if (!target.Elements(group.Key).Any())
                {
                    foreach (var child in group)
                        target.Add(Clone(child));
                }
                continue;
            }

            var protoChild = group.First();
            var own = target.Element(group.Key);
            if (own == null)
                target.Add(Clone(protoChild));
            else
                Merge(own, protoChild, depth + 1);
        }
    }

    private static string OwnText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));

    private XNode Clone(XNode node)
    {
        if (node is XElement element)
        {
            foreach (var _ in element.DescendantsAndSelf())
                _budget.CountElement();
            return new XElement(element);
        }

        return node is XText text ? new XText(text.Value) : new XText(string.Empty);
    }
}

/// <summary>Accessors for template elements in the template's own namespace.</summary>
internal static class XfaXml
{
    public static XElement? Child(this XElement element, string localName)
        => element.Element(element.Name.Namespace + localName);

    public static IEnumerable<XElement> ChildrenNamed(this XElement element, string localName)
        => element.Elements(element.Name.Namespace + localName);

    public static string? Attr(this XElement? element, string name)
        => element?.Attribute(name)?.Value;

    public static string AttrOr(this XElement? element, string name, string fallback)
        => element?.Attribute(name)?.Value is { Length: > 0 } value ? value.Trim() : fallback;

    public static double? Measure(this XElement? element, string name, string defaultUnit = "in")
        => XfaMeasure.Parse(element?.Attribute(name)?.Value, defaultUnit);

    public static int IntAttr(this XElement? element, string name, int fallback)
        => int.TryParse(element?.Attribute(name)?.Value?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary><c>presence</c> of an element: visible, hidden, invisible or inactive.</summary>
    public static string Presence(this XElement? element)
        => element.AttrOr("presence", "visible");

    /// <summary>Hidden and inactive objects take no space; invisible ones do.</summary>
    public static bool TakesNoSpace(this XElement element)
        => element.Presence() is "hidden" or "inactive";
}
