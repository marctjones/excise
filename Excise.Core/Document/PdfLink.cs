using Excise.Core.Primitives;
using System.Collections.Generic;

namespace Excise.Core.Document;

/// <summary>What kind of target a <see cref="PdfLink"/> points at.</summary>
public enum PdfLinkKind
{
    /// <summary>A GoTo action or /Dest — another page in this document. <see cref="PdfLink.DestinationPage"/> is valid.</summary>
    InternalDestination,
    /// <summary>A URI action with an http/https/mailto target. <see cref="PdfLink.Uri"/> is valid.</summary>
    ExternalUri,
    /// <summary>
    /// An action type excise refuses to run rather than silently ignore
    /// (#625) — /Launch, /GoToE (embedded-file), /GoToR (remote-file) actions,
    /// and URI actions with a non-allowlisted scheme (file:, javascript:, etc.).
    /// <see cref="PdfLink.DangerousActionType"/> names what was refused, for
    /// the UI to show a clear "this was blocked" message instead of nothing
    /// happening.
    /// </summary>
    Dangerous,
}

/// <summary>
/// One link annotation extracted from a PDF page's /Annots array
/// (PDF spec §12.5.6.5) — either an internal-document destination, an
/// external URI, or a dangerous action type excise refuses to run (#625).
/// </summary>
public sealed class PdfLink
{
    /// <summary>Click rectangle in PDF points (Y-up, bottom-left origin).</summary>
    public PdfRectangle Rect { get; }
    /// <summary>What kind of target this link points at.</summary>
    public PdfLinkKind Kind { get; }
    /// <summary>1-based page number of the link's destination. Valid only when <see cref="Kind"/> is <see cref="PdfLinkKind.InternalDestination"/>.</summary>
    public int DestinationPage { get; }
    /// <summary>The link's target URI. Valid only when <see cref="Kind"/> is <see cref="PdfLinkKind.ExternalUri"/>.</summary>
    public string? Uri { get; }
    /// <summary>What action type was refused (e.g. "Launch", "GoToE", "URI:file"). Valid only when <see cref="Kind"/> is <see cref="PdfLinkKind.Dangerous"/>.</summary>
    public string? DangerousActionType { get; }

    /// <summary>Internal-destination link. Preserved as the original two-arg constructor for source/binary compatibility.</summary>
    public PdfLink(PdfRectangle rect, int destinationPage)
    {
        Rect = rect;
        Kind = PdfLinkKind.InternalDestination;
        DestinationPage = destinationPage;
    }

    private PdfLink(PdfRectangle rect, PdfLinkKind kind, string? uri, string? dangerousActionType)
    {
        Rect = rect;
        Kind = kind;
        Uri = uri;
        DangerousActionType = dangerousActionType;
    }

    public static PdfLink ExternalLink(PdfRectangle rect, string uri) =>
        new(rect, PdfLinkKind.ExternalUri, uri, dangerousActionType: null);

    public static PdfLink DangerousLink(PdfRectangle rect, string actionType) =>
        new(rect, PdfLinkKind.Dangerous, uri: null, dangerousActionType: actionType);
}

public static class PdfLinkParser
{
    /// <summary>
    /// Extract internal-document link annotations from <paramref name="pageDict"/>.
    /// </summary>
    /// <remarks>
    /// We share PdfOutlineParser's page-ref → page-number map and named-dest
    /// resolution because both ToC entries and link annotations use the
    /// same /Dest mechanism under the hood.
    /// </remarks>
    public static IReadOnlyList<PdfLink> Parse(PdfDocument doc, PdfDictionary pageDict,
        System.Collections.Generic.Dictionary<(int, int), int> pageRefToNumber,
        System.Collections.Generic.Dictionary<string, PdfObject>? namedDests)
    {
        var annotsObj = pageDict.GetOptional("Annots");
        if (annotsObj == null) return System.Array.Empty<PdfLink>();
        if (doc.Resolve(annotsObj) is not PdfArray annots) return System.Array.Empty<PdfLink>();

        var links = new List<PdfLink>();
        foreach (var entry in annots)
        {
            if (doc.Resolve(entry) is not PdfDictionary annot) continue;
            if (annot.GetNameOrNull("Subtype") != "Link") continue;

            var rectArr = doc.Resolve(annot.GetOptional("Rect") ?? (PdfObject)PdfNull.Instance) as PdfArray;
            if (rectArr == null || rectArr.Count < 4) continue;
            var rect = new PdfRectangle(
                (double)rectArr.GetNumber(0),
                (double)rectArr.GetNumber(1),
                (double)rectArr.GetNumber(2),
                (double)rectArr.GetNumber(3));

            var link = ResolveLink(doc, annot, rect, pageRefToNumber, namedDests);
            if (link == null) continue;

            links.Add(link);
        }
        return links;
    }

    /// <summary>
    /// URI schemes excise will navigate to after user confirmation (#625).
    /// Everything else — file:, javascript:, data:, and any other scheme —
    /// resolves to <see cref="PdfLinkKind.Dangerous"/> instead.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> AllowedUriSchemes =
        new(System.StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    private static PdfLink? ResolveLink(PdfDocument doc, PdfDictionary annot, PdfRectangle rect,
        System.Collections.Generic.Dictionary<(int, int), int> pageRefToNumber,
        System.Collections.Generic.Dictionary<string, PdfObject>? namedDests)
    {
        var dest = annot.GetOptional("Dest");
        if (dest == null)
        {
            var action = doc.Resolve(annot.GetOptional("A") ?? (PdfObject)PdfNull.Instance) as PdfDictionary;
            if (action == null) return null;

            var actionType = action.GetNameOrNull("S");
            switch (actionType)
            {
                case "GoTo":
                    dest = action.GetOptional("D");
                    break;
                case "URI":
                    var uriObj = doc.Resolve(action.GetOptional("URI") ?? (PdfObject)PdfNull.Instance);
                    var uriValue = (uriObj as PdfString)?.Value;
                    if (string.IsNullOrWhiteSpace(uriValue)) return null;
                    return System.Uri.TryCreate(uriValue, System.UriKind.Absolute, out var parsed)
                        && AllowedUriSchemes.Contains(parsed.Scheme)
                        ? PdfLink.ExternalLink(rect, uriValue)
                        : PdfLink.DangerousLink(rect, $"URI:{(parsed?.Scheme ?? "malformed")}");
                // /Launch runs an external application or file — a classic
                // malware vector. /GoToE and /GoToR navigate into an
                // embedded or remote file rather than this document, which
                // is the same "leaves the document excise is showing you"
                // risk as /Launch. Refuse all three explicitly (#625)
                // rather than silently doing nothing.
                case "Launch":
                case "GoToE":
                case "GoToR":
                    return PdfLink.DangerousLink(rect, actionType);
                default:
                    return null;
            }
        }
        if (dest == null) return null;

        // Resolve named destinations to their array form (same code path
        // outline items use; both go through the catalog's name tree).
        if (dest is PdfName n &&
            namedDests != null && namedDests.TryGetValue(n.Value, out var nd))
        {
            dest = nd;
        }
        else if (dest is PdfString s &&
            namedDests != null && namedDests.TryGetValue(s.Value, out var sd))
        {
            dest = sd;
        }
        else
        {
            dest = doc.Resolve(dest);
        }

        if (dest is PdfArray arr && arr.Count > 0 &&
            arr[0] is PdfReference pageRef &&
            pageRefToNumber.TryGetValue((pageRef.ObjectNum, pageRef.Generation), out var pageNum))
        {
            return new PdfLink(rect, pageNum);
        }
        return null;
    }
}
