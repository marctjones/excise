using System.Globalization;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>The outcome of <see cref="PdfXfaLayout.ApplyXfaLayout"/>.</summary>
public enum XfaLayoutStatus
{
    /// <summary>
    /// The document is not a dynamic XFA form that asks to be rendered
    /// (catalog <c>/NeedsRendering true</c>); nothing was changed.
    /// </summary>
    NotDynamicXfa,

    /// <summary>The pages were already generated from the XFA form (by excise); nothing was changed.</summary>
    AlreadyLaidOut,

    /// <summary>The placeholder pages were replaced by the form's layout.</summary>
    LaidOut,

    /// <summary>The form could not be laid out; the document is unchanged.</summary>
    Failed,
}

/// <summary>Bounds for one layout run.</summary>
public sealed class XfaLayoutOptions
{
    /// <summary>Wall-clock limit for the whole run. The default is 15 seconds.</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>What <see cref="PdfXfaLayout.ApplyXfaLayout"/> did, and what the rendition leaves out.</summary>
public sealed class XfaLayoutResult
{
    public XfaLayoutStatus Status { get; init; }

    /// <summary>The document's page count after the call.</summary>
    public int PageCount { get; init; }

    /// <summary>Why layout failed; null otherwise.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Parts of the form the rendition does not show or approximates (images,
    /// barcodes, unsupported references...), one line per kind.
    /// </summary>
    public IReadOnlyList<string> Omissions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Scripts the form carries, which excise does not run (#1570, #1571),
    /// counted by the event that would run them (<c>initialize</c>,
    /// <c>calculate</c>, <c>click</c>...).
    /// </summary>
    public IReadOnlyDictionary<string, int> ScriptsNotRun { get; init; } = new Dictionary<string, int>();

    /// <summary>True when the document's pages now show the XFA form.</summary>
    public bool ShowsForm => Status is XfaLayoutStatus.LaidOut or XfaLayoutStatus.AlreadyLaidOut;
}

/// <summary>
/// Displays dynamic XFA forms (#1547 phase 2) by replacing their placeholder
/// pages with the form's initial layout. See
/// docs/architecture/xfa-rendering.md for the design and its limits.
/// </summary>
public static class PdfXfaLayout
{
    private const string PieceInfoOwner = "Excise";
    private const string MarkerKey = "XfaLayout";

    /// <summary>
    /// Lay out a dynamic XFA form into ordinary pages, in memory. The document
    /// is left untouched unless the whole layout succeeds. Scripts do not run.
    /// </summary>
    /// <remarks>
    /// Costs one <see cref="PdfXfaDetection.DetectXfaForm"/> call on any other
    /// document. <c>/XFA</c> and <c>/NeedsRendering</c> are kept, so XFA
    /// viewers still regenerate the form from a saved copy; any redaction of
    /// the laid-out document removes them (see <see cref="RemoveXfaForm"/>).
    /// </remarks>
    public static XfaLayoutResult ApplyXfaLayout(
        this PdfDocument document,
        XfaLayoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new XfaLayoutOptions();

        // Only a form that asks to be rendered (/NeedsRendering true, the
        // criterion pdf.js uses too) has placeholder pages. A document
        // classified dynamic only for lacking AcroForm widgets can carry real
        // page content (pdfium's rectangles_multi_page_xfa.pdf: five drawn
        // pages), which a layout must not replace.
        if (document.DetectXfaForm() != PdfXfaFormKind.Dynamic || !NeedsRendering(document))
            return new XfaLayoutResult { Status = XfaLayoutStatus.NotDynamicXfa, PageCount = document.PageCount };

        if (document.HasXfaLayoutPages())
            return new XfaLayoutResult { Status = XfaLayoutStatus.AlreadyLaidOut, PageCount = document.PageCount };

        var budget = new XfaBudget(options.TimeLimit, cancellationToken);
        var report = new XfaReport();

        List<XfaPage> pages;
        try
        {
            if (!XfaPackets.TryRead(document, out var packets, out var reason))
                return Failed(document, reason, report);

            var template = new XfaTemplate(packets!.Template, budget, report);
            var merge = new XfaMerge(budget, report, packets.DataRoot);
            var form = merge.Merge(template.Root);
            var layout = new XfaLayout(budget, report);
            var pageAreas = ReadPageAreas(template.Root, merge, layout, budget, report);
            var root = layout.Build(form, pageAreas[0].ContentAreas[0].W);
            pages = new XfaPaginator(pageAreas, budget, report).Paginate(root);
        }
        catch (XfaLayoutException ex)
        {
            return Failed(document, ex.Message, report);
        }

        // Only now touch the document, and undo everything on failure.
        int original = document.PageCount;
        try
        {
            var written = new XfaPdfWriter(document, budget, report).Write(pages);
            var stamp = new PdfString("D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z");
            foreach (var page in written)
                Mark(page, stamp);
            for (int i = original - 1; i >= 0; i--)
                document.Pages.RemoveAt(i);
        }
        catch (Exception ex)
        {
            // Any failure while writing leaves the document as it was opened.
            while (document.PageCount > original)
                document.Pages.RemoveAt(document.PageCount - 1);
            if (ex is not XfaLayoutException)
                throw;
            return Failed(document, ex.Message, report);
        }

        return new XfaLayoutResult
        {
            Status = XfaLayoutStatus.LaidOut,
            PageCount = document.PageCount,
            Omissions = report.Notes,
            ScriptsNotRun = new Dictionary<string, int>(report.ScriptEvents),
        };
    }

    /// <summary>
    /// True when excise generated pages of this document from its XFA form
    /// (at least one page carries the marker) and the XFA form is still
    /// present. Such a document is never laid out again: pages the user added
    /// after the layout would otherwise be replaced on the next open.
    /// </summary>
    public static bool HasXfaLayoutPages(this PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.PageCount == 0 || !HasXfaEntry(document))
            return false;

        foreach (var page in document.Pages)
        {
            if (IsMarked(document, page))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Remove the XFA form: <c>/AcroForm /XFA</c>, the catalog's
    /// <c>/NeedsRendering</c>, and the packet streams themselves once nothing
    /// else references them. The pages are unchanged. Returns true when
    /// anything was removed.
    /// </summary>
    /// <remarks>
    /// On a document whose pages excise generated from the XFA form, the
    /// packet restates everything on those pages; redaction calls this so a
    /// viewer cannot regenerate the redacted values from it.
    /// </remarks>
    public static bool RemoveXfaForm(this PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool changed = document.Catalog.Remove("NeedsRendering");
        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acroForm
            || acroForm.GetOptional("XFA") is not { } xfa)
        {
            return changed;
        }

        var packetObjects = new HashSet<int>();
        if (xfa is PdfReference direct)
            packetObjects.Add(direct.ObjectNum);
        if (document.Resolve(xfa) is PdfArray array)
        {
            foreach (var item in array)
            {
                if (item is PdfReference r)
                    packetObjects.Add(r.ObjectNum);
            }
        }

        acroForm.Remove("XFA");

        // The writer saves every object in the table, reachable or not, so the
        // packet streams must be freed or their data would still be in the file.
        var reachable = document.ComputeReachableObjects();
        foreach (var objectNumber in packetObjects)
        {
            if (!reachable.Contains(objectNumber))
                document.RemoveObject(objectNumber);
        }
        return true;
    }

    /// <summary>
    /// The redaction rule for laid-out XFA documents (decision 5 in
    /// docs/architecture/xfa-rendering.md): once any page content is being
    /// redacted, the XFA packet goes, whatever the caller's carrier scope.
    /// </summary>
    internal static bool RemoveXfaSourceOfLaidOutPages(PdfDocument document)
        => document.HasXfaLayoutPages() && document.RemoveXfaForm();

    private static XfaLayoutResult Failed(PdfDocument document, string reason, XfaReport report)
        => new()
        {
            Status = XfaLayoutStatus.Failed,
            PageCount = document.PageCount,
            FailureReason = reason,
            Omissions = report.Notes,
            ScriptsNotRun = new Dictionary<string, int>(report.ScriptEvents),
        };

    private static bool NeedsRendering(PdfDocument document)
        => document.Catalog.GetOptional("NeedsRendering") is { } value
           && document.Resolve(value) is PdfBoolean { Value: true };

    private static bool HasXfaEntry(PdfDocument document)
        => document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm
           && acroForm.GetOptional("XFA") != null;

    private static void Mark(PdfPage page, PdfString stamp)
    {
        var data = new PdfDictionary();
        data["LastModified"] = stamp;
        var privateData = new PdfDictionary();
        privateData.SetBool(MarkerKey, true);
        data["Private"] = privateData;

        var pieceInfo = new PdfDictionary();
        pieceInfo[PieceInfoOwner] = data;
        page.Dictionary["PieceInfo"] = pieceInfo;
        page.Dictionary["LastModified"] = stamp;
    }

    private static bool IsMarked(PdfDocument document, PdfPage page)
    {
        if (document.Resolve(page.Dictionary.GetOptional("PieceInfo") ?? PdfNull.Instance) is not PdfDictionary pieceInfo
            || document.Resolve(pieceInfo.GetOptional(PieceInfoOwner) ?? PdfNull.Instance) is not PdfDictionary data
            || document.Resolve(data.GetOptional("Private") ?? PdfNull.Instance) is not PdfDictionary privateData)
        {
            return false;
        }
        return document.Resolve(privateData.GetOptional(MarkerKey) ?? PdfNull.Instance) is PdfBoolean { Value: true };
    }

    private static List<XfaPageArea> ReadPageAreas(
        XElement root, XfaMerge merge, XfaLayout layout, XfaBudget budget, XfaReport report)
    {
        var pageSet = root.Child("pageSet")
            ?? root.Descendants(root.Name.Namespace + "pageSet").FirstOrDefault();

        var result = new List<XfaPageArea>();
        if (pageSet != null)
        {
            foreach (var area in pageSet.Descendants(root.Name.Namespace + "pageArea"))
            {
                budget.Tick();
                result.Add(ReadPageArea(area, merge, layout, report));
            }
        }

        if (result.Count == 0)
        {
            report.Note("no page area: US Letter assumed");
            var letter = new XElement(root.Name.Namespace + "pageArea");
            result.Add(ReadPageArea(letter, merge, layout, report));
        }
        return result;
    }

    private static XfaPageArea ReadPageArea(XElement area, XfaMerge merge, XfaLayout layout, XfaReport report)
    {
        var (width, height) = MediumSize(area.Child("medium"));

        var contentAreas = new List<XfaRect>();
        foreach (var ca in area.ChildrenNamed("contentArea"))
        {
            if (ca.TakesNoSpace())
                continue;
            var w = ca.Measure("w") ?? width;
            var h = ca.Measure("h") ?? height;
            if (w <= 0 || h <= 0)
                continue;
            contentAreas.Add(new XfaRect(ca.Measure("x") ?? 0, ca.Measure("y") ?? 0, w, h));
        }
        if (contentAreas.Count == 0)
        {
            report.Note("page area without a content area: the whole page is used");
            contentAreas.Add(new XfaRect(0, 0, width, height));
        }

        var fixedNode = merge.MergePageArea(area);
        var fixedBox = fixedNode.Children.Count == 0 ? null : layout.Build(fixedNode, width);

        var occur = area.Child("occur");
        return new XfaPageArea
        {
            Element = area,
            Width = width,
            Height = height,
            ContentAreas = contentAreas,
            Fixed = fixedBox,
            MaxOccur = occur == null ? -1 : occur.IntAttr("max", -1),
        };
    }

    /// <summary>Page size from <c>&lt;medium&gt;</c> (XFA 3.3 "medium": short × long, portrait unless landscape).</summary>
    private static (double Width, double Height) MediumSize(XElement? medium)
    {
        var (shortSide, longSide) = medium.AttrOr("stock", "letter").ToLowerInvariant() switch
        {
            "legal" => (612.0, 1008.0),
            "a4" => (595.28, 841.89),
            "a3" => (841.89, 1190.55),
            "a5" => (419.53, 595.28),
            "ledger" or "tabloid" => (792.0, 1224.0),
            "executive" => (522.0, 756.0),
            _ => (612.0, 792.0),
        };

        if (medium.Measure("short") is double s && s > 0)
            shortSide = s;
        if (medium.Measure("long") is double l && l > 0)
            longSide = l;
        shortSide = Math.Clamp(shortSide, 1, 14400);
        longSide = Math.Clamp(longSide, 1, 14400);

        return medium.AttrOr("orientation", "portrait") == "landscape"
            ? (longSide, shortSide)
            : (shortSide, longSide);
    }
}
