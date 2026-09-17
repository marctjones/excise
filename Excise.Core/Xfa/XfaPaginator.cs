using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>
/// Something to draw, at a page position (top-left origin, points).
/// <see cref="DecorationOnly"/> marks a container: only its border and fill
/// are drawn, because its children are painted separately.
/// </summary>
internal readonly record struct XfaPaint(XfaBox Box, XfaRect Rect, bool DecorationOnly);

internal sealed class XfaPage
{
    public XfaPage(XfaPageArea area) => Area = area;

    public XfaPageArea Area { get; }

    public List<XfaPaint> Paints { get; } = new();
}

/// <summary>A resolved <c>&lt;pageArea&gt;</c>.</summary>
internal sealed class XfaPageArea
{
    public required XElement Element { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required IReadOnlyList<XfaRect> ContentAreas { get; init; }

    /// <summary>The page's own fixed content, laid out at the page origin.</summary>
    public XfaBox? Fixed { get; init; }

    /// <summary><c>occur max</c>; -1 for unlimited.</summary>
    public int MaxOccur { get; init; } = -1;

    public int Used { get; set; }

    public bool Matches(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;
        var t = target.Trim().TrimStart('#');
        var dot = t.LastIndexOf('.');
        if (dot >= 0)
            t = t[(dot + 1)..];
        var bracket = t.IndexOf('[');
        if (bracket >= 0)
            t = t[..bracket];
        return t == Element.Attr("id") || t == Element.Attr("name");
    }
}

/// <summary>
/// Phase two of layout: pour the laid-out form into page areas (XFA 3.3,
/// "Layout for Growable Objects" and "Flowing Layout Across Pages").
/// </summary>
internal sealed class XfaPaginator
{
    private const double Epsilon = 0.01;

    private readonly XfaBudget _budget;
    private readonly XfaReport _report;
    private readonly IReadOnlyList<XfaPageArea> _pageAreas;
    private readonly List<XfaPage> _pages = new();
    private readonly List<Fragment> _open = new();

    private XfaPage _page = null!;
    private int _pageAreaIndex;
    private int _contentAreaIndex;
    private double _cursor;
    private bool _areaHasContent;
    private XfaBreak? _pendingBreak;

    private sealed class Fragment
    {
        public required XfaBox Box { get; init; }
        public required double X { get; init; }
        public double Top { get; set; }
        public int PaintIndex { get; set; }
        public List<XfaPaint> Paints { get; set; } = null!;
    }

    public XfaPaginator(IReadOnlyList<XfaPageArea> pageAreas, XfaBudget budget, XfaReport report)
    {
        if (pageAreas.Count == 0)
            throw new XfaLayoutException("The XFA form has no page area.");
        _pageAreas = pageAreas;
        _budget = budget;
        _report = report;
    }

    private XfaRect Area => _page.Area.ContentAreas[_contentAreaIndex];

    private double AreaBottom => Area.Y + Area.H;

    public List<XfaPage> Paginate(XfaBox? root)
    {
        StartPage(0);
        if (root != null)
        {
            if (root.BreakBefore is { } before && before.TargetType is "pageArea" or "pageEven" or "pageOdd"
                && FindPageArea(before.Target) is { } first && first != _pageAreaIndex)
            {
                // A break before the very first content only chooses the first page.
                _pages.Clear();
                _pageAreas[_pageAreaIndex].Used = 0;
                StartPage(first);
            }
            Place(root, Area.X, isRoot: true);
        }
        return _pages;
    }

    private void Place(XfaBox box, double originX, bool isRoot = false)
    {
        _budget.Tick();
        ApplyPending();
        if (!isRoot && box.BreakBefore is { } before)
            Break(before);

        double remaining = AreaBottom - _cursor - OpenBottomMargins();
        bool split = box.Splittable && (box.H > remaining + Epsilon || box.ContainsBreak);
        if (!split)
        {
            if (box.H > remaining + Epsilon && _areaHasContent)
                NextArea();
            if (box.H > Area.H + Epsilon)
                _report.Note("content taller than its page area is clipped");

            EmitWhole(box, originX + box.X, _cursor);
            _cursor += box.H;
            _areaHasContent = true;
        }
        else
        {
            Split(box, originX + box.X);
        }

        if (box.BreakAfter is { } after)
            _pendingBreak = after;
    }

    private void Split(XfaBox box, double x)
    {
        var fragment = new Fragment { Box = box, X = x };
        Open(fragment);
        _cursor += box.Margin.Top;
        double innerX = x + box.Margin.Left;

        foreach (var line in box.Lines)
        {
            _budget.Tick();
            if (line.Items.Count == 1)
            {
                // A single object per line (tb, table): the object places
                // itself, and may split further.
                var item = line.Items[0];
                Place(item, innerX);
                continue;
            }

            ApplyPending();
            double remaining = AreaBottom - _cursor - OpenBottomMargins();
            if (line.H > remaining + Epsilon && _areaHasContent)
                NextArea();

            double top = _cursor;
            foreach (var item in line.Items)
                EmitWhole(item, innerX + item.X, top + (item.Y - line.Y));
            _cursor = top + line.H;
            _areaHasContent = true;
        }

        _cursor += box.Margin.Bottom;
        Close(fragment);
        _open.Remove(fragment);
    }

    private double OpenBottomMargins() => _open.Sum(f => f.Box.Margin.Bottom);

    private void Open(Fragment fragment)
    {
        fragment.Top = _cursor;
        fragment.Paints = _page.Paints;
        fragment.PaintIndex = _page.Paints.Count;
        _open.Add(fragment);
    }

    private void Close(Fragment fragment)
    {
        var height = Math.Max(0, _cursor - fragment.Top);
        if (!fragment.Box.Invisible && !IsInsideInvisible(fragment))
        {
            fragment.Paints.Insert(fragment.PaintIndex,
                new XfaPaint(fragment.Box, new XfaRect(fragment.X, fragment.Top, fragment.Box.W, height), DecorationOnly: true));
            // Later fragments on the same page were opened after this index.
            foreach (var other in _open)
            {
                if (other != fragment && ReferenceEquals(other.Paints, fragment.Paints) && other.PaintIndex >= fragment.PaintIndex)
                    other.PaintIndex++;
            }
        }
    }

    private bool IsInsideInvisible(Fragment fragment)
    {
        foreach (var other in _open)
        {
            if (other == fragment)
                return false;
            if (other.Box.Invisible)
                return true;
        }
        return false;
    }

    private void EmitWhole(XfaBox box, double x, double y)
    {
        if (box.Invisible || _open.Any(f => f.Box.Invisible))
            return;
        EmitTree(box, x, y, depth: 0);
    }

    private void EmitTree(XfaBox box, double x, double y, int depth)
    {
        XfaBudget.CheckDepth(depth);
        _budget.Tick();
        if (box.Invisible)
            return;

        var rect = new XfaRect(x, y, box.W, box.H);
        if (box.Leaf != null)
        {
            _page.Paints.Add(new XfaPaint(box, rect, DecorationOnly: false));
            return;
        }

        _page.Paints.Add(new XfaPaint(box, rect, DecorationOnly: true));
        foreach (var line in box.Lines)
        {
            foreach (var item in line.Items)
                EmitTree(item, x + box.Margin.Left + item.X, y + box.Margin.Top + item.Y, depth + 1);
        }
    }

    private void ApplyPending()
    {
        if (_pendingBreak is not { } pending)
            return;
        _pendingBreak = null;
        Break(pending);
    }

    private void Break(XfaBreak request)
    {
        switch (request.TargetType)
        {
            case "pageArea":
            case "pageEven":
            case "pageOdd":
            {
                var target = FindPageArea(request.Target);
                bool samePage = target == null || target == _pageAreaIndex;
                bool? wantEven = request.TargetType switch
                {
                    "pageEven" => true,
                    "pageOdd" => false,
                    _ => null,
                };
                bool rightParity = wantEven == null || (_pages.Count % 2 == 0) == wantEven;
                if (!PageHasContent() && samePage && !request.StartNew && rightParity)
                    return;
                NewPage(target ?? NextPageAreaIndex());
                // An even/odd break skips a page to land on the right side,
                // leaving a blank page as a printed form does.
                if (wantEven != null && (_pages.Count % 2 == 0) != wantEven)
                    NewPage(target ?? NextPageAreaIndex());
                break;
            }
            case "contentArea":
                if (!_areaHasContent && !request.StartNew)
                    return;
                NextArea();
                break;
        }
    }

    private int? FindPageArea(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        for (int i = 0; i < _pageAreas.Count; i++)
        {
            if (_pageAreas[i].Matches(target))
                return i;
        }
        _report.Note("break target not found");
        return null;
    }

    private bool PageHasContent()
        => _areaHasContent || _contentAreaIndex > 0;

    private void NextArea()
    {
        if (_contentAreaIndex + 1 < _page.Area.ContentAreas.Count)
        {
            CloseOpenFragments();
            _contentAreaIndex++;
            _cursor = Area.Y;
            _areaHasContent = false;
            ReopenFragments();
            return;
        }
        NewPage(NextPageAreaIndex());
    }

    private int NextPageAreaIndex()
    {
        var current = _pageAreas[_pageAreaIndex];
        if (current.MaxOccur >= 0 && current.Used >= current.MaxOccur && _pageAreaIndex + 1 < _pageAreas.Count)
            return _pageAreaIndex + 1;
        return _pageAreaIndex;
    }

    private void NewPage(int pageAreaIndex)
    {
        CloseOpenFragments();
        StartPage(pageAreaIndex);
        ReopenFragments();
    }

    private void StartPage(int pageAreaIndex)
    {
        if (_pages.Count >= XfaBudget.MaxPages)
            throw new XfaLayoutException($"The XFA layout needs more than {XfaBudget.MaxPages} pages.");

        _pageAreaIndex = pageAreaIndex;
        var area = _pageAreas[pageAreaIndex];
        area.Used++;
        _page = new XfaPage(area);
        _pages.Add(_page);
        _contentAreaIndex = 0;
        _cursor = Area.Y;
        _areaHasContent = false;

        if (area.Fixed != null)
            EmitTree(area.Fixed, 0, 0, depth: 0);
    }

    private void CloseOpenFragments()
    {
        foreach (var fragment in _open)
        {
            // Close at the bottom of the area: the fragment continues.
            _cursor = Math.Max(_cursor, fragment.Top);
            Close(fragment);
        }
    }

    private void ReopenFragments()
    {
        foreach (var fragment in _open)
        {
            fragment.Top = _cursor;
            fragment.Paints = _page.Paints;
            fragment.PaintIndex = _page.Paints.Count;
            _cursor += fragment.Box.Margin.Top;
        }
    }
}
