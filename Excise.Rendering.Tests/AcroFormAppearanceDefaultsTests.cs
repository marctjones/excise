using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Rendering.Tests;

public sealed class AcroFormAppearanceDefaultsTests
{
    [Fact]
    public void Defaults_ResolveDocumentAppearanceAndResourcesOnce()
    {
        using var document = PdfDocument.CreateNew();
        var resources = new PdfDictionary { ["Marker"] = new PdfName("Original") };
        document.Catalog["AcroForm"] = new PdfDictionary
        {
            ["DA"] = new PdfString("/Helv 12 Tf 0 g"),
            ["DR"] = resources,
        };
        var defaults = new AcroFormAppearanceDefaults(document);

        Assert.Equal("/Helv 12 Tf 0 g", defaults.DefaultAppearance);
        Assert.Same(resources, defaults.Resources);

        document.Catalog["AcroForm"] = new PdfDictionary
        {
            ["DA"] = new PdfString("/Other 20 Tf"),
            ["DR"] = new PdfDictionary(),
        };
        Assert.Equal("/Helv 12 Tf 0 g", defaults.DefaultAppearance);
        Assert.Same(resources, defaults.Resources);
    }

    [Fact]
    public void Defaults_MissingAcroFormReturnsEmptyDefaults()
    {
        using var document = PdfDocument.CreateNew();
        var defaults = new AcroFormAppearanceDefaults(document);

        Assert.Null(defaults.DefaultAppearance);
        Assert.Null(defaults.Resources);
    }
}
