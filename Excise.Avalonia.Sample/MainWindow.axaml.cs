using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Excise.Avalonia.Controls;
using Excise.Core.Document;

namespace Excise.Avalonia.Sample;

/// <summary>
/// Minimal reference: host <see cref="PdfViewerControl"/>, open a file, and
/// drive navigation/zoom through the control's public API. No MVVM, no app
/// services — this is everything you need to embed a PDF viewer.
/// </summary>
public partial class MainWindow : Window
{
    private PdfViewerControl _viewer = null!;
    private TextBlock _pageLabel = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _viewer = this.FindControl<PdfViewerControl>("Viewer")!;
        _pageLabel = this.FindControl<TextBlock>("PageLabel")!;
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        // Excise.Core opens the document; hand it straight to the control.
        _viewer.Document = PdfDocument.Open(file.Path.LocalPath);
        _viewer.CurrentPage = 1;
        UpdateLabel();
    }

    private void OnPrev(object? sender, RoutedEventArgs e) { _viewer.PreviousPage(); UpdateLabel(); }
    private void OnNext(object? sender, RoutedEventArgs e) { _viewer.NextPage(); UpdateLabel(); }
    private void OnZoomIn(object? sender, RoutedEventArgs e) => _viewer.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e) => _viewer.ZoomOut();
    private void OnActualSize(object? sender, RoutedEventArgs e) => _viewer.ZoomToActualSize();

    private void OnPageChanged(object? sender, PageChangedEventArgs e) => UpdateLabel();

    private void UpdateLabel()
        => _pageLabel.Text = _viewer.Document is null
            ? "No document"
            : $"Page {_viewer.CurrentPage} / {_viewer.Document.PageCount}";
}
