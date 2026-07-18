namespace Excise.Core.Document;

/// <summary>
/// Represents a named destination in a PDF document.
/// ISO 32000-2:2020 §12.3.2.3.
/// Named destinations allow jumping to a specific page and location via a name string.
/// </summary>
public sealed record NamedDestination(
    /// <summary>
    /// The destination name.
    /// </summary>
    string Name,

    /// <summary>
    /// The target page number (1-based), or null if the page couldn't be resolved.
    /// </summary>
    int? PageNumber,

    /// <summary>
    /// Horizontal coordinate, or null if not specified.
    /// </summary>
    double? X = null,

    /// <summary>
    /// Vertical coordinate, or null if not specified.
    /// </summary>
    double? Y = null,

    /// <summary>
    /// Zoom level, or null if not specified.
    /// For /XYZ fit mode, this is the zoom percentage.
    /// </summary>
    double? Zoom = null,

    /// <summary>
    /// The fit mode that determines how the page is displayed.
    /// Values: /Fit, /FitH, /FitV, /XYZ, /FitR, /FitB, /FitBH, /FitBV.
    /// </summary>
    string FitMode = "XYZ");
