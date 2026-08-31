using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.Rendering;
using System.Reflection;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Guards the rendering boundary: the reusable viewer owns interactive
/// scheduling and bitmap retention, while the App workflow owns only export.
/// </summary>
public class ViewerOwnsDisplayRenderingTests
{
    [Fact]
    public void MainWindowViewModel_HasNoDisplayRendererOrBitmapCacheDependency()
    {
        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        var constructorParameters = typeof(MainWindowViewModel)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        fields.Should().NotContain(field =>
            field.FieldType == typeof(SkiaRenderer) ||
            field.FieldType == typeof(IPageImageRenderer));
        constructorParameters.Should().NotContain(typeof(IPageImageRenderer));
        typeof(MainWindowViewModel).GetProperty("CurrentPageImage").Should().BeNull(
            "PdfViewerControl owns display bitmaps; the ViewModel must not revive the removed parallel display path");
    }
}
