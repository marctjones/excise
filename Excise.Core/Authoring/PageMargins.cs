namespace Excise.Core.Authoring;

/// <summary>
/// Page margins in PDF points. The content area is the page minus these
/// margins; the builder flows content top-to-bottom inside it.
/// </summary>
public readonly record struct PageMargins(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Default 1-inch (72 pt) margins on all sides.</summary>
    public static PageMargins Default => All(72);

    /// <summary>Equal margins on all four sides.</summary>
    public static PageMargins All(double points) => new(points, points, points, points);

    /// <summary>Symmetric horizontal/vertical margins.</summary>
    public static PageMargins Symmetric(double horizontal, double vertical) =>
        new(horizontal, vertical, horizontal, vertical);
}
