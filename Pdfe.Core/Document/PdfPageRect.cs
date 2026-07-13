namespace Pdfe.Core.Document;

/// <summary>
/// Identifies the coordinate system used by a <see cref="PdfPageRect"/>.
/// </summary>
public enum PdfCoordinateSpace
{
    /// <summary>PDF content-stream points: origin at bottom-left, Y increases upward.</summary>
    ContentPoints = 0,

    /// <summary>Displayed page points after page rotation: origin at top-left, Y increases downward.</summary>
    VisualPoints = 1,

    /// <summary>Single-page viewer DIPs/pixels before zoom transform: origin at top-left, Y increases downward.</summary>
    ViewerDips = 2,

    /// <summary>Continuous reading-view DIPs after layout zoom: origin at top-left, Y increases downward.</summary>
    ContinuousDips = 3,
}

/// <summary>
/// A page-scoped rectangle whose coordinate space is carried with the values.
/// </summary>
public readonly record struct PdfPageRect
{
    /// <summary>PDF points per inch.</summary>
    public const double PdfPointsPerInch = 72.0;

    /// <summary>
    /// Create a page-scoped rectangle.
    /// </summary>
    public PdfPageRect(
        int pageNumber,
        double x,
        double y,
        double width,
        double height,
        PdfCoordinateSpace space,
        double unitsPerPoint = 1.0)
    {
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number is 1-based and must be positive.");
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentException("Rectangle coordinates must be finite.");
        if (width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle dimensions must be non-negative.");
        if (!double.IsFinite(unitsPerPoint) || unitsPerPoint <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsPerPoint), "Coordinate scale must be positive.");

        PageNumber = pageNumber;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Space = space;
        UnitsPerPoint = unitsPerPoint;
    }

    /// <summary>The 1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>Left coordinate in <see cref="Space"/>.</summary>
    public double X { get; }

    /// <summary>
    /// Y coordinate in <see cref="Space"/>. For <see cref="PdfCoordinateSpace.ContentPoints"/>,
    /// this is the lower Y value. For top-left spaces, this is the top Y value.
    /// </summary>
    public double Y { get; }

    /// <summary>Width in <see cref="Space"/> units.</summary>
    public double Width { get; }

    /// <summary>Height in <see cref="Space"/> units.</summary>
    public double Height { get; }

    /// <summary>The coordinate system used by this rectangle.</summary>
    public PdfCoordinateSpace Space { get; }

    /// <summary>Number of this coordinate system's units per PDF point.</summary>
    public double UnitsPerPoint { get; }

    /// <summary>Equivalent DPI implied by <see cref="UnitsPerPoint"/>.</summary>
    public double Dpi => UnitsPerPoint * PdfPointsPerInch;

    /// <summary>Right coordinate in <see cref="Space"/>.</summary>
    public double Right => X + Width;

    /// <summary>Second Y edge in <see cref="Space"/>.</summary>
    public double Y2 => Y + Height;

    /// <summary>Create a content-space rectangle from a PDF rectangle.</summary>
    public static PdfPageRect FromContentPoints(int pageNumber, PdfRectangle rect) =>
        FromPdfRectangle(pageNumber, rect, PdfCoordinateSpace.ContentPoints);

    /// <summary>Create a visual-space rectangle in PDF points.</summary>
    public static PdfPageRect VisualPoints(int pageNumber, double x, double y, double width, double height) =>
        new(pageNumber, x, y, width, height, PdfCoordinateSpace.VisualPoints);

    /// <summary>Create a single-page viewer rectangle from rendered-page DIPs/pixels.</summary>
    public static PdfPageRect ViewerDips(
        int pageNumber,
        double x,
        double y,
        double width,
        double height,
        double renderDpi) =>
        new(pageNumber, x, y, width, height, PdfCoordinateSpace.ViewerDips, renderDpi / PdfPointsPerInch);

    /// <summary>Create a rectangle from a numeric rectangle in the given space.</summary>
    public static PdfPageRect FromPdfRectangle(
        int pageNumber,
        PdfRectangle rect,
        PdfCoordinateSpace space,
        double unitsPerPoint = 1.0)
    {
        var normalized = rect.Normalize();
        return new PdfPageRect(
            pageNumber,
            normalized.Left,
            normalized.Bottom,
            normalized.Right - normalized.Left,
            normalized.Top - normalized.Bottom,
            space,
            unitsPerPoint);
    }

    /// <summary>
    /// Return a numeric rectangle carrying this instance's raw coordinate values.
    /// For top-left spaces, <c>Bottom</c>/<c>Top</c> are just the two Y edges.
    /// </summary>
    public PdfRectangle ToPdfRectangle() => new(X, Y, Right, Y2);

    /// <summary>Return a copy with a different coordinate space and scale.</summary>
    public PdfPageRect WithSpace(PdfCoordinateSpace space, double unitsPerPoint = 1.0) =>
        new(PageNumber, X, Y, Width, Height, space, unitsPerPoint);

    /// <inheritdoc />
    public override string ToString() =>
        $"Page {PageNumber} {Space} ({X:F2},{Y:F2},{Width:F2}x{Height:F2}, scale={UnitsPerPoint:F4})";
}

/// <summary>
/// Converts <see cref="PdfPageRect"/> values between page coordinate spaces.
/// </summary>
public static class PdfCoordinateMapper
{
    /// <summary>Convert a rectangle to content-stream PDF points.</summary>
    public static PdfPageRect ToContentPoints(PdfPage page, PdfPageRect rect)
    {
        EnsurePage(page, rect);
        if (rect.Space == PdfCoordinateSpace.ContentPoints)
            return rect.WithSpace(PdfCoordinateSpace.ContentPoints);

        var visual = ToVisualPoints(page, rect);
        var content = page.ToContentStreamCoordinates(visual.ToPdfRectangle()).Normalize();
        return PdfPageRect.FromPdfRectangle(page.PageNumber, content, PdfCoordinateSpace.ContentPoints);
    }

    /// <summary>Convert a rectangle to displayed-page PDF points.</summary>
    public static PdfPageRect ToVisualPoints(PdfPage page, PdfPageRect rect)
    {
        EnsurePage(page, rect);
        return rect.Space switch
        {
            PdfCoordinateSpace.VisualPoints => rect.WithSpace(PdfCoordinateSpace.VisualPoints),
            PdfCoordinateSpace.ContentPoints => ContentToVisualPoints(page, rect),
            PdfCoordinateSpace.ViewerDips or PdfCoordinateSpace.ContinuousDips =>
                new PdfPageRect(
                    page.PageNumber,
                    rect.X / rect.UnitsPerPoint,
                    rect.Y / rect.UnitsPerPoint,
                    rect.Width / rect.UnitsPerPoint,
                    rect.Height / rect.UnitsPerPoint,
                    PdfCoordinateSpace.VisualPoints),
            _ => throw new ArgumentOutOfRangeException(nameof(rect), rect.Space, "Unsupported coordinate space.")
        };
    }

    /// <summary>Convert a rectangle to single-page viewer DIPs/pixels at <paramref name="renderDpi"/>.</summary>
    public static PdfPageRect ToViewerDips(PdfPage page, PdfPageRect rect, double renderDpi)
    {
        if (!double.IsFinite(renderDpi) || renderDpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderDpi), "Render DPI must be positive.");

        EnsurePage(page, rect);
        var unitsPerPoint = renderDpi / PdfPageRect.PdfPointsPerInch;
        if (rect.Space == PdfCoordinateSpace.ViewerDips &&
            Math.Abs(rect.UnitsPerPoint - unitsPerPoint) < 0.000001)
        {
            return rect;
        }

        var visual = ToVisualPoints(page, rect);
        return new PdfPageRect(
            page.PageNumber,
            visual.X * unitsPerPoint,
            visual.Y * unitsPerPoint,
            visual.Width * unitsPerPoint,
            visual.Height * unitsPerPoint,
            PdfCoordinateSpace.ViewerDips,
            unitsPerPoint);
    }

    /// <summary>Convert a rectangle to continuous reading-view DIPs at <paramref name="unitsPerPoint"/>.</summary>
    public static PdfPageRect ToContinuousDips(PdfPage page, PdfPageRect rect, double unitsPerPoint)
    {
        if (!double.IsFinite(unitsPerPoint) || unitsPerPoint <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsPerPoint), "Coordinate scale must be positive.");

        EnsurePage(page, rect);
        var visual = ToVisualPoints(page, rect);
        return new PdfPageRect(
            page.PageNumber,
            visual.X * unitsPerPoint,
            visual.Y * unitsPerPoint,
            visual.Width * unitsPerPoint,
            visual.Height * unitsPerPoint,
            PdfCoordinateSpace.ContinuousDips,
            unitsPerPoint);
    }

    private static PdfPageRect ContentToVisualPoints(PdfPage page, PdfPageRect rect)
    {
        var content = rect.ToPdfRectangle().Normalize();
        var mb = page.MediaBox.Normalize();
        var l = mb.Left;
        var b = mb.Bottom;
        var w = mb.Width;
        var h = mb.Height;
        var rotation = page.Rotation;   // already canonical {0,90,180,270}

        (double x, double y) Map(double cx, double cy) => rotation switch
        {
            90 => (cy - b, cx - l),
            180 => (l + w - cx, cy - b),
            270 => (b + h - cy, l + w - cx),
            _ => (cx - l, b + h - cy),
        };

        var p1 = Map(content.Left, content.Bottom);
        var p2 = Map(content.Left, content.Top);
        var p3 = Map(content.Right, content.Bottom);
        var p4 = Map(content.Right, content.Top);

        var minX = Math.Min(Math.Min(p1.x, p2.x), Math.Min(p3.x, p4.x));
        var maxX = Math.Max(Math.Max(p1.x, p2.x), Math.Max(p3.x, p4.x));
        var minY = Math.Min(Math.Min(p1.y, p2.y), Math.Min(p3.y, p4.y));
        var maxY = Math.Max(Math.Max(p1.y, p2.y), Math.Max(p3.y, p4.y));

        return new PdfPageRect(
            page.PageNumber,
            minX,
            minY,
            maxX - minX,
            maxY - minY,
            PdfCoordinateSpace.VisualPoints);
    }

    private static void EnsurePage(PdfPage page, PdfPageRect rect)
    {
        if (page == null)
            throw new ArgumentNullException(nameof(page));
        if (rect.PageNumber != page.PageNumber)
            throw new ArgumentException(
                $"Rectangle is for page {rect.PageNumber}, but mapper page is {page.PageNumber}.",
                nameof(rect));
    }

}
