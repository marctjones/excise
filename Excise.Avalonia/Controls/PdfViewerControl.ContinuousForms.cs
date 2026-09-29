using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// AcroForm fill in the continuous view (#1807).
/// </summary>
/// <remarks>
/// <para>
/// Continuous view used to be read-only by design ("editing always happens in
/// single-page mode", #371), and it is the DEFAULT view: a form opened there
/// looked like a plain page and silently took no input. Each realized page slot
/// now carries its own field inputs, built by the same
/// <see cref="FormFieldInputFactory.Build"/> the single-page overlay uses, so the two
/// views cannot fill a field differently, and positioned through the same
/// <see cref="PdfCoordinateMapper"/> the continuous selection highlight uses,
/// so there is one continuous transform, not a second one.
/// </para>
/// <para>
/// Only realized slots hold inputs; they are dropped when the page scrolls out
/// of the realized window (like the page composite, #1466), so a 400-page form
/// never keeps 400 pages of text boxes.
/// </para>
/// </remarks>
public partial class PdfViewerControl
{
    private const string ContinuousFormFieldClass = "continuous-form-field";

    /// <summary>Build (or keep) the inputs of one realized page slot at the current zoom.</summary>
    private void SyncContinuousSlotFormFields(PdfPageSlot slot)
    {
        var provider = PageFormFieldsProvider;
        var doc = Document;
        if (provider == null || doc == null || ZoomLevel <= 0)
        {
            ClearContinuousFormFields(slot);
            return;
        }

        // Built means "asked the provider at this zoom", even when the page has no
        // fields, so a fieldless page does not re-ask on every scroll.
        if (slot.FormFieldsBuiltForZoom == ZoomLevel) return;

        IReadOnlyList<PdfField> fields;
        PdfPage page;
        try
        {
            fields = provider(slot.PageNumber);
            page = doc.GetPage(slot.PageNumber);
        }
        catch (Exception)
        {
            ClearContinuousFormFields(slot);
            return;
        }

        slot.FormFieldControls.Clear();
        var unitsPerPoint = PointsToDip * ZoomLevel;
        var ordered = FormFieldInputFactory.OrderFormFieldsForTabbing(fields);
        for (var tabIndex = 0; tabIndex < ordered.Count; tabIndex++)
        {
            var field = ordered[tabIndex];
            var dips = PdfCoordinateMapper.ToContinuousDips(
                page, PdfPageRect.FromContentPoints(page.PageNumber, field.Rect!.Value), unitsPerPoint);

            var input = FormFieldInputFactory.Build(
                field, Math.Max(dips.Width, 4), Math.Max(dips.Height, 4), tabIndex, this);
            if (input == null) continue;

            input.Classes.Add(ContinuousFormFieldClass);
            Canvas.SetLeft(input, dips.X);
            Canvas.SetTop(input, dips.Y);
            slot.FormFieldControls.Add(input);
        }

        slot.FormFieldsBuiltForZoom = ZoomLevel;
        slot.FormFieldsSignature = FormFieldInputFactory.FormFieldSetSignature(ordered);
    }

    private static void ClearContinuousFormFields(PdfPageSlot slot)
    {
        if (slot.FormFieldControls.Count > 0) slot.FormFieldControls.Clear();
        slot.FormFieldsBuiltForZoom = double.NaN;
        slot.FormFieldsSignature = 0;
    }

    /// <summary>
    /// A page's field set changed (a field was added, the document was edited): rebuild the
    /// slots whose signature no longer matches. Slots whose fields are unchanged keep their
    /// inputs, so scrolling, which re-raises <see cref="FormFields"/>, never steals the
    /// focus from a box being typed in.
    /// </summary>
    private void RefreshContinuousFormFieldsIfChanged()
    {
        if (ViewMode != PdfViewMode.Continuous || ContinuousItems == null || _continuousSlots == null)
            return;

        var provider = PageFormFieldsProvider;
        foreach (var container in ContinuousItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageSlot slot) continue;
            if (provider == null) { ClearContinuousFormFields(slot); continue; }
            try
            {
                if (FormFieldInputFactory.FormFieldSetSignature(
                        FormFieldInputFactory.OrderFormFieldsForTabbing(provider(slot.PageNumber)))
                    != slot.FormFieldsSignature)
                {
                    slot.FormFieldsBuiltForZoom = double.NaN;
                }
            }
            catch (Exception) { slot.FormFieldsBuiltForZoom = double.NaN; }
            SyncContinuousSlotFormFields(slot);
        }
    }
}
