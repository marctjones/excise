using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Xfa;

namespace Excise.App.Services;

/// <summary>
/// Whether a document carries filled form values (#2023). A page thumbnail of such a document
/// is a picture of those values, so <see cref="ThumbnailCacheService"/> keeps its thumbnails in
/// memory and never on disk. Every doubt answers "filled": a form that cannot be read is not
/// cached, which costs a re-render, where the opposite error writes personal data to disk.
/// </summary>
internal static class FilledFormValues
{
    /// <summary>
    /// The document's AcroForm fields, read once so a later write can re-check their values
    /// cheaply (<see cref="AnyFieldHasValue"/>). Empty when there is no AcroForm. Null when the
    /// AcroForm could not be read, which counts as filled.
    /// </summary>
    public static IReadOnlyList<PdfField>? ReadFields(PdfDocument document)
    {
        try
        {
            return document.GetAcroForm()?.Fields ?? Array.Empty<PdfField>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// A non-signature field holds a non-empty /V: a non-blank string, a name other than
    /// <c>Off</c> (a check box or radio button that is on), or a non-empty array (a multi-select
    /// list). <c>/V /Off</c> is the unfilled state every blank form's buttons carry.
    /// </summary>
    public static bool AnyFieldHasValue(PdfDocument document, IReadOnlyList<PdfField> fields)
    {
        try
        {
            foreach (var field in fields)
            {
                if (field.FieldType == PdfFieldType.Signature)
                    continue;
                if (IsFilled(document, field.RawDictionary.GetOptional("V")))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>The one field holds a filled value, by the rule of <see cref="AnyFieldHasValue"/>.</summary>
    public static bool FieldHasValue(PdfDocument document, PdfField field) =>
        AnyFieldHasValue(document, new[] { field });

    private static bool IsFilled(PdfDocument document, PdfObject? value)
    {
        if (value == null)
            return false;
        return document.Resolve(value) switch
        {
            PdfString s => !string.IsNullOrWhiteSpace(s.Value),
            PdfName n => n.Value.Length > 0 && !string.Equals(n.Value, "Off", StringComparison.Ordinal),
            PdfArray a => a.Count > 0,
            PdfNull => false,
            null => false,
            _ => true,
        };
    }

    /// <summary>
    /// The XFA datasets fill a field: <see cref="XfaFilledData.HasFilledValue"/> merges them
    /// with the template and looks at what each field is bound to, so a blank form's empty data
    /// nodes and check buttons at their off value are unfilled. A dynamic form with no data is
    /// unfilled. An /XFA entry that cannot be read or merged counts as filled.
    /// </summary>
    public static bool XfaDataHasValue(PdfDocument document)
    {
        try
        {
            var streams = XfaPackets.ReadPacketStreams(document);
            if (streams == null)
                return false;
            if (!XfaPackets.TryRead(streams, out var packets, out _) || packets == null)
                return true;
            return XfaFilledData.HasFilledValue(packets);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }
}
