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

    /// <summary>
    /// A presence a script set on THIS instance. The template element is shared by every instance of a
    /// repeated subform, so a script's show or hide must not be written to it.
    /// </summary>
    public string? PresenceOverride { get; set; }

    public string Presence => PresenceOverride ?? Element.Presence();

    /// <summary>Hidden and inactive objects take no space; invisible ones do.</summary>
    public bool TakesNoSpace => Presence is "hidden" or "inactive";

    public bool IsContainer => Kind is XfaNodeKind.Subform or XfaNodeKind.Area or XfaNodeKind.ExclGroup;

    /// <summary>
    /// Containers: the data group this instance's children bind within (the
    /// merge scope). Null when there is no data, or a named subform found no
    /// data group to bind to. Static-XFA write-back (#2013) reads it.
    /// </summary>
    public XElement? DataScope { get; set; }

    /// <summary>
    /// Fields and exclusion groups: the data node the merge bound this object
    /// to, or null when nothing bound (no data, <c>match="none"</c>, or no
    /// matching node). Static-XFA write-back (#2013) writes the value here.
    /// </summary>
    public XElement? BoundData { get; set; }

    /// <summary>
    /// Choice-list items resolved from <c>bindItems</c> (XFA 3.3 p212, p624), which replace the
    /// template's <c>items</c> for this instance; null when the field has none. Kept on the node
    /// because the template element is shared by every instance.
    /// </summary>
    public IReadOnlyList<XfaItem>? BoundItems { get; set; }

    /// <summary>Exclusion groups: how the data supplied the selection (XFA 3.3 p196-197).</summary>
    public XfaExclGroupFormat Format { get; set; }
}

/// <summary>One choice-list item: the text shown and the value saved (XFA 3.3 p758-760).</summary>
internal readonly record struct XfaItem(string Display, string Save);

/// <summary>How an exclusion group's selection is held in the data (XFA 3.3 p196-197).</summary>
internal enum XfaExclGroupFormat
{
    /// <summary>Nothing in the data selects a member (no data, or none bound).</summary>
    Unbound,

    /// <summary>The group binds one data value holding the selected member's on value.</summary>
    Short,

    /// <summary>Each member binds its own data value.</summary>
    Long,
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
    private readonly XElement? _datasets;
    private readonly HashSet<XElement> _consumed = new();

    /// <param name="datasets">The <c>&lt;xfa:datasets&gt;</c> element, for <c>bindItems</c>; null when absent.</param>
    public XfaMerge(XfaBudget budget, XfaReport report, XElement? dataRoot, XElement? datasets = null)
    {
        _budget = budget;
        _report = report;
        _dataRoot = dataRoot;
        _datasets = datasets ?? dataRoot?.Parent?.Parent;
    }

    public XfaFormNode Merge(XElement rootSubform)
    {
        var root = new XfaFormNode(XfaNodeKind.Subform, rootSubform) { DataScope = _dataRoot };
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
                    var area = new XfaFormNode(XfaNodeKind.Area, child) { DataScope = scope };
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
        var node = new XfaFormNode(XfaNodeKind.Subform, subform) { DataScope = scope };
        BuildChildren(node, subform, scope, depth + 1);
        return node;
    }

    private XfaFormNode BuildExclGroup(XElement group, XElement? scope, int depth)
    {
        CountScripts(group);
        var node = new XfaFormNode(XfaNodeKind.ExclGroup, group) { DataScope = scope };

        // XFA 3.3 p196-197. Short format: the named group binds a data value and its members stay
        // unbound. Long format: the members bind their own data values, inside the group's data
        // group when it has one, else (an unnamed group, or no data for the group) in the parent's
        // scope as before.
        var bound = Bind(group, scope);
        if (bound != null && bound.Elements().Any())
        {
            node.DataScope = bound;
            node.Format = XfaExclGroupFormat.Long;
            BuildChildren(node, group, bound, depth + 1);
            return node;
        }

        if (bound == null)
        {
            BuildChildren(node, group, scope, depth + 1);
            if (node.Children.Any(c => c.Kind == XfaNodeKind.Field && c.BoundData != null))
                node.Format = XfaExclGroupFormat.Long;
            return node;
        }

        BuildChildren(node, group, scope: null, depth + 1);
        node.BoundData = bound;
        node.Format = XfaExclGroupFormat.Short;

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
        node.BoundData = bound;
        if (bound != null)
        {
            if (IsMultiSelect(field) && bound.Elements().Any())
            {
                // XFA 3.3 p198: a multi-select choice list binds a data GROUP; its value is the
                // values of all the group's children, newline-separated.
                node.RichValue = null;
                node.Value = string.Join("\n", bound.Elements().Select(DataText));
            }
            else if (XfaRichText.FindXhtmlBody(bound) is { } body)
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

        BindItems(field, node);
        return node;
    }

    private static bool IsMultiSelect(XElement field)
        => field.Child("ui")?.Child("choiceList")?.Attr("open") == "multiSelect";

    /// <summary>
    /// <c>bindItems</c> (XFA 3.3 p212, p624): the choice list's items come from a set of data nodes,
    /// each giving a value (<c>valueRef</c>) and a label (<c>labelRef</c>, else the value), and they
    /// replace the template's <c>items</c>. <c>ref</c> is evaluated from the field's bound data node.
    /// Only the datasets-internal form is resolved: a <c>connection</c> (a web service) is never
    /// contacted. Check buttons and radio buttons may also take bindItems; none in the corpus does,
    /// and only choice lists are resolved here.
    /// </summary>
    private void BindItems(XElement field, XfaFormNode node)
    {
        var bindItems = field.ChildrenNamed("bindItems").ToList();
        if (bindItems.Count == 0 || field.Child("ui")?.Child("choiceList") == null)
            return;

        var items = new List<XfaItem>();
        bool any = false;
        foreach (var bind in bindItems)
        {
            if (!string.IsNullOrEmpty(bind.Attr("connection")))
            {
                _report.Note("bindItems from a connection not resolved (no web service is contacted)");
                continue;
            }
            var reference = bind.Attr("ref");
            if (string.IsNullOrWhiteSpace(reference))
                continue;
            any = true;
            foreach (var member in XfaSom.Evaluate(reference, node.BoundData, _dataRoot, _budget, _datasets))
            {
                _budget.Tick();
                if (_datasets != null && !member.AncestorsAndSelf().Contains(_datasets))
                    continue;   // p212: the ref names data nodes
                var value = XfaSom.ItemText(member, bind.Attr("valueRef"), _budget);
                if (value == null)
                    continue;
                var label = XfaSom.ItemText(member, bind.Attr("labelRef"), _budget) ?? value;
                items.Add(new XfaItem(label, value));
            }
        }

        if (any)
            node.BoundItems = items;
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

    /// <summary>
    /// The check button's off value: its second <c>items</c> entry. When <c>items</c> is present
    /// without one, the spec default is the null string (XFA 3.3 p759, #2016). With no <c>items</c>
    /// at all, "0": the pages read for #2016 (p650-651, p758-760) state no default for that case.
    /// </summary>
    public static string OffValue(XElement field)
    {
        var items = field.ChildrenNamed("items").FirstOrDefault();
        if (items == null)
            return "0";
        return ItemTexts(items).Skip(1).FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// A choice list's items as (display, save) pairs: the resolved <c>bindItems</c> when there are
    /// any, else the template's <c>items</c>. With one <c>items</c> list the displayed text is also
    /// what is saved; with two, the save column is the first flagged <c>save="1"</c> and the other
    /// is displayed (XFA 3.3 p758, p760).
    /// </summary>
    public static IReadOnlyList<XfaItem> ChoiceItems(XfaFormNode node)
    {
        if (node.BoundItems != null)
            return node.BoundItems;

        var lists = node.Element.ChildrenNamed("items").ToList();
        var saveList = lists.FirstOrDefault(l => l.Attr("save") == "1");
        var displayList = lists.FirstOrDefault(l => l.Attr("save") != "1") ?? lists.FirstOrDefault();
        var display = ItemTexts(displayList);
        var save = saveList != null ? ItemTexts(saveList) : display;
        var result = new List<XfaItem>(display.Count);
        for (int i = 0; i < display.Count; i++)
            result.Add(new XfaItem(display[i], i < save.Count ? save[i] : display[i]));
        return result;
    }

    public static List<string> ItemTexts(XElement? items)
        => items == null
            ? new List<string>()
            : items.Elements().Select(e => e.Value).ToList();
}
