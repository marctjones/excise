using Avalonia.Media.Imaging;
using ReactiveUI;

namespace Excise.App.Models;

public class PageThumbnail : ReactiveObject
{
    private Bitmap? _thumbnailImage;
    private bool _isSelected;
    private bool _isMarkedForPageOperation;

    public int PageNumber { get; set; }
    public int PageIndex { get; set; }

    public Bitmap? ThumbnailImage
    {
        get => _thumbnailImage;
        set => this.RaiseAndSetIfChanged(ref _thumbnailImage, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public bool IsMarkedForPageOperation
    {
        get => _isMarkedForPageOperation;
        set => this.RaiseAndSetIfChanged(ref _isMarkedForPageOperation, value);
    }
}
