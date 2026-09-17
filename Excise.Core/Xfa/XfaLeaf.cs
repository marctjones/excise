using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>A rectangle in page space: top-left origin, points.</summary>
internal readonly record struct XfaRect(double X, double Y, double W, double H)
{
    public double Right => X + W;

    public double Bottom => Y + H;

    public XfaRect Deflate(XfaInsets insets)
        => new(X + insets.Left, Y + insets.Top,
            Math.Max(0, W - insets.Horizontal), Math.Max(0, H - insets.Vertical));

    public XfaRect Offset(double dx, double dy) => new(X + dx, Y + dy, W, H);
}

/// <summary>A resolved <c>&lt;caption&gt;</c>.</summary>
internal sealed record XfaCaptionSpec(
    string Placement,
    double? Reserve,
    bool Invisible,
    XfaFontSpec Font,
    XfaParaSpec Para,
    XfaInsets Margin,
    IReadOnlyList<XfaParagraph> Paragraphs);

/// <summary>Where a leaf's caption and widget sit inside its box.</summary>
internal readonly record struct XfaLeafRegions(XfaRect Content, XfaRect? Caption, XfaRect Ui);

/// <summary>
/// A field or draw, resolved once: styles, caption, widget kind and display
/// text. Measurement (layout) and drawing (emission) both use it, so they
/// cannot disagree.
/// </summary>
internal sealed class XfaLeaf
{
    private XfaLeaf(XfaFormNode node)
    {
        Node = node;
        Element = node.Element;
    }

    public XfaFormNode Node { get; }

    public XElement Element { get; }

    public XfaInsets Margin { get; private init; }

    public XfaFontSpec Font { get; private init; } = XfaFontSpec.Default;

    public XfaParaSpec Para { get; private init; } = XfaParaSpec.Default;

    /// <summary>The widget element inside <c>&lt;ui&gt;</c> (<c>textEdit</c>, <c>checkButton</c>...).</summary>
    public XElement? Widget { get; private init; }

    public string WidgetKind { get; private init; } = "textEdit";

    public XfaInsets WidgetMargin { get; private init; }

    public XfaCaptionSpec? Caption { get; private init; }

    public bool Wrap { get; private init; }

    /// <summary>The text the widget shows, as paragraphs.</summary>
    public IReadOnlyList<XfaParagraph> Paragraphs { get; private init; } = Array.Empty<XfaParagraph>();

    /// <summary>A draw's shape value (<c>rectangle</c>, <c>line</c>, <c>arc</c>).</summary>
    public XElement? Shape { get; private init; }

    public double CheckSize { get; private init; }

    /// <summary>For list boxes: every item's display text, and which are selected.</summary>
    public IReadOnlyList<(string Text, bool Selected)> ListItems { get; private init; } = Array.Empty<(string, bool)>();

    /// <summary>Comb cell count for a comb text field; 0 otherwise.</summary>
    public int CombCells { get; private init; }

    public bool IsField => Node.Kind == XfaNodeKind.Field;

    public static XfaLeaf From(XfaFormNode node, XfaReport report)
    {
        var e = node.Element;
        var font = XfaFontSpec.From(e.Child("font"));
        var para = XfaParaSpec.From(e.Child("para"));
        var widget = e.Child("ui")?.Elements().FirstOrDefault(w => w.Name.LocalName is not ("extras" or "picture"));
        var kind = widget?.Name.LocalName ?? "textEdit";
        bool isField = node.Kind == XfaNodeKind.Field;

        var paragraphs = new List<XfaParagraph>();
        var listItems = new List<(string, bool)>();
        XElement? shape = null;

        switch (kind)
        {
            case "checkButton":
            case "button":
            case "signature":
                break;

            case "imageEdit":
                report.Note("image field content not drawn");
                break;

            case "barcode":
                report.Note("barcodes not drawn");
                break;

            case "passwordEdit":
            {
                var mask = widget.AttrOr("passwordChar", "*");
                var length = node.Value?.Length ?? 0;
                if (length > 0 && mask.Length > 0)
                    paragraphs = XfaText.PlainParagraphs(string.Concat(Enumerable.Repeat(mask[..1], Math.Min(length, 256))), font);
                break;
            }

            case "choiceList":
            {
                var (display, selected) = ChoiceDisplay(e, node.Value);
                var open = widget.AttrOr("open", "userControl");
                if (open is "always" or "multiSelect")
                {
                    var values = SelectedValues(node.Value);
                    listItems = display.Select((text, i) => (text, values.Contains(SaveValue(e, i, text)))).ToList();
                }
                else if (selected != null)
                {
                    paragraphs = XfaText.PlainParagraphs(selected, font);
                }
                break;
            }

            default:
                if (node.RichValue != null)
                    paragraphs = XfaRichText.Paragraphs(node.RichValue, font, null);
                else
                    paragraphs = XfaText.PlainParagraphs(node.Value, font);

                if (!isField)
                {
                    shape = e.Child("value")?.Elements()
                        .FirstOrDefault(v => v.Name.LocalName is "rectangle" or "line" or "arc");
                    if (e.Child("value")?.Child("image") != null)
                        report.Note("images not drawn");
                }
                break;
        }

        bool wrap = kind switch
        {
            "textEdit" => widget == null
                ? !isField
                : widget.AttrOr("multiLine", isField ? "0" : "1") == "1",
            _ => false,
        };
        if (node.RichValue != null && kind == "textEdit" && widget?.Attr("multiLine") == null)
            wrap = true;

        int comb = 0;
        if (kind == "textEdit" && widget?.Child("comb") is { } combElement)
            comb = Math.Clamp(combElement.IntAttr("numberOfCells", 0), 0, 1000);

        return new XfaLeaf(node)
        {
            Margin = XfaInsets.From(e.Child("margin")),
            Font = font,
            Para = para,
            Widget = widget,
            WidgetKind = kind,
            WidgetMargin = XfaInsets.From(widget?.Child("margin")),
            Caption = CaptionFrom(e.Child("caption"), font, para, isButton: kind == "button"),
            Wrap = wrap,
            Paragraphs = paragraphs,
            Shape = shape,
            CheckSize = Math.Clamp(widget.Measure("size", "pt") ?? 10, 1, 200),
            ListItems = listItems,
            CombCells = comb,
        };
    }

    private static XfaCaptionSpec? CaptionFrom(XElement? caption, XfaFontSpec fieldFont, XfaParaSpec fieldPara, bool isButton)
    {
        if (caption == null || caption.TakesNoSpace())
            return null;

        var font = caption.Child("font") is { } f ? XfaFontSpec.From(f) : fieldFont;
        var value = caption.Child("value");
        List<XfaParagraph> paragraphs = new();
        if (value != null)
        {
            var exData = value.Child("exData");
            if (exData != null && XfaRichText.FindXhtmlBody(exData) is { } body)
                paragraphs = XfaRichText.Paragraphs(body, font, null);
            else if (value.Child("text") is { } text)
                paragraphs = XfaText.PlainParagraphs(text.Value, font);
            else if (exData != null)
                paragraphs = XfaText.PlainParagraphs(exData.Value, font);
        }

        var reserve = caption.Measure("reserve");
        return new XfaCaptionSpec(
            caption.AttrOr("placement", "left"),
            reserve > 0 ? reserve : null,
            caption.Presence() == "invisible",
            font,
            caption.Child("para") is { } p ? XfaParaSpec.From(p)
                // A button's label is centred on the button unless the template says otherwise.
                : isButton && IsDefaultPara(fieldPara) ? XfaParaSpec.Default with { HAlign = "center", VAlign = "middle" }
                : fieldPara,
            XfaInsets.From(caption.Child("margin")),
            paragraphs);
    }

    private static bool IsDefaultPara(XfaParaSpec para) => ReferenceEquals(para, XfaParaSpec.Default);

    private static (List<string> Display, string? Selected) ChoiceDisplay(XElement field, string? value)
    {
        var itemLists = field.ChildrenNamed("items").ToList();
        var displayItems = itemLists.FirstOrDefault(i => i.Attr("save") != "1") ?? itemLists.FirstOrDefault();
        var saveItems = itemLists.FirstOrDefault(i => i.Attr("save") == "1");
        var display = XfaValues.ItemTexts(displayItems);
        var save = saveItems != null ? XfaValues.ItemTexts(saveItems) : display;

        if (string.IsNullOrEmpty(value))
            return (display, null);

        var index = save.IndexOf(value);
        return (display, index >= 0 && index < display.Count ? display[index] : value);
    }

    private static string SaveValue(XElement field, int index, string display)
    {
        var saveItems = field.ChildrenNamed("items").FirstOrDefault(i => i.Attr("save") == "1");
        var save = XfaValues.ItemTexts(saveItems);
        return index < save.Count ? save[index] : display;
    }

    private static HashSet<string> SelectedValues(string? value)
        => string.IsNullOrEmpty(value)
            ? new HashSet<string>(StringComparer.Ordinal)
            : value.Replace("\r\n", "\n").Split('\n').ToHashSet(StringComparer.Ordinal);

    private XfaTextBlock ValueBlock(double width, bool wrap, XfaBudget budget)
    {
        if (ListItems.Count > 0)
        {
            var paragraphs = ListItems.Select(i => XfaText.PlainParagraphs(i.Text.Length == 0 ? " " : i.Text, Font)[0]).ToList();
            return XfaText.Layout(paragraphs, width, false, XfaParaSpec.Default, Font, budget);
        }
        return XfaText.Layout(Paragraphs, width, wrap, Para, Font, budget);
    }

    public XfaTextBlock LayoutValue(double width, XfaBudget budget) => ValueBlock(width, Wrap, budget);

    public XfaTextBlock LayoutCaption(double width, bool wrap, XfaBudget budget)
        => Caption == null
            ? XfaTextBlock.Empty
            : XfaText.Layout(Caption.Paragraphs, width, wrap, Caption.Para, Caption.Font, budget);

    private bool CaptionIsSide => Caption != null && Caption.Placement is "left" or "right" or "inline";

    private bool CaptionIsStacked => Caption != null && Caption.Placement is "top" or "bottom";

    /// <summary>The natural (or fixed) size of this leaf.</summary>
    public (double W, double H) Measure(double? forcedWidth, double available, XfaBudget budget)
    {
        var minW = Math.Max(0, Element.Measure("minW") ?? 0);
        var maxW = Element.Measure("maxW");
        var minH = Math.Max(0, Element.Measure("minH") ?? 0);
        var maxH = Element.Measure("maxH");

        double? captionReserve = null;
        if (Caption != null)
        {
            if (Caption.Reserve is { } r)
            {
                captionReserve = r;
            }
            else if (CaptionIsSide)
            {
                captionReserve = LayoutCaption(0, false, budget).Width + Caption.Margin.Horizontal;
            }
        }

        double w;
        if ((forcedWidth ?? Element.Measure("w")) is { } fixedW)
        {
            w = Math.Max(0, fixedW);
        }
        else
        {
            double content = WidgetKind == "checkButton"
                ? CheckSize
                : ValueBlock(0, false, budget).Width;
            double natural = content + WidgetMargin.Horizontal + Margin.Horizontal
                + (CaptionIsSide ? captionReserve ?? 0 : 0);
            if (CaptionIsStacked)
                natural = Math.Max(natural, LayoutCaption(0, false, budget).Width + Caption!.Margin.Horizontal + Margin.Horizontal);
            w = Math.Max(minW, maxW is { } mw && mw > 0 ? Math.Min(natural, mw) : natural);
            if (Wrap && available > 0 && w > available)
                w = Math.Max(minW, available);
        }

        if (Element.Measure("h") is { } fixedH)
            return (w, Math.Max(0, fixedH));

        double contentWidth = w - Margin.Horizontal;
        double sideReserve = CaptionIsSide ? captionReserve ?? 0 : 0;
        double valueWidth = Math.Max(0, contentWidth - sideReserve - WidgetMargin.Horizontal);

        double valueHeight;
        if (WidgetKind == "checkButton")
        {
            valueHeight = CheckSize;
        }
        else
        {
            valueHeight = ValueBlock(valueWidth, Wrap, budget).Height;
            if (valueHeight == 0 && IsField)
                valueHeight = Font.LineHeight;
        }

        double body = valueHeight + WidgetMargin.Vertical;
        if (CaptionIsSide)
        {
            var cap = LayoutCaption(Math.Max(0, sideReserve - Caption!.Margin.Horizontal), true, budget);
            body = Math.Max(body, cap.Height + Caption.Margin.Vertical);
        }
        else if (CaptionIsStacked)
        {
            body += captionReserve
                ?? LayoutCaption(Math.Max(0, contentWidth - Caption!.Margin.Horizontal), true, budget).Height
                   + Caption!.Margin.Vertical;
        }

        double h = body + Margin.Vertical;
        h = Math.Max(minH, maxH is { } mh && mh > 0 ? Math.Min(h, mh) : h);
        return (w, h);
    }

    /// <summary>Split a placed box into caption and widget regions.</summary>
    public XfaLeafRegions Regions(XfaRect box, XfaBudget budget)
    {
        var content = box.Deflate(Margin);
        if (Caption == null)
            return new XfaLeafRegions(content, null, content);

        // A push button's caption is its face: it covers the whole widget.
        if (WidgetKind == "button")
            return new XfaLeafRegions(content, content, content);

        double reserve;
        if (Caption.Reserve is { } r)
            reserve = r;
        else if (CaptionIsSide)
            reserve = LayoutCaption(0, false, budget).Width + Caption.Margin.Horizontal;
        else
            reserve = LayoutCaption(Math.Max(0, content.W - Caption.Margin.Horizontal), true, budget).Height
                + Caption.Margin.Vertical;

        reserve = Math.Max(0, reserve);
        switch (Caption.Placement)
        {
            case "right":
            {
                reserve = Math.Min(reserve, content.W);
                var cap = new XfaRect(content.Right - reserve, content.Y, reserve, content.H);
                return new XfaLeafRegions(content, cap, content with { W = content.W - reserve });
            }
            case "top":
            {
                reserve = Math.Min(reserve, content.H);
                var cap = content with { H = reserve };
                return new XfaLeafRegions(content, cap, new XfaRect(content.X, content.Y + reserve, content.W, content.H - reserve));
            }
            case "bottom":
            {
                reserve = Math.Min(reserve, content.H);
                var cap = new XfaRect(content.X, content.Bottom - reserve, content.W, reserve);
                return new XfaLeafRegions(content, cap, content with { H = content.H - reserve });
            }
            default:
            {
                reserve = Math.Min(reserve, content.W);
                var cap = content with { W = reserve };
                return new XfaLeafRegions(content, cap, new XfaRect(content.X + reserve, content.Y, content.W - reserve, content.H));
            }
        }
    }
}
