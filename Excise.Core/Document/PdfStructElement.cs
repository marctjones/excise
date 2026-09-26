using Excise.Core.Primitives;
using System.Collections.Generic;
using System.Linq;

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
    /// The type the <c>/RoleMap</c> chain from <see cref="Type"/> ends on: the
    /// first standard structure type it reaches, else where it ends or repeats
    /// (ISO 32000-2 §14.7.3). <see cref="Type"/> when the role map does not name it.
    /// </summary>
    public string RoleMappedType { get; }

    /// <summary>
    /// The 1-based page the element is associated with: its own <c>/Pg</c>, else
    /// the <c>/Pg</c> of its first marked-content or object reference that names
    /// one, else its parent's. Null when none of those names a page.
    /// </summary>
    public int? PageNumber { get; }

    /// <summary>
    /// Child elements (nested in the structure tree).
    /// Empty if this is a leaf element.
    /// </summary>
    public IReadOnlyList<PdfStructElement> Children { get; }

    /// <summary>
    /// The marked-content sequences this element's <c>/K</c> references, in
    /// <c>/K</c> order: integer MCIDs and <c>/MCR</c> dictionaries alike.
    /// </summary>
    public IReadOnlyList<PdfMarkedContentReference> MarkedContent { get; }

    /// <summary>
    /// Reference to the raw structure dictionary, for advanced use cases.
    /// </summary>
    public PdfDictionary RawDictionary { get; }

    internal PdfStructElement(
        string type,
        string? altText = null,
        string? actualText = null,
        string? language = null,
        int? pageNumber = null,
        IReadOnlyList<PdfStructElement>? children = null,
        IReadOnlyList<PdfMarkedContentReference>? markedContent = null,
        PdfDictionary? rawDictionary = null,
        string? roleMappedType = null)
    {
        Type = type;
        RoleMappedType = roleMappedType ?? type;
        AltText = altText;
        ActualText = actualText;
        Language = language;
        PageNumber = pageNumber;
        Children = children ?? System.Array.Empty<PdfStructElement>();
        MarkedContent = markedContent ?? System.Array.Empty<PdfMarkedContentReference>();
        RawDictionary = rawDictionary ?? new PdfDictionary();
    }

    public override string ToString()
    {
        var parts = new List<string> { $"Type={Type}" };
        if (!string.IsNullOrEmpty(AltText)) parts.Add($"Alt={AltText}");
        if (!string.IsNullOrEmpty(ActualText)) parts.Add($"ActualText={ActualText}");
        if (MarkedContent.Count > 0) parts.Add($"MCIDs=[{string.Join(",", MarkedContent.Select(r => r.Mcid))}]");
        if (Children.Count > 0) parts.Add($"Children={Children.Count}");
        return $"StructElement({string.Join(", ", parts)})";
    }
}

/// <summary>
/// One marked-content sequence a structure element references (ISO 32000-2
/// §14.7.5.2): an integer in its <c>/K</c>, or an <c>/MCR</c> dictionary.
/// </summary>
/// <param name="Mcid">The sequence's <c>/MCID</c>.</param>
/// <param name="PageNumber">The 1-based page the reference's own <c>/Pg</c>
/// names, else the page its element's <c>/Pg</c> names (Tables 355 and 357);
/// null when neither names a page.</param>
public readonly record struct PdfMarkedContentReference(int Mcid, int? PageNumber);
