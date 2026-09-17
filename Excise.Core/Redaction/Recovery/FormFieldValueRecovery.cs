using System;
using System.Collections.Generic;
using Excise.Core.Document;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587/#1592 — AcroForm field values, located at their widget's <c>/Rect</c>.
///
/// <para><b>The failure mode.</b> A form field's displayed text lives in its
/// widget's APPEARANCE STREAM (<c>/AP /N</c>), but the authoritative value lives
/// in the field dictionary's <c>/V</c> (§12.7.3.3). A redactor that rewrites
/// page content, or that blanks the appearance stream, leaves <c>/V</c>
/// untouched — and every reader that regenerates appearances (any viewer
/// honouring <c>/NeedAppearances</c>) paints the "redacted" value straight back
/// onto the page. The value is also plain text in the file for anyone who
/// looks.</para>
///
/// <para><b>Certain, with a real location.</b> Unlike the width residue this
/// asserts nothing probabilistic — the string is in the bytes — and unlike the
/// document-level carriers it has page geometry, because the widget states
/// exactly where on the page that value belongs. That makes it linkable to a
/// mark and drawable by <c>--restore</c> (#1588).</para>
///
/// <para><b>/V only, not /DV or /TU.</b> The default value and the tooltip are
/// authored strings, not entered data: reporting them would fill an audit with
/// form design (#1431's carrier list keeps them greppable for exactly that
/// reason). A blank or whitespace value carries nothing and is skipped.</para>
/// </summary>
public static class FormFieldValueRecovery
{
    /// <param name="Rect">The widget's rectangle, or null for a field with no widget on any page.</param>
    public readonly record struct FieldValue(
        int PageNumber, string FieldName, string Value, PdfRectangle? Rect);

    public static IReadOnlyList<FieldValue> Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var found = new List<FieldValue>();

        PdfAcroForm? form;
        try { form = document.GetAcroForm(); }
        catch { return found; }
        if (form == null) return found;

        foreach (var field in form.Fields)
        {
            string? value;
            try { value = field.Value; }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(value)) continue;

            // A field with no widget still holds the value; it has no page, so
            // it is reported page 0 and the report files it document-level.
            found.Add(new FieldValue(
                field.PageNumber ?? 0,
                string.IsNullOrEmpty(field.FullName) ? field.PartialName : field.FullName,
                value!,
                field.Rect));
        }

        return found;
    }
}
