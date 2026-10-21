using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>
/// Whether the XFA datasets fill any field of the form (#2023): the merge binds each field and
/// exclusion group to its data node (XFA 3.3 p176), and a bound node that holds a value other
/// than the field's unset state is a filled value. A blank form is not filled although its
/// datasets carry nodes: empty ones, and check buttons at their off value (the W-9's
/// <c>&lt;c1_1&gt;0&lt;/c1_1&gt;</c>, items 1..7 / 0). Data no field binds is not drawn, so it
/// does not count; a template's own default value is not data, so it does not count either.
/// </summary>
internal static class XfaFilledData
{
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when a field or exclusion group is bound to a data node holding a value. Throws
    /// (<see cref="XfaLayoutException"/>, among others) when the merge cannot run; the caller
    /// decides what an unreadable form means.
    /// </summary>
    public static bool HasFilledValue(XfaPackets packets)
    {
        if (packets.DataRoot == null)
            return false;

        var budget = new XfaBudget(TimeLimit, CancellationToken.None);
        var report = new XfaReport();
        var template = new XfaTemplate(packets.Template, budget, report);
        var form = new XfaMerge(budget, report, packets.DataRoot, packets.Datasets).Merge(template.Root);

        var stack = new Stack<XfaFormNode>();
        stack.Push(form);
        while (stack.Count > 0)
        {
            budget.Tick();
            var node = stack.Pop();
            if (node.BoundData is { } data && IsFilled(node, data))
                return true;
            foreach (var child in node.Children)
                stack.Push(child);
        }
        return false;
    }

    private static bool IsFilled(XfaFormNode node, XElement data)
    {
        var value = data.Value;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (node.Kind)
        {
            case XfaNodeKind.Field when UiKind(node.Element) == "checkButton":
                // Off and neutral are the unset states (XFA 3.3 p759).
                var items = XfaValues.ItemTexts(node.Element.ChildrenNamed("items").FirstOrDefault());
                var neutral = items.Skip(2).FirstOrDefault();
                return value != XfaValues.OffValue(node.Element) && value != neutral;
            case XfaNodeKind.ExclGroup:
                // Short format: the node holds the selected member's on value (p196). A member's
                // off value there selects nothing.
                return !node.Children.Any(m => m.Kind == XfaNodeKind.Field && value == XfaValues.OffValue(m.Element));
            case XfaNodeKind.Field:
                return true;
            default:
                return false;
        }
    }

    private static string? UiKind(XElement field)
        => field.Child("ui")?.Elements()
            .FirstOrDefault(w => w.Name.LocalName is not ("extras" or "picture"))?.Name.LocalName;
}
