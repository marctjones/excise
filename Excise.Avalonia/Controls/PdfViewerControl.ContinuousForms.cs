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
/// <see cref="BuildFormFieldInput"/> the single-page overlay uses, so the two
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

    /// <summary>Order fields the way a person reads a form: top to bottom, then left to right.</summary>
    private static List<PdfField> OrderFormFieldsForTabbing(IEnumerable<PdfField> fields) => fields
        .Where(field => field.Rect.HasValue)
        .OrderByDescending(field => field.Rect!.Value.Top)
        .ThenBy(field => field.Rect!.Value.Left)
        .ThenBy(field => field.FullName, StringComparer.Ordinal)
        .ToList();

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
        var ordered = OrderFormFieldsForTabbing(fields);
        for (var tabIndex = 0; tabIndex < ordered.Count; tabIndex++)
        {
            var field = ordered[tabIndex];
            var dips = PdfCoordinateMapper.ToContinuousDips(
                page, PdfPageRect.FromContentPoints(page.PageNumber, field.Rect!.Value), unitsPerPoint);

            var input = BuildFormFieldInput(
                field, Math.Max(dips.Width, 12), Math.Max(dips.Height, 12), tabIndex);
            if (input == null) continue;

            input.Classes.Add(ContinuousFormFieldClass);
            Canvas.SetLeft(input, dips.X);
            Canvas.SetTop(input, dips.Y);
            slot.FormFieldControls.Add(input);
        }

        slot.FormFieldsBuiltForZoom = ZoomLevel;
        slot.FormFieldsSignature = FormFieldSetSignature(ordered);
    }

    private static void ClearContinuousFormFields(PdfPageSlot slot)
    {
        if (slot.FormFieldControls.Count > 0) slot.FormFieldControls.Clear();
        slot.FormFieldsBuiltForZoom = double.NaN;
        slot.FormFieldsSignature = 0;
    }

    /// <summary>Names and rectangles only: a typed value must not look like a changed page.</summary>
    private static int FormFieldSetSignature(IEnumerable<PdfField> fields)
    {
        var hash = new HashCode();
        foreach (var field in fields)
        {
            hash.Add(field.FullName, StringComparer.Ordinal);
            hash.Add(field.Rect);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// A page's field set changed (a field was added, the document was edited): rebuild the
    /// slots whose signature no longer matches. Slots whose fields are unchanged keep their
    /// inputs, so scrolling, which re-raises <see cref="FormFields"/>, never steals the
    /// focus from a box being typed in.
    /// </summary>
    private void RefreshContinuousFormFieldsIfChanged()
    {
        if (ViewMode != PdfViewMode.Continuous || _continuousItems == null || _continuousSlots == null)
            return;

        var provider = PageFormFieldsProvider;
        foreach (var container in _continuousItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageSlot slot) continue;
            if (provider == null) { ClearContinuousFormFields(slot); continue; }
            try
            {
                if (FormFieldSetSignature(OrderFormFieldsForTabbing(provider(slot.PageNumber)))
                    != slot.FormFieldsSignature)
                {
                    slot.FormFieldsBuiltForZoom = double.NaN;
                }
            }
            catch (Exception) { slot.FormFieldsBuiltForZoom = double.NaN; }
            SyncContinuousSlotFormFields(slot);
        }
    }

    /// <summary>
    /// True when the pointer event came from a continuous-view field input. The root handlers
    /// listen with handledEventsToo, so without this a press in a field would also start a
    /// text-selection drag and the field would never take focus.
    /// </summary>
    private bool IsFormFieldOverlayEvent(PointerEventArgs e)
    {
        if (InteractionMode is not (InteractionMode.None or InteractionMode.TextSelection))
            return false;

        for (var current = e.Source as StyledElement; current != null; current = current.Parent)
        {
            if (current is Control control && control.Classes.Contains(ContinuousFormFieldClass))
                return true;
        }
        return false;
    }
}
