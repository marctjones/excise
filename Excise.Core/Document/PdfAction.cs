namespace Excise.Core.Document;

/// <summary>
/// A parsed PDF action dictionary (ISO 32000-2:2020 §12.6 "Actions"), covering
/// document-level (/OpenAction, /AA), page-level (/AA), and document-JavaScript
/// (/Names/JavaScript) actions.
/// </summary>
/// <remarks>
/// This is a read-only, non-executing model (issue #331). excise never runs the
/// JavaScript it parses — <see cref="IsJavaScript"/> and <see cref="JavaScriptSource"/>
/// exist so callers can detect and flag JS-bearing documents, not so they can
/// evaluate the script. All action types round-trip through their original
/// dictionary in the document object graph; this record is a read-side view,
/// not the thing that gets serialized back out.
/// </remarks>
public sealed record PdfAction(
    /// <summary>The action type (/S), e.g. "GoTo", "URI", "JavaScript", "Named", "GoToR", "Launch", "SubmitForm".</summary>
    string Type,

    /// <summary>Target URI for a URI action (/URI). Null for other action types.</summary>
    string? Uri = null,

    /// <summary>
    /// Decoded ECMAScript source for a JavaScript action (/JS, string or stream form).
    /// Never executed by excise. Null for non-JavaScript actions or if /JS could not be decoded.
    /// </summary>
    string? JavaScriptSource = null,

    /// <summary>The named-action name (/N) for a Named action, e.g. "NextPage", "PrevPage", "FirstPage", "LastPage".</summary>
    string? NamedActionName = null,

    /// <summary>
    /// 1-based destination page number for a GoTo action, resolved from /D
    /// (direct destination array or named destination). Null if the action
    /// is not a GoTo, or the destination could not be resolved.
    /// </summary>
    int? DestinationPage = null,

    /// <summary>The additional-actions chain (/Next) to run after this one, in order. Empty if absent.</summary>
    IReadOnlyList<PdfAction>? Next = null)
{
    /// <summary>True if this is a /JavaScript action (regardless of whether the source decoded successfully).</summary>
    public bool IsJavaScript => string.Equals(Type, "JavaScript", StringComparison.Ordinal);

    /// <summary>Actions chained via /Next, defaulting to an empty list rather than null.</summary>
    public IReadOnlyList<PdfAction> NextActions => Next ?? Array.Empty<PdfAction>();
}
