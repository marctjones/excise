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
    /// The value was stored. <paramref name="fallbackPage"/> is the viewer's current
    /// page at commit time, used only when the field does not know its own page.
    /// </summary>
    void EditStored(PdfField field, string? newValue, string? oldValue, int fallbackPage);

    /// <summary>The field refused the value (#1671): it was not stored.</summary>
    void EditRejected(string fieldName, string message);
}
