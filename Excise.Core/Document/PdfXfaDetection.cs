using Excise.Core.Parsing;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// What kind of XFA (XML Forms Architecture) form a document carries (#1547).
/// </summary>
public enum PdfXfaFormKind
{
    /// <summary>No <c>/AcroForm /XFA</c> entry: an ordinary PDF or AcroForm.</summary>
    None,

    /// <summary>
    /// XFA data alongside usable AcroForm fields ("static XFA"). A viewer that
    /// ignores XFA can still show and fill the page through the AcroForm.
    /// </summary>
    Static,

    /// <summary>
    /// A dynamic XFA form: the catalog sets <c>/NeedsRendering true</c>, or
    /// there are no AcroForm fields with a widget to fall back on. The page
    /// content is typically a "Please wait..." placeholder that only an XFA
    /// engine replaces.
    /// </summary>
    Dynamic,
}

/// <summary>
/// Detects XFA forms so the viewer can say when it cannot show one (#1547,
/// phase 1). Detection only: excise does not render or fill XFA.
/// </summary>
public static class PdfXfaDetection
{
    /// <summary>
    /// Classify the document's XFA form, if any.
    /// </summary>
    /// <remarks>
    /// <para><c>/XFA</c> lives in the AcroForm dictionary and is either one
    /// stream or an array of (name, stream) packet pairs (ISO 32000-1
    /// Table 218). An empty array or a value that does not resolve to either
    /// shape counts as no XFA.</para>
    /// <para><c>/NeedsRendering</c> is a CATALOG entry, not an AcroForm one
    /// (ISO 32000-1 Table 28; deprecated in PDF 2.0): true means the document
    /// must be regenerated from the XFA template when opened.</para>
    /// <para>"Usable AcroForm fields" means at least one field with a widget
    /// annotation — a field list with no widgets has nothing to display or
    /// fill.</para>
    /// </remarks>
    public static PdfXfaFormKind DetectXfaForm(this PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Catalog.GetOptional("AcroForm") is not { } acroFormObj
            || document.Resolve(acroFormObj) is not PdfDictionary acroForm)
        {
            return PdfXfaFormKind.None;
        }

        if (!HasXfaPackets(document, acroForm.GetOptional("XFA")))
            return PdfXfaFormKind.None;

        if (document.Catalog.GetOptional("NeedsRendering") is { } needsRendering
            && document.Resolve(needsRendering) is PdfBoolean { Value: true })
        {
            return PdfXfaFormKind.Dynamic;
        }

        return HasUsableAcroFormFields(document)
            ? PdfXfaFormKind.Static
            : PdfXfaFormKind.Dynamic;
    }

    private static bool HasXfaPackets(PdfDocument document, PdfObject? xfa)
    {
        if (xfa == null)
            return false;

        return document.Resolve(xfa) switch
        {
            PdfStream => true,
            PdfArray packets => packets.Any(p => document.Resolve(p) is PdfStream),
            _ => false,
        };
    }

    private static bool HasUsableAcroFormFields(PdfDocument document)
    {
        try
        {
            var form = document.GetAcroForm();
            return form != null && form.Fields.Any(f => f.Widgets.Count > 0);
        }
        catch (PdfParseException)
        {
            // A malformed field tree offers nothing to fall back on; say so
            // rather than fail the document open.
            return false;
        }
    }
}
