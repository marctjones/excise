using Avalonia;
using Excise.App.ViewModels;
using Excise.Core.Document;

namespace Excise.App.Tests.Utilities;

/// <summary>Marks a viewer-space <see cref="Rect"/>; production marks with a <see cref="PdfPageRect"/>.</summary>
internal static class RedactionWorkflowManagerTestExtensions
{
    public static void MarkArea(
        this RedactionWorkflowManager manager,
        int pageNumber,
        Rect area,
        string previewText,
        int renderDpi = MainWindowViewModel.DefaultViewerRenderDpi) =>
        manager.MarkArea(
            PdfPageRect.ViewerDips(pageNumber, area.X, area.Y, area.Width, area.Height, renderDpi),
            previewText);
}
