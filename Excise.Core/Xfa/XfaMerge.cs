using System.Xml.Linq;

namespace Excise.Core.Xfa;

internal enum XfaNodeKind
{
    Subform,
    Area,
    ExclGroup,
    Field,
    Draw,
}

/// <summary>
/// One object of the merged form: a template element plus its instance data.
/// </summary>
internal sealed class XfaFormNode
{
    public XfaFormNode(XfaNodeKind kind, XElement element)
    {
        Kind = kind;
        Element = element;
    }

    public XfaNodeKind Kind { get; }

    /// <summary>The resolved template element this node instantiates.</summary>
    public XElement Element { get; }

    public List<XfaFormNode> Children { get; } = new();

    /// <summary>
    /// The raw value (fields and draws): bound data when there is any, else the
    /// template's own value. Never formatted by a picture clause.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Rich-text value (an <c>exData</c> or data element holding XHTML).</summary>
    public XElement? RichValue { get; set; }

    public bool IsContainer => Kind is XfaNodeKind.Subform or XfaNodeKind.Area or XfaNodeKind.ExclGroup;
}

/// <summary>
/// Builds the form DOM from the resolved template and the data (XFA 3.3,
/// "Data merging"): repeated subforms are expanded by <c>occur</c> and by the
/// data groups they bind to, and fields take their values from the data.
/// </summary>
/// <remarks>
/// Only the binding rules listed in docs/architecture/xfa-rendering.md are
/// implemented. Scripts never run (#1570, #1571); the report counts them.
/// </remarks>
internal sealed class XfaMerge
{
    private readonly XfaBudget _budget;
    private readonly XfaReport _report;
    private readonly XElement? _dataRoot;
    private readonly HashSet<XElement> _consumed = new();

    public XfaMerge(XfaBudget budget, XfaReport report, XElement? dataRoot)
    {
        _budget = budget;
        _report = report;
        _dataRoot = dataRoot;
    }

    public XfaFormNode Merge(XElement rootSubform)
    {
        var root = new XfaFormNode(XfaNodeKind.Subform, rootSubform);
        CountScripts(rootSubform);
        BuildChildren(root, rootSubform, _dataRoot, depth: 1);
        return root;
    }

    /// <summary>
    /// A page area's own content (headers, footers, page furniture). It is
    /// laid out at the page origin on every page that uses the area.
    /// </summary>
    public XfaFormNode MergePageArea(XElement pageArea)
    {
        var node = new XfaFormNode(XfaNodeKind.Area, pageArea);
        CountScripts(pageArea);
        BuildChildren(node, pageArea, scope: null, depth: 1);
        return node;
    }

    private void CountScripts(XElement container)
    {
        foreach (var ev in container.ChildrenNamed("event"))
        {
            if (ev.Child("script") != null)
                _report.Script(ev.AttrOr("activity", "event"));
        }

        foreach (var name in new[] { "calculate", "validate" })
        {
            if (container.Child(name)?.Child("script") != null)
                _report.Script(name);
        }
    }

    private void BuildChildren(XfaFormNode parent, XElement container, XElement? scope, int depth)
    {
        XfaBudget.CheckDepth(depth);

        foreach (var child in container.Elements())
        {
            _budget.Tick();
            switch (child.Name.LocalName)
            {
                case "subform":
                    foreach (var instance in ExpandSubform(child, scope, depth))
                        parent.Children.Add(instance);
                    break;

                case "subformSet":
                    // Ordered and unordered sets contribute all their members; a
                    // "choice" set would pick one from the data, which is
                    // approximated by its first member.
                    if (child.AttrOr("relation", "ordered") == "choice")
                    {
                        _report.Note("subformSet choice shows its first member");
                        var first = child.Elements().FirstOrDefault(e => e.Name.LocalName is "subform" or "subformSet");
                        if (first != null)
                        {
                            var wrapper = new XElement(child.Name, first);
                            BuildChildren(parent, wrapper, scope, depth + 1);
                        }
                    }
                    else
                    {
                        BuildChildren(parent, child, scope, depth + 1);
                    }
                    break;

                case "area":
                {
                    var area = new XfaFormNode(XfaNodeKind.Area, child);
                    BuildChildren(area, child, scope, depth + 1);
                    parent.Children.Add(area);
                    break;
                }

                case "exclGroup":
                    parent.Children.Add(BuildExclGroup(child, scope, depth));
                    break;

                case "field":
                    parent.Children.Add(BuildField(child, scope));
                    break;

                case "draw":
                    parent.Children.Add(BuildDraw(child));
                    break;

                case "pageSet":
                    // Pagination reads the page set separately.
                    break;

                case "breakBefore":
                case "breakAfter":
                case "break":
                    break;

                case "keep":
                case "overflow":
                    if (child.Attributes().Any(a => a.Name.LocalName is "leader" or "trailer"))
                        _report.Note("overflow leaders and trailers are not drawn");
                    break;
            }
        }
    }

    private IEnumerable<XfaFormNode> ExpandSubform(XElement subform, XElement? scope, int depth)
    {
        var occur = subform.Child("occur");
        int min = Math.Max(0, occur.IntAttr("min", 1));
        int max = occur == null ? 1 : occur.IntAttr("max", -1);
        int initial = Math.Max(0, occur.IntAttr("initial", min));
        int cap = max < 0 ? XfaBudget.MaxInstancesPerSubform : Math.Min(max, XfaBudget.MaxInstancesPerSubform);
        min = Math.Min(min, cap);

        var name = subform.Attr("name");
        var bind = subform.Child("bind");
        var match = bind.AttrOr("match", "once");
        bool transparent = string.IsNullOrEmpty(name) || match == "none";

        var instances = new List<XfaFormNode>();

        if (_dataRoot == null || transparent || match == "global")
        {
            int count = Math.Min(Math.Max(initial, min), cap);
            for (int i = 0; i < count; i++)
                instances.Add(BuildSubformInstance(subform, _dataRoot == null ? null : scope, depth));
            return instances;
        }

        List<XElement> candidates = match == "dataRef"
            ? XfaSom.Evaluate(bind.Attr("ref"), scope, _dataRoot, _budget)
            : Unconsumed(scope, name!);

        int n = Math.Clamp(candidates.Count, min, cap);
        for (int i = 0; i < n; i++)
        {
            XElement? instanceScope = null;
            if (i < candidates.Count)
            {
                instanceScope = candidates[i];
                _consumed.Add(instanceScope);
            }
            instances.Add(BuildSubformInstance(subform, instanceScope, depth));
        }
        return instances;
    }

    private XfaFormNode BuildSubformInstance(XElement subform, XElement? scope, int depth)
    {
        _budget.CountInstance();
        CountScripts(subform);
        var node = new XfaFormNode(XfaNodeKind.Subform, subform);
        BuildChildren(node, subform, scope, depth + 1);
        return node;
    }

    private XfaFormNode BuildExclGroup(XElement group, XElement? scope, int depth)
    {
        CountScripts(group);
        var node = new XfaFormNode(XfaNodeKind.ExclGroup, group);
        BuildChildren(node, group, scope, depth + 1);

        var bound = Bind(group, scope);
        if (bound == null)
            return node;

        var selected = DataText(bound);
        foreach (var member in node.Children.Where(c => c.Kind == XfaNodeKind.Field))
        {
            var on = XfaValues.OnValue(member.Element);
            member.Value = on == selected ? on : XfaValues.OffValue(member.Element);
        }
        return node;
    }

    private XfaFormNode BuildField(XElement field, XElement? scope)
    {
        CountScripts(field);
        var node = new XfaFormNode(XfaNodeKind.Field, field);
        XfaValues.ReadTemplateValue(field, node);

        var bound = Bind(field, scope);
        if (bound != null)
        {
            if (XfaRichText.FindXhtmlBody(bound) is { } body)
            {
                node.RichValue = body;
                node.Value = XfaRichText.PlainText(body);
            }
            else
            {
                node.RichValue = null;
                node.Value = DataText(bound);
            }
        }
        return node;
    }

    private XfaFormNode BuildDraw(XElement draw)
    {
        var node = new XfaFormNode(XfaNodeKind.Draw, draw);
        XfaValues.ReadTemplateValue(draw, node);
        return node;
    }

    private XElement? Bind(XElement element, XElement? scope)
    {
        if (_dataRoot == null)
            return null;

        var name = element.Attr("name");
        var bind = element.Child("bind");
        var match = bind.AttrOr("match", "once");

        switch (match)
        {
            case "none":
                return null;

            case "dataRef":
                return XfaSom.Evaluate(bind.Attr("ref"), scope, _dataRoot, _budget).FirstOrDefault();

            case "global":
                if (string.IsNullOrEmpty(name))
                    return null;
                foreach (var candidate in _dataRoot.DescendantsAndSelf())
                {
                    _budget.Tick();
                    if (candidate.Name.LocalName == name)
                        return candidate;
                }
                return null;

            default:
                if (string.IsNullOrEmpty(name) || scope == null)
                    return null;
                var value = Unconsumed(scope, name).FirstOrDefault();
                if (value != null)
                    _consumed.Add(value);
                return value;
        }
    }

    private List<XElement> Unconsumed(XElement? scope, string name)
    {
        var result = new List<XElement>();
        if (scope == null)
            return result;
        foreach (var child in scope.Elements())
        {
            _budget.Tick();
            if (child.Name.LocalName == name && !_consumed.Contains(child))
                result.Add(child);
        }
        return result;
    }

    private static string DataText(XElement data) => data.Value;
}

/// <summary>Template values and check-button states.</summary>
internal static class XfaValues
{
    private static readonly HashSet<string> ScalarValueKinds = new(StringComparer.Ordinal)
    {
        "text", "integer", "decimal", "float", "date", "time", "dateTime", "boolean",
    };

    public static void ReadTemplateValue(XElement element, XfaFormNode node)
    {
        var value = element.Child("value");
        if (value == null)
            return;

        foreach (var content in value.Elements())
        {
            var kind = content.Name.LocalName;
            if (ScalarValueKinds.Contains(kind))
            {
                node.Value = content.Value;
                return;
            }

            if (kind == "exData")
            {
                if (XfaRichText.FindXhtmlBody(content) is { } body)
                {
                    node.RichValue = body;
                    node.Value = XfaRichText.PlainText(body);
                }
                else
                {
                    node.Value = content.Value;
                }
                return;
            }
        }
    }

    /// <summary>The check button's on value: its first <c>items</c> entry, else "1".</summary>
    public static string OnValue(XElement field)
        => ItemTexts(field.ChildrenNamed("items").FirstOrDefault()).FirstOrDefault() ?? "1";

    public static string OffValue(XElement field)
        => ItemTexts(field.ChildrenNamed("items").FirstOrDefault()).Skip(1).FirstOrDefault() ?? "0";

    public static List<string> ItemTexts(XElement? items)
        => items == null
            ? new List<string>()
            : items.Elements().Select(e => e.Value).ToList();
}
