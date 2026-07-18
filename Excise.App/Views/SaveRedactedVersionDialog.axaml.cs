using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Excise.App.ViewModels;

namespace Excise.App.Views;

public partial class SaveRedactedVersionDialog : Window
{
    public string? ResultFilePath { get; private set; }

    public SaveRedactedVersionDialog()
    {
        InitializeComponent();
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SaveRedactedVersionDialogViewModel viewModel)
            return;

        var file = await ShowSaveFileDialog(viewModel.SaveFilePath);

        if (file != null)
        {
            viewModel.SaveFilePath = file.Path.LocalPath;
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SaveRedactedVersionDialogViewModel viewModel)
        {
            ResultFilePath = viewModel.SaveFilePath;
        }

        Close(ResultFilePath);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        ResultFilePath = null;
        Close(null);
    }

    private async Task<IStorageFile?> ShowSaveFileDialog(string suggestedFileName)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "Save Redacted PDF",
            SuggestedFileName = System.IO.Path.GetFileName(suggestedFileName),
            DefaultExtension = "pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Document")
                {
                    Patterns = new[] { "*.pdf" },
                    MimeTypes = new[] { "application/pdf" }
                }
            }
        };

        // Try to set the suggested directory
        try
        {
            var dir = System.IO.Path.GetDirectoryName(suggestedFileName);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
            }
        }
        catch
        {
            // Ignore errors, will use default location
        }

        return await StorageProvider.SaveFilePickerAsync(options);
    }
}
