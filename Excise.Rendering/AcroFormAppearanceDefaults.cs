using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering;

/// <summary>
/// Lazily owns the document-level AcroForm default appearance and resource
/// resolution for one render context. Widget synthesis borrows these values;
/// page and annotation appearance streams keep their existing resource stack.
/// </summary>
internal sealed class AcroFormAppearanceDefaults
{
    private readonly PdfDocument _document;
    private bool _resolved;
    private PdfDictionary? _resources;
    private string? _defaultAppearance;

    public AcroFormAppearanceDefaults(PdfDocument document)
    {
        _document = document;
    }

    public PdfDictionary? Resources
    {
        get
        {
            EnsureResolved();
            return _resources;
        }
    }

    public string? DefaultAppearance
    {
        get
        {
            EnsureResolved();
            return _defaultAppearance;
        }
    }

    private void EnsureResolved()
    {
        if (_resolved)
            return;

        _resolved = true;
        var acroFormObject = _document.Catalog.GetOptional("AcroForm");
        if (acroFormObject == null
            || _document.Resolve(acroFormObject) is not PdfDictionary acroForm)
        {
            return;
        }

        _defaultAppearance = acroForm.GetStringOrNull("DA");
        var resourcesObject = acroForm.GetOptional("DR");
        if (resourcesObject != null)
            _resources = _document.Resolve(resourcesObject) as PdfDictionary;
    }
}
