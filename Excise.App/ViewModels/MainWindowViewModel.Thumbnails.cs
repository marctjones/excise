using Excise.Core.Document;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// Stable thumbnail binding surface. <see cref="ThumbnailSidebarSession"/>
/// owns cache and background-work lifetime; page-operation selection remains
/// a workspace concern in <see cref="MainWindowViewModel"/>.
/// </summary>
public partial class MainWindowViewModel
{
    internal const int ThumbnailPrefetchMargin = ThumbnailSidebarSession.PrefetchMargin;
    internal const int ThumbnailKeepMargin = ThumbnailSidebarSession.KeepMargin;

    internal Task? ThumbnailPrefetchTask => _thumbnailSession.PrefetchTask;
    internal Task? ThumbnailPrewarmTask => _thumbnailSession.PrewarmTask;

    internal bool ThumbnailPrewarmEnabled
    {
        get => _thumbnailSession.PrewarmEnabled;
        set => _thumbnailSession.PrewarmEnabled = value;
    }

    internal static (int PrefetchFrom, int PrefetchTo, int KeepFrom, int KeepTo) ComputeThumbnailWindow(
        int visibleMin,
        int visibleMax,
        int pageCount,
        int prefetchMargin = ThumbnailPrefetchMargin,
        int keepMargin = ThumbnailKeepMargin) =>
        ThumbnailSidebarSession.ComputeWindow(
            visibleMin,
            visibleMax,
            pageCount,
            prefetchMargin,
            keepMargin);

    public void NotifyThumbnailViewport(int pageIndex, bool isVisible) =>
        _thumbnailSession.NotifyViewport(pageIndex, isVisible);

    public Task EnsureThumbnailLoadedAsync(
        int pageIndex,
        CancellationToken cancellationToken = default) =>
        _thumbnailSession.EnsureLoadedAsync(pageIndex, cancellationToken);

    private void StartThumbnailSession(
        string filePath,
        PdfDocument document,
        string? cacheSalt = null)
    {
        _thumbnailSession.Start(
            filePath,
            document,
            document.PageCount,
            AttachPageSelectionTracking,
            cacheSalt);
        UpdateThumbnailSelection();
        RaiseSelectedPagePropertiesChanged();
    }

    private void ResetThumbnailSession() => _thumbnailSession.Reset();
}
