namespace Excise.Core.Authoring;

/// <summary>
/// A page size in PDF points (1 pt = 1/72 inch), portrait by convention.
/// Use the presets (<see cref="Letter"/>, <see cref="A4"/>, …) or construct
/// your own; call <see cref="Landscape"/> to swap the dimensions.
/// </summary>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>US Letter — 8.5 × 11 in (612 × 792 pt).</summary>
    public static PageSize Letter => new(612, 792);

    /// <summary>US Legal — 8.5 × 14 in (612 × 1008 pt).</summary>
    public static PageSize Legal => new(612, 1008);

    /// <summary>ISO A4 — 210 × 297 mm (595.28 × 841.89 pt).</summary>
    public static PageSize A4 => new(595.28, 841.89);

    /// <summary>ISO A3 — 297 × 420 mm (841.89 × 1190.55 pt).</summary>
    public static PageSize A3 => new(841.89, 1190.55);

    /// <summary>ISO A5 — 148 × 210 mm (419.53 × 595.28 pt).</summary>
    public static PageSize A5 => new(419.53, 595.28);

    /// <summary>Returns this size rotated to landscape (wider than tall).</summary>
    public PageSize Landscape() => new(Math.Max(Width, Height), Math.Min(Width, Height));

    /// <summary>Returns this size rotated to portrait (taller than wide).</summary>
    public PageSize Portrait() => new(Math.Min(Width, Height), Math.Max(Width, Height));
}
