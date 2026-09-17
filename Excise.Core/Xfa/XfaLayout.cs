using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>A page or content-area break requested by a container.</summary>
internal sealed record XfaBreak(string TargetType, string? Target, bool StartNew);

/// <summary>
/// One laid-out object. Positions are relative to the parent's content origin
/// (inside the parent's margins); pagination turns them into page positions.
/// </summary>
internal sealed class XfaBox
{
    public XfaBox(XfaFormNode node) => Node = node;

    public XfaFormNode Node { get; }

    /// <summary>Resolved leaf (fields and draws only).</summary>
    public XfaLeaf? Leaf { get; init; }

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    public XfaInsets Margin { get; init; }

    /// <summary>
    /// Content grouped into lines: the units pagination may split between. A
    /// positioned container has one line holding every child.
    /// </summary>
    public List<XfaLine> Lines { get; } = new();

    /// <summary>Whether pagination may split this container between lines.</summary>
    public bool Splittable { get; set; }

    /// <summary><c>presence="invisible"</c>: takes space, draws nothing.</summary>
    public bool Invisible { get; init; }

    public XfaBreak? BreakBefore { get; init; }

    public XfaBreak? BreakAfter { get; init; }

    /// <summary>
    /// A descendant requests a break, so pagination must place this container
    /// line by line even when it fits.
    /// </summary>
    public bool ContainsBreak { get; set; }
}

internal sealed class XfaLine
{
    public double Y { get; set; }

    public double H { get; set; }

    public List<XfaBox> Items { get; } = new();
}

/// <summary>
/// Phase one of layout: sizes and relative positions for every object, with
/// no page limits. See docs/architecture/xfa-rendering.md.
/// </summary>
internal sealed class XfaLayout
{
    private const double Unbounded = 1_000_000;

    private readonly XfaBudget _budget;
    private readonly XfaReport _report;

    public XfaLayout(XfaBudget budget, XfaReport report)
    {
        _budget = budget;
        _report = report;
    }

    public XfaBox? Build(XfaFormNode node, double available, int depth = 0, double? forcedWidth = null)
    {
        XfaBudget.CheckDepth(depth);
        var e = node.Element;
        if (e.TakesNoSpace())
            return null;

        _budget.CountBox();

        if (!node.IsContainer)
        {
            var leaf = XfaLeaf.From(node, _report);
            var (w, h) = leaf.Measure(forcedWidth, available, _budget);
            return new XfaBox(node)
            {
                Leaf = leaf,
                W = w,
                H = h,
                Invisible = e.Presence() == "invisible",
            };
        }

        var margin = XfaInsets.From(e.Child("margin"));
        var box = new XfaBox(node)
        {
            Margin = margin,
            Invisible = e.Presence() == "invisible",
            BreakBefore = ReadBreak(e, before: true),
            BreakAfter = ReadBreak(e, before: false),
        };

        var fixedW = forcedWidth ?? e.Measure("w");
        var fixedH = e.Measure("h");
        var minW = Math.Max(0, e.Measure("minW") ?? 0);
        var maxW = e.Measure("maxW") is { } mw && mw > 0 ? mw : (double?)null;
        var minH = Math.Max(0, e.Measure("minH") ?? 0);
        var maxH = e.Measure("maxH") is { } mh && mh > 0 ? mh : (double?)null;

        double outer = fixedW ?? Math.Min(maxW ?? Unbounded, available > 0 ? available : Unbounded);
        double inner = Math.Max(0, outer - margin.Horizontal);

        var layout = node.Kind == XfaNodeKind.Area ? "position" : e.AttrOr("layout", "position");
        double contentW, contentH;
        switch (layout)
        {
            case "tb":
                (contentW, contentH) = LayoutTopToBottom(box, node.Children, inner, depth);
                break;
            case "lr-tb":
            case "rl-tb":
                (contentW, contentH) = LayoutRows(box, node.Children, inner, depth, wrap: true);
                break;
            case "row":
                (contentW, contentH) = LayoutRows(box, node.Children, inner, depth, wrap: false);
                break;
            case "table":
                (contentW, contentH) = LayoutTable(box, node, inner, depth);
                break;
            default:
                (contentW, contentH) = LayoutPositioned(box, node.Children, inner, depth);
                break;
        }

        box.W = fixedW ?? Clamp(contentW + margin.Horizontal, minW, maxW);
        box.H = fixedH ?? Clamp(contentH + margin.Vertical, minH, maxH);

        if (layout == "rl-tb")
        {
            var innerWidth = box.W - margin.Horizontal;
            foreach (var item in box.Lines.SelectMany(l => l.Items))
                item.X = innerWidth - item.X - item.W;
        }

        box.ContainsBreak = box.Lines.SelectMany(l => l.Items)
            .Any(k => k.BreakBefore != null || k.BreakAfter != null || k.ContainsBreak);

        var intact = e.Child("keep").AttrOr("intact", "none");
        box.Splittable = node.Kind == XfaNodeKind.Subform
            && fixedH == null
            && layout is "tb" or "table" or "lr-tb" or "rl-tb"
            && intact == "none";

        return box;
    }

    private static double Clamp(double value, double min, double? max)
    {
        if (max is { } m)
            value = Math.Min(value, m);
        return Math.Max(value, min);
    }

    private (double W, double H) LayoutPositioned(XfaBox box, List<XfaFormNode> children, double inner, int depth)
    {
        var line = new XfaLine();
        double right = 0, bottom = 0;
        foreach (var child in children)
        {
            var kid = Build(child, inner, depth + 1);
            if (kid == null)
                continue;

            var e = child.Element;
            double x = e.Measure("x") ?? 0;
            double y = e.Measure("y") ?? 0;
            switch (e.AttrOr("anchorType", "topLeft"))
            {
                case "topCenter": x -= kid.W / 2; break;
                case "topRight": x -= kid.W; break;
                case "middleLeft": y -= kid.H / 2; break;
                case "middleCenter": x -= kid.W / 2; y -= kid.H / 2; break;
                case "middleRight": x -= kid.W; y -= kid.H / 2; break;
                case "bottomLeft": y -= kid.H; break;
                case "bottomCenter": x -= kid.W / 2; y -= kid.H; break;
                case "bottomRight": x -= kid.W; y -= kid.H; break;
            }
            kid.X = x;
            kid.Y = y;
            line.Items.Add(kid);
            right = Math.Max(right, x + kid.W);
            bottom = Math.Max(bottom, y + kid.H);
        }

        line.Y = 0;
        line.H = bottom;
        if (line.Items.Count > 0)
            box.Lines.Add(line);
        return (right, bottom);
    }

    private (double W, double H) LayoutTopToBottom(XfaBox box, List<XfaFormNode> children, double inner, int depth)
    {
        double y = 0, widest = 0;
        foreach (var child in children)
        {
            var kid = Build(child, inner, depth + 1);
            if (kid == null)
                continue;
            kid.X = 0;
            kid.Y = y;
            var line = new XfaLine { Y = y, H = kid.H };
            line.Items.Add(kid);
            box.Lines.Add(line);
            y += kid.H;
            widest = Math.Max(widest, kid.W);
        }
        return (widest, y);
    }

    private (double W, double H) LayoutRows(XfaBox box, List<XfaFormNode> children, double inner, int depth, bool wrap)
    {
        double y = 0, widest = 0;
        var line = new XfaLine();
        double x = 0;

        void Close()
        {
            if (line.Items.Count == 0)
                return;
            line.Y = y;
            line.H = line.Items.Max(i => i.H);
            foreach (var item in line.Items)
                item.Y = y;
            box.Lines.Add(line);
            widest = Math.Max(widest, x);
            y += line.H;
            line = new XfaLine();
            x = 0;
        }

        foreach (var child in children)
        {
            var kid = Build(child, Math.Max(0, inner - x), depth + 1);
            if (kid == null)
                continue;
            if (wrap && line.Items.Count > 0 && x + kid.W > inner + 0.01)
                Close();
            kid.X = x;
            line.Items.Add(kid);
            x += kid.W;
        }
        Close();
        return (widest, y);
    }

    private (double W, double H) LayoutTable(XfaBox box, XfaFormNode table, double inner, int depth)
    {
        var e = table.Element;
        var widths = XfaMeasure.ParseList(e.Attr("columnWidths"));
        double y = 0, widest = 0;

        // Auto columns (-1 or missing) take the widest natural cell width.
        var rows = table.Children
            .Where(c => c.Kind == XfaNodeKind.Subform && c.Element.AttrOr("layout", "position") == "row"
                        && !c.Element.TakesNoSpace())
            .ToList();
        int columns = Math.Max(widths.Count,
            rows.Count == 0 ? 0 : rows.Max(r => r.Children.Where(c => !c.Element.TakesNoSpace()).Sum(c => Math.Max(1, c.Element.IntAttr("colSpan", 1)))));
        columns = Math.Min(columns, 1000);
        while (widths.Count < columns)
            widths.Add(-1);

        for (int col = 0; col < columns; col++)
        {
            if (widths[col] >= 0)
                continue;
            double natural = 0;
            foreach (var row in rows)
            {
                int c = 0;
                foreach (var cell in row.Children.Where(k => !k.Element.TakesNoSpace()))
                {
                    int span = cell.Element.IntAttr("colSpan", 1);
                    if (c == col && span == 1)
                    {
                        var measured = Build(cell, inner, depth + 2);
                        natural = Math.Max(natural, measured?.W ?? 0);
                    }
                    c += Math.Max(1, span);
                }
            }
            widths[col] = natural;
        }

        foreach (var child in table.Children)
        {
            _budget.Tick();
            bool isRow = child.Kind == XfaNodeKind.Subform && child.Element.AttrOr("layout", "position") == "row";
            XfaBox? kid = isRow ? BuildRow(child, widths, depth + 1) : Build(child, inner, depth + 1);
            if (kid == null)
                continue;
            kid.X = 0;
            kid.Y = y;
            var line = new XfaLine { Y = y, H = kid.H };
            line.Items.Add(kid);
            box.Lines.Add(line);
            y += kid.H;
            widest = Math.Max(widest, kid.W);
        }
        return (widest, y);
    }

    private XfaBox? BuildRow(XfaFormNode row, List<double> widths, int depth)
    {
        var e = row.Element;
        if (e.TakesNoSpace())
            return null;
        _budget.CountBox();

        var margin = XfaInsets.From(e.Child("margin"));
        var box = new XfaBox(row)
        {
            Margin = margin,
            Invisible = e.Presence() == "invisible",
            BreakBefore = ReadBreak(e, before: true),
            BreakAfter = ReadBreak(e, before: false),
        };

        var line = new XfaLine();
        int col = 0;
        double x = 0;
        foreach (var cell in row.Children)
        {
            if (cell.Element.TakesNoSpace())
                continue;
            int span = cell.Element.IntAttr("colSpan", 1);
            int end = span < 0 ? widths.Count : Math.Min(widths.Count, col + Math.Max(1, span));
            double width = 0;
            for (int i = col; i < end; i++)
                width += widths[i];
            if (end <= col)
                _report.Note("table cell beyond the declared columns");

            var kid = Build(cell, width, depth + 1, forcedWidth: end > col ? width : null);
            col = Math.Max(end, col + 1);
            if (kid == null)
                continue;
            kid.X = x;
            kid.Y = 0;
            x += kid.W;
            line.Items.Add(kid);
        }

        double height = line.Items.Count == 0 ? 0 : line.Items.Max(i => i.H);
        var fixedH = e.Measure("h");
        var minH = e.Measure("minH") ?? 0;
        height = fixedH ?? Math.Max(height, minH - margin.Vertical);
        foreach (var item in line.Items)
            item.H = height;   // cells stretch to the row height
        line.H = height;
        if (line.Items.Count > 0)
            box.Lines.Add(line);

        box.W = x + margin.Horizontal;
        box.H = fixedH ?? height + margin.Vertical;
        box.Splittable = false;
        return box;
    }

    /// <summary>XFA 3.x <c>breakBefore</c>/<c>breakAfter</c>, or 2.x <c>break</c>.</summary>
    private static XfaBreak? ReadBreak(XElement container, bool before)
    {
        var modern = container.ChildrenNamed(before ? "breakBefore" : "breakAfter").FirstOrDefault();
        if (modern != null)
        {
            var type = modern.AttrOr("targetType", "auto");
            return type == "auto" ? null : new XfaBreak(type, modern.Attr("target"), modern.AttrOr("startNew", "0") == "1");
        }

        var legacy = container.Child("break");
        if (legacy == null)
            return null;
        var kind = legacy.AttrOr(before ? "before" : "after", "auto");
        return kind == "auto"
            ? null
            : new XfaBreak(kind, legacy.Attr(before ? "beforeTarget" : "afterTarget"), legacy.AttrOr("startNew", "0") == "1");
    }
}
