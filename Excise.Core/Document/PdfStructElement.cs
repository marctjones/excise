using Excise.Core.Primitives;
using System.Collections.Generic;

namespace Excise.Core.Document;

/// <summary>
/// Represents a single element in the PDF structure tree (tagged PDF).
/// Structure trees define the logical reading order and semantics of content,
/// used by screen readers and accessible tools. They are also a security concern:
/// the structure tree can reference content via /MCID that may be "hidden" on screen
/// but fully accessible via the structure tree.
///
/// ISO 32000-2 §14.7 specifies structure tree semantics.
/// </summary>
public sealed class PdfStructElement
{
    /// <summary>
    /// The element type (e.g., "/P", "/H1", "/H2", "/Span", "/Figure", "/Table", "/TR", "/TD").
    /// This defines the semantic role of the element (paragraph, heading, table row, etc.).
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Alternate text for this element. Typically used for images or complex structures
    /// when the actual content needs a textual description.
    /// Corresponds to /Alt entry in the structure dictionary.
    /// </summary>
    public string? AltText { get; }

    /// <summary>
    /// Override text for accessibility. When present, screen readers use this instead
    /// of extracting text from the content stream.
    /// Corresponds to /ActualText entry in the structure dictionary.
    /// </summary>
    public string? ActualText { get; }

    /// <summary>
    /// Language code for this element (e.g., "en", "es", "fr").
    /// Corresponds to /Lang entry in the structure dictionary.
    /// </summary>
    public string? Language { get; }

    /// <summary>
    /// The page number (1-based) that this element is associated with, if known.
    /// May be null if the structure element spans multiple pages or has no clear page association.
    /// </summary>
    public int? PageNumber { get; }

    /// <summary>
    /// Child elements (nested in the structure tree).
    /// Empty if this is a leaf element.
    /// </summary>
    public IReadOnlyList<PdfStructElement> Children { get; }

    /// <summary>
    /// List of Marked Content IDs (/MCID) that this element references.
    /// MCIDs are integers that tag content streams with semantic meaning.
    /// For example, /MCID 5 in the content stream links that content to this structure element.
    /// </summary>
    public IReadOnlyList<int> MarkedContentIds { get; }

    /// <summary>
    /// Reference to the raw structure dictionary, for advanced use cases.
    /// </summary>
    public PdfDictionary RawDictionary { get; }

    public PdfStructElement(
        string type,
        string? altText = null,
        string? actualText = null,
        string? language = null,
        int? pageNumber = null,
        IReadOnlyList<PdfStructElement>? children = null,
        IReadOnlyList<int>? markedContentIds = null,
        PdfDictionary? rawDictionary = null)
    {
        Type = type;
        AltText = altText;
        ActualText = actualText;
        Language = language;
        PageNumber = pageNumber;
        Children = children ?? System.Array.Empty<PdfStructElement>();
        MarkedContentIds = markedContentIds ?? System.Array.Empty<int>();
        RawDictionary = rawDictionary ?? new PdfDictionary();
    }

    public override string ToString()
    {
        var parts = new List<string> { $"Type={Type}" };
        if (!string.IsNullOrEmpty(AltText)) parts.Add($"Alt={AltText}");
        if (!string.IsNullOrEmpty(ActualText)) parts.Add($"ActualText={ActualText}");
        if (MarkedContentIds.Count > 0) parts.Add($"MCIDs=[{string.Join(",", MarkedContentIds)}]");
        if (Children.Count > 0) parts.Add($"Children={Children.Count}");
        return $"StructElement({string.Join(", ", parts)})";
    }
}
