namespace Excise.Core.Document;

/// <summary>
/// Presentation transition style. ISO 32000-2:2020 §12.4.4, Table 149 (/Trans /S).
/// </summary>
public enum PdfTransitionStyle
{
    /// <summary>Two lines sweep across the screen, revealing the new page (/Split).</summary>
    Split,

    /// <summary>Multiple lines sweep across, like a venetian blind (/Blinds).</summary>
    Blinds,

    /// <summary>A rectangular box sweeps in or out (/Box).</summary>
    Box,

    /// <summary>A single line sweeps across the screen (/Wipe).</summary>
    Wipe,

    /// <summary>The old page dissolves into the new one (/Dissolve).</summary>
    Dissolve,

    /// <summary>Similar to Dissolve, sweeping from one side (/Glitter).</summary>
    Glitter,

    /// <summary>The new page simply replaces the old one; no special effect (/R).</summary>
    Replace,

    /// <summary>The new page "flies" in or out (/Fly).</summary>
    Fly,

    /// <summary>The old page slides off as the new one slides in (/Push).</summary>
    Push,

    /// <summary>The new page slides in, covering the old one (/Cover).</summary>
    Cover,

    /// <summary>The old page slides off, uncovering the new one (/Uncover).</summary>
    Uncover,

    /// <summary>The new page gradually becomes visible (/Fade).</summary>
    Fade,
}

/// <summary>
/// A page transition effect (ISO 32000-2:2020 §12.4.4) used by presentation-mode
/// viewers when navigating to this page. excise parses and preserves this
/// dictionary on save; it does not implement presentation-mode playback
/// (issue #331 — UI integration deferred to a future issue).
/// </summary>
public sealed record PdfPageTransition(
    /// <summary>The transition style (/S). Defaults to <see cref="PdfTransitionStyle.Replace"/> if /S is absent.</summary>
    PdfTransitionStyle Style = PdfTransitionStyle.Replace,

    /// <summary>Duration of the transition effect itself, in seconds (/D). Default 1.</summary>
    double Duration = 1.0,

    /// <summary>
    /// Dimension in which the effect occurs (/Dm): "H" (horizontal, default) or "V" (vertical).
    /// Applies only to Split and Blinds styles.
    /// </summary>
    string? Dimension = null,

    /// <summary>
    /// Direction of motion (/M): "I" (inward, default) or "O" (outward).
    /// Applies only to Split, Box, and Fly styles.
    /// </summary>
    string? Motion = null,

    /// <summary>
    /// Direction the effect moves, in degrees counterclockwise from left-to-right (/Di).
    /// Valid numeric values: 0, 90, 180, or 270. Default 0. <c>-1</c> is a sentinel
    /// for the distinct name value <c>/Di /None</c> (Fly only — moving directly
    /// inward/outward with no oblique angle); it is not itself a spec value and must
    /// not be confused with a literal 315-degree direction, which is also legal per
    /// §12.4.4 Table 149 and is preserved as 315.
    /// </summary>
    int Direction = 0,

    /// <summary>Starting/ending scale for a Fly transition (/SS). Default 1.0.</summary>
    double? FlyScale = null,

    /// <summary>Whether the area to be flown in is rectangular and opaque (/B, Fly only). Default false.</summary>
    bool FlyOpaqueRectangle = false);
