using System.Text;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// Turns laid-out pages into ordinary PDF pages: base-14 text, paths and
/// fills in one content stream per page. The existing renderer, extractor,
/// search and redaction read these pages like any others.
/// </summary>
internal sealed class XfaPdfWriter
{
    private readonly PdfDocument _document;
    private readonly XfaBudget _budget;
    private readonly XfaReport _report;

    public XfaPdfWriter(PdfDocument document, XfaBudget budget, XfaReport report)
    {
        _document = document;
        _budget = budget;
        _report = report;
    }

    /// <summary>Append one PDF page per laid-out page; returns the new pages.</summary>
    public List<PdfPage> Write(IReadOnlyList<XfaPage> pages)
    {
        var result = new List<PdfPage>(pages.Count);
        foreach (var page in pages)
        {
            var pdfPage = _document.Pages.AddBlank(page.Area.Width, page.Area.Height);
            var content = new PageContent(pdfPage, page.Area.Height);
            foreach (var paint in page.Paints)
            {
                _budget.Tick();
                if (paint.DecorationOnly)
                    DrawBorder(content, paint.Rect, XfaBorderSpec.From(paint.Box.Node.Element.Child("border")));
                else if (paint.Box.Leaf is { } leaf)
                    DrawLeaf(content, leaf, paint.Rect);
            }

            pdfPage.SetContentStreamBytes(Encoding.Latin1.GetBytes(content.ToString()));
            result.Add(pdfPage);
        }
        return result;
    }

    private void DrawLeaf(PageContent content, XfaLeaf leaf, XfaRect rect)
    {
        DrawBorder(content, rect, XfaBorderSpec.From(leaf.Element.Child("border")));

        var regions = leaf.Regions(rect, _budget);

        content.SaveAndClip(rect);

        if (leaf.Caption is { } caption && regions.Caption is { } captionRect && !caption.Invisible)
        {
            var area = captionRect.Deflate(caption.Margin);
            var block = XfaText.Layout(caption.Paragraphs, area.W, true, caption.Para, caption.Font, _budget);
            DrawTextBlock(content, block, area, caption.Para);
        }

        var ui = regions.Ui;
        if (leaf.Widget != null)
            DrawBorder(content, ui, XfaBorderSpec.From(leaf.Widget.Child("border")));
        var inner = ui.Deflate(leaf.WidgetMargin);

        switch (leaf.WidgetKind)
        {
            case "checkButton":
                DrawCheckButton(content, leaf, inner);
                break;

            case "button":
            case "signature":
            case "imageEdit":
            case "barcode":
                break;

            default:
                if (leaf.Shape != null)
                {
                    DrawShape(content, leaf.Shape, inner);
                }
                else if (leaf.ListItems.Count > 0)
                {
                    DrawList(content, leaf, inner);
                }
                else if (leaf.CombCells > 0)
                {
                    DrawComb(content, leaf, inner);
                }
                else
                {
                    var block = leaf.LayoutValue(inner.W, _budget);
                    DrawTextBlock(content, block, inner, leaf.Para);
                }
                break;
        }

        content.Restore();
    }

    private void DrawTextBlock(PageContent content, XfaTextBlock block, XfaRect area, XfaParaSpec para)
    {
        if (block.Lines.Count == 0)
            return;

        double y = area.Y + para.SpaceAbove;
        double free = area.H - block.Height;
        if (para.VAlign == "middle")
            y += free / 2;
        else if (para.VAlign == "bottom")
            y += free;

        foreach (var line in block.Lines)
        {
            _budget.Tick();
            double left = area.X + para.MarginLeft;
            double width = area.W - para.MarginLeft - para.MarginRight;
            double start = line.HAlign switch
            {
                "center" => left + (width - line.Width) / 2,
                "right" => left + width - line.Width,
                _ => left,
            };

            // The baseline sits where a 1.2 line box puts it: half-leading plus
            // an ascent of 0.8 em (the value PdfFont uses for base-14 fonts).
            double baseline = y + (line.Height - line.MaxSize * 1.2) / 2 + line.MaxSize * 0.9;
            foreach (var run in line.Runs)
            {
                content.Text(run.Text, run.Font, start + run.X, baseline, _report);
                if (run.Font.Underline)
                    content.Line(start + run.X, baseline + run.Font.Size * 0.12, start + run.X + run.Width,
                        baseline + run.Font.Size * 0.12, Math.Max(0.5, run.Font.Size / 20), run.Font.Color, null);
            }
            y += line.Height;
        }
    }

    private void DrawComb(PageContent content, XfaLeaf leaf, XfaRect area)
    {
        var text = string.Concat(leaf.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Text));
        if (text.Length == 0)
            return;
        double cell = area.W / leaf.CombCells;
        double baseline = area.Y + (area.H - leaf.Font.Size * 1.2) / 2 + leaf.Font.Size * 0.9;
        for (int i = 0; i < Math.Min(text.Length, leaf.CombCells); i++)
        {
            var ch = text[i].ToString();
            double w = XfaText.Measure(ch, leaf.Font);
            content.Text(ch, leaf.Font, area.X + i * cell + (cell - w) / 2, baseline, _report);
        }
    }

    private void DrawList(PageContent content, XfaLeaf leaf, XfaRect area)
    {
        double y = area.Y;
        double lineHeight = leaf.Font.LineHeight;
        foreach (var (text, selected) in leaf.ListItems)
        {
            _budget.Tick();
            if (y >= area.Bottom)
                break;
            if (selected)
                content.Fill(new XfaRect(area.X, y, area.W, lineHeight), 0, new PdfColor(0.8, 0.85, 1.0));
            content.Text(text, leaf.Font, area.X + 1, y + (lineHeight - leaf.Font.Size * 1.2) / 2 + leaf.Font.Size * 0.9, _report);
            y += lineHeight;
        }
    }

    private void DrawCheckButton(PageContent content, XfaLeaf leaf, XfaRect area)
    {
        var widget = leaf.Widget!;
        double size = Math.Min(leaf.CheckSize, Math.Max(1, Math.Min(area.W, area.H)));
        var box = new XfaRect(area.X, area.Y + (area.H - size) / 2, size, size);
        bool round = widget.AttrOr("shape", "square") == "round";

        if (widget.Child("border") == null)
        {
            if (round)
                content.Ellipse(box, 0.5, PdfColor.Black, null);
            else
                content.StrokeRect(box, 0.5, PdfColor.Black, null);
        }

        bool on = leaf.Node.Value != null && leaf.Node.Value == XfaValues.OnValue(leaf.Element);
        if (!on)
            return;

        var mark = widget.AttrOr("mark", "default");
        if (mark == "default")
            mark = round ? "circle" : "check";
        var color = leaf.Font.Color;
        var inset = new XfaRect(box.X + size * 0.2, box.Y + size * 0.2, size * 0.6, size * 0.6);
        switch (mark)
        {
            case "circle":
                content.Ellipse(inset, 0, color, color);
                break;
            case "square":
                content.Fill(inset, 0, color);
                break;
            case "diamond":
                content.Polygon(new[]
                {
                    (inset.X + inset.W / 2, inset.Y), (inset.Right, inset.Y + inset.H / 2),
                    (inset.X + inset.W / 2, inset.Bottom), (inset.X, inset.Y + inset.H / 2),
                }, color);
                break;
            default:
            {
                var glyph = mark switch { "cross" => "8", "star" => "H", _ => "4" };
                var font = new XfaFontSpec("ZapfDingbats", size * 0.8, false, false, false, color);
                double w = XfaText.Measure(glyph, font);
                content.Text(glyph, font, box.X + (size - w) / 2, box.Y + size * 0.78, _report);
                break;
            }
        }
    }

    private void DrawShape(PageContent content, XElement shape, XfaRect area)
    {
        var edge = shape.ChildrenNamed("edge").FirstOrDefault();
        bool visible = edge == null || edge.Presence() == "visible";
        double thickness = Math.Clamp(edge.Measure("thickness") ?? 0.5, 0, 100);
        var color = XfaColor.Of(edge, PdfColor.Black);
        string? dash = DashFor(edge.AttrOr("stroke", "solid"), thickness);

        switch (shape.Name.LocalName)
        {
            case "line":
            {
                if (!visible || thickness <= 0)
                    return;
                if (area.W < 0.01 || area.H < 0.01)
                    content.Line(area.X, area.Y, area.Right, area.Bottom, thickness, color, dash);
                else if (shape.AttrOr("slope", "\\") == "/")
                    content.Line(area.X, area.Bottom, area.Right, area.Y, thickness, color, dash);
                else
                    content.Line(area.X, area.Y, area.Right, area.Bottom, thickness, color, dash);
                break;
            }

            case "arc":
            {
                var fillElement = shape.Child("fill");
                PdfColor? fill = fillElement != null && fillElement.Presence() == "visible"
                    ? XfaColor.Of(fillElement, PdfColor.White)
                    : null;
                if (fill != null && XfaBorderSpec.IsApproximatedFill(fillElement))
                    _report.Note("gradient and pattern fills drawn as their base colour");
                var bounds = area;
                if (shape.AttrOr("circular", "0") == "1")
                {
                    double d = Math.Min(area.W, area.H);
                    bounds = new XfaRect(area.X + (area.W - d) / 2, area.Y + (area.H - d) / 2, d, d);
                }
                double start = double.TryParse(shape.Attr("startAngle"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
                double sweep = double.TryParse(shape.Attr("sweepAngle"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sw) ? sw : 360;
                if (Math.Abs(sweep) >= 360)
                    content.Ellipse(bounds, visible ? thickness : 0, color, fill);
                else
                    content.Arc(bounds, start, sweep, visible ? thickness : 0, color);
                break;
            }

            default:
                DrawBorder(content, area, XfaBorderSpec.From(shape));
                break;
        }
    }

    private static string? DashFor(string stroke, double thickness)
    {
        var t = PdfNumberFormatter.Format(Math.Max(thickness, 0.5));
        var t3 = PdfNumberFormatter.Format(Math.Max(thickness, 0.5) * 3);
        return stroke switch
        {
            "dashed" => $"[{t3} {t3}] 0",
            "dotted" => $"[{t} {t}] 0",
            "dashDot" => $"[{t3} {t} {t} {t}] 0",
            "dashDotDot" => $"[{t3} {t} {t} {t} {t} {t}] 0",
            _ => null,
        };
    }

    private void DrawBorder(PageContent content, XfaRect rect, XfaBorderSpec? border)
    {
        if (border == null)
            return;
        if (border.FillApproximated)
            _report.Note("gradient and pattern fills drawn as their base colour");

        if (border.Fill is { } fill)
            content.Fill(rect, border.Radius, fill);

        if (!border.HasVisibleEdge)
            return;

        var edges = border.Edges;
        bool uniform = edges.All(e => e == edges[0]);
        if (uniform)
        {
            var e = edges[0];
            double offset = border.Hand switch { "right" => e.Thickness / 2, "left" => -e.Thickness / 2, _ => 0 };
            var r = new XfaRect(rect.X + offset, rect.Y + offset, Math.Max(0, rect.W - 2 * offset), Math.Max(0, rect.H - 2 * offset));
            if (border.Radius > 0)
                content.RoundedRect(r, border.Radius, e.Thickness, e.Color, DashFor(e.Stroke, e.Thickness));
            else
                content.StrokeRect(r, e.Thickness, e.Color, DashFor(e.Stroke, e.Thickness));
            return;
        }

        // top, right, bottom, left
        void Side(XfaEdgeSpec e, double x1, double y1, double x2, double y2)
        {
            if (e.Visible && e.Thickness > 0)
                content.Line(x1, y1, x2, y2, e.Thickness, e.Color, DashFor(e.Stroke, e.Thickness));
        }

        double Off(XfaEdgeSpec e) => border.Hand switch { "right" => e.Thickness / 2, "left" => -e.Thickness / 2, _ => 0 };
        Side(edges[0], rect.X, rect.Y + Off(edges[0]), rect.Right, rect.Y + Off(edges[0]));
        Side(edges[1], rect.Right - Off(edges[1]), rect.Y, rect.Right - Off(edges[1]), rect.Bottom);
        Side(edges[2], rect.X, rect.Bottom - Off(edges[2]), rect.Right, rect.Bottom - Off(edges[2]));
        Side(edges[3], rect.X + Off(edges[3]), rect.Y, rect.X + Off(edges[3]), rect.Bottom);
    }

    /// <summary>
    /// A page's content stream under construction. Callers use top-left page
    /// coordinates; this class flips them to PDF's bottom-left space.
    /// </summary>
    private sealed class PageContent
    {
        private const double Kappa = 0.5522847498;

        private readonly StringBuilder _sb = new();
        private readonly PdfPage _page;
        private readonly double _pageHeight;
        private readonly Dictionary<string, string> _fontNames = new(StringComparer.Ordinal);

        public PageContent(PdfPage page, double pageHeight)
        {
            _page = page;
            _pageHeight = pageHeight;
        }

        public override string ToString() => _sb.ToString();

        private static string F(double v) => PdfNumberFormatter.Format(v);

        private double Y(double top) => _pageHeight - top;

        private void Emit(string line) => _sb.Append(line).Append('\n');

        private static string Rgb(PdfColor c) => $"{F(c.R)} {F(c.G)} {F(c.B)}";

        public void SaveAndClip(XfaRect r)
        {
            Emit("q");
            Emit($"{F(r.X)} {F(Y(r.Bottom))} {F(r.W)} {F(r.H)} re W n");
        }

        public void Restore() => Emit("Q");

        public void Text(string text, XfaFontSpec spec, double x, double baseline, XfaReport report)
        {
            if (string.IsNullOrEmpty(text) || spec.Size <= 0)
                return;

            var font = spec.ToPdfFont();
            if (!font.CanEncodeFully(text))
                report.Note("text outside the WinAnsi character set shown as '?'");

            if (!_fontNames.TryGetValue(font.BaseFont, out var name))
            {
                name = _page.AddFont(font);
                _fontNames[font.BaseFont] = name;
            }

            Emit("BT");
            Emit($"/{name} {F(spec.Size)} Tf");
            // Tz is text state and survives ET, so every run sets its own.
            Emit($"{F(spec.WidthScale * 100)} Tz");
            Emit($"{Rgb(spec.Color)} rg");
            Emit($"{F(x)} {F(Y(baseline))} Td");
            Emit($"{font.EncodeString(text)} Tj");
            Emit("ET");
        }

        public void Line(double x1, double y1, double x2, double y2, double width, PdfColor color, string? dash)
        {
            Emit("q");
            Emit($"{F(width)} w {Rgb(color)} RG");
            if (dash != null)
                Emit($"{dash} d");
            Emit($"{F(x1)} {F(Y(y1))} m {F(x2)} {F(Y(y2))} l S");
            Emit("Q");
        }

        public void StrokeRect(XfaRect r, double width, PdfColor color, string? dash)
        {
            if (width <= 0)
                return;
            Emit("q");
            Emit($"{F(width)} w {Rgb(color)} RG");
            if (dash != null)
                Emit($"{dash} d");
            Emit($"{F(r.X)} {F(Y(r.Bottom))} {F(r.W)} {F(r.H)} re S");
            Emit("Q");
        }

        public void Fill(XfaRect r, double radius, PdfColor color)
        {
            Emit("q");
            Emit($"{Rgb(color)} rg");
            if (radius > 0)
                RoundedPath(r, radius);
            else
                Emit($"{F(r.X)} {F(Y(r.Bottom))} {F(r.W)} {F(r.H)} re");
            Emit("f");
            Emit("Q");
        }

        public void RoundedRect(XfaRect r, double radius, double width, PdfColor color, string? dash)
        {
            Emit("q");
            Emit($"{F(width)} w {Rgb(color)} RG");
            if (dash != null)
                Emit($"{dash} d");
            RoundedPath(r, radius);
            Emit("S");
            Emit("Q");
        }

        private void RoundedPath(XfaRect r, double radius)
        {
            radius = Math.Min(radius, Math.Min(r.W, r.H) / 2);
            double k = radius * Kappa;
            double x0 = r.X, x1 = r.Right, y0 = Y(r.Y), y1 = Y(r.Bottom);
            Emit($"{F(x0 + radius)} {F(y0)} m");
            Emit($"{F(x1 - radius)} {F(y0)} l");
            Emit($"{F(x1 - radius + k)} {F(y0)} {F(x1)} {F(y0 - radius + k)} {F(x1)} {F(y0 - radius)} c");
            Emit($"{F(x1)} {F(y1 + radius)} l");
            Emit($"{F(x1)} {F(y1 + radius - k)} {F(x1 - radius + k)} {F(y1)} {F(x1 - radius)} {F(y1)} c");
            Emit($"{F(x0 + radius)} {F(y1)} l");
            Emit($"{F(x0 + radius - k)} {F(y1)} {F(x0)} {F(y1 + radius - k)} {F(x0)} {F(y1 + radius)} c");
            Emit($"{F(x0)} {F(y0 - radius)} l");
            Emit($"{F(x0)} {F(y0 - radius + k)} {F(x0 + radius - k)} {F(y0)} {F(x0 + radius)} {F(y0)} c");
            Emit("h");
        }

        public void Ellipse(XfaRect r, double width, PdfColor stroke, PdfColor? fill)
        {
            if (width <= 0 && fill == null)
                return;
            double cx = r.X + r.W / 2, cy = Y(r.Y + r.H / 2);
            double rx = r.W / 2, ry = r.H / 2, kx = rx * Kappa, ky = ry * Kappa;
            Emit("q");
            if (width > 0)
                Emit($"{F(width)} w {Rgb(stroke)} RG");
            if (fill is { } f)
                Emit($"{Rgb(f)} rg");
            Emit($"{F(cx + rx)} {F(cy)} m");
            Emit($"{F(cx + rx)} {F(cy + ky)} {F(cx + kx)} {F(cy + ry)} {F(cx)} {F(cy + ry)} c");
            Emit($"{F(cx - kx)} {F(cy + ry)} {F(cx - rx)} {F(cy + ky)} {F(cx - rx)} {F(cy)} c");
            Emit($"{F(cx - rx)} {F(cy - ky)} {F(cx - kx)} {F(cy - ry)} {F(cx)} {F(cy - ry)} c");
            Emit($"{F(cx + kx)} {F(cy - ry)} {F(cx + rx)} {F(cy - ky)} {F(cx + rx)} {F(cy)} c");
            Emit(fill != null && width > 0 ? "b" : fill != null ? "f" : "S");
            Emit("Q");
        }

        /// <summary>An elliptical arc; angles in degrees, counter-clockwise from 3 o'clock.</summary>
        public void Arc(XfaRect r, double startDegrees, double sweepDegrees, double width, PdfColor color)
        {
            if (width <= 0 || sweepDegrees == 0)
                return;
            double cx = r.X + r.W / 2, cy = Y(r.Y + r.H / 2);
            double rx = r.W / 2, ry = r.H / 2;
            int segments = (int)Math.Ceiling(Math.Abs(sweepDegrees) / 90);
            double step = sweepDegrees / segments * Math.PI / 180;
            double a = startDegrees * Math.PI / 180;

            Emit("q");
            Emit($"{F(width)} w {Rgb(color)} RG");
            Emit($"{F(cx + rx * Math.Cos(a))} {F(cy + ry * Math.Sin(a))} m");
            for (int i = 0; i < segments; i++)
            {
                double b = a + step;
                double k = 4.0 / 3.0 * Math.Tan((b - a) / 4);
                double x1 = cx + rx * (Math.Cos(a) - k * Math.Sin(a));
                double y1 = cy + ry * (Math.Sin(a) + k * Math.Cos(a));
                double x2 = cx + rx * (Math.Cos(b) + k * Math.Sin(b));
                double y2 = cy + ry * (Math.Sin(b) - k * Math.Cos(b));
                Emit($"{F(x1)} {F(y1)} {F(x2)} {F(y2)} {F(cx + rx * Math.Cos(b))} {F(cy + ry * Math.Sin(b))} c");
                a = b;
            }
            Emit("S");
            Emit("Q");
        }

        public void Polygon(IReadOnlyList<(double X, double Y)> points, PdfColor fill)
        {
            if (points.Count < 3)
                return;
            Emit("q");
            Emit($"{Rgb(fill)} rg");
            Emit($"{F(points[0].X)} {F(Y(points[0].Y))} m");
            for (int i = 1; i < points.Count; i++)
                Emit($"{F(points[i].X)} {F(Y(points[i].Y))} l");
            Emit("h f");
            Emit("Q");
        }
    }
}
