using Excise.Core.Primitives;
using System.Collections.Generic;

namespace Excise.Core.Document;

/// <summary>
/// Represents a single Optional Content Group (OCG / layer) in a PDF document.
/// OCGs allow content to be made visible or invisible via the /OCProperties
/// entry in the document catalog (ISO 32000-2 §8.11).
/// </summary>
public sealed record PdfOcg(
    /// <summary>
    /// The human-readable name of this OCG (from /Name in the OCG dict).
    /// </summary>
    string Name,

    /// <summary>
    /// Whether this OCG is visible by default in the document's initial view.
    /// If false, content marked with this OCG is hidden until the user toggles
    /// it on. This is the security-critical field: if true but actual visibility
    /// is false (set in default config), content may be extractable via other tools.
    /// </summary>
    bool IsVisibleByDefault,

    /// <summary>
    /// The raw OCG dictionary from the PDF. Contains /Name, /Type, and optional
    /// /Intent (View vs Design), /Usage (print/export behaviors), etc.
    /// </summary>
    PdfDictionary RawDictionary);

/// <summary>
/// Represents the document's Optional Content Groups (OCGs) configuration.
/// Encapsulates the /Catalog/OCProperties/D (default configuration) state.
/// </summary>
public sealed class PdfOcgConfig
{
    /// <summary>
    /// List of all OCGs defined in /Catalog/OCProperties/OCGs.
    /// Empty if the document has no optional content.
    /// </summary>
    public IReadOnlyList<PdfOcg> AllOcgs { get; }

    /// <summary>
    /// The names of OCGs that are OFF (invisible) by default.
    /// Taken from /Catalog/OCProperties/D/OFF array.
    /// </summary>
    public IReadOnlySet<string> OffByDefault { get; }

    /// <summary>
    /// The viewing intent. Either "View" (for screen viewing) or "Design"
    /// (for design/editing). From /Catalog/OCProperties/D/Intent.
    /// Defaults to "View" if not specified.
    /// </summary>
    public string Intent { get; }

    /// <summary>
    /// The base state for OCGs not explicitly listed in ON or OFF arrays.
    /// Either "ON" or "OFF". From /Catalog/OCProperties/D/BaseState.
    /// Defaults to "ON" if not specified.
    /// </summary>
    public string BaseState { get; }

    /// <summary>
    /// The layer order from /Catalog/OCProperties/D/Order.
    /// This is a hierarchical structure for organizing layers in the UI.
    /// For now, we treat it as opaque — consumers can walk it if needed.
    /// </summary>
    public PdfObject? LayerOrder { get; }

    public PdfOcgConfig(
        IReadOnlyList<PdfOcg> allOcgs,
        IReadOnlySet<string> offByDefault,
        string intent = "View",
        string baseState = "ON",
        PdfObject? layerOrder = null)
    {
        AllOcgs = allOcgs;
        OffByDefault = offByDefault;
        Intent = intent;
        BaseState = baseState;
        LayerOrder = layerOrder;
    }

    /// <summary>
    /// Check if an OCG is visible by default.
    /// Returns true if the OCG is not in the OFF list and either:
    /// - It's in an explicit ON list (if one exists), OR
    /// - BaseState is "ON" (default behavior when not listed).
    /// </summary>
    public bool IsVisibleByDefault(string ocgName)
    {
        return !OffByDefault.Contains(ocgName);
    }
}
