using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Where a form-field input built for either view reports an edit (#1842,
/// <c>docs/architecture/pdf-viewer-control-architecture.md</c> §3.2). The viewer
/// implements it; the shared input factory calls it, so the single-page layer
/// and the continuous slots cannot store or report a field value differently
/// (#1807).
/// </summary>
internal interface IFormFieldEditSink
{
    /// <summary>The host's pre-store permission check (#1874): false refuses the edit.</summary>
    bool AdmitEdit();

    /// <summary>
    /// The value was stored. The sink resolves the page it reports (the field's
    /// own, else the viewer's current page) when this is called, never when the
    /// input was built: a continuous slot's inputs outlive page changes.
    /// </summary>
    void EditStored(PdfField field, string? newValue, string? oldValue);

    /// <summary>The field refused the value (#1671): it was not stored.</summary>
    void EditRejected(string fieldName, string message);
}
