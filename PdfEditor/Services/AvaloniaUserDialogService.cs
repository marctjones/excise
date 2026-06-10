using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace PdfEditor.Services;

public sealed class AvaloniaUserDialogService : IUserDialogService
{
    private readonly ILogger<AvaloniaUserDialogService> _logger;

    public AvaloniaUserDialogService(ILogger<AvaloniaUserDialogService> logger)
    {
        _logger = logger;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            _logger.LogWarning("Could not show message dialog: Main window not found. Message was: {Message}", message);
            return;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 450,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 15
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 400
        };

        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(30, 5),
            Margin = new Thickness(0, 10, 0, 0)
        };

        okButton.Click += (_, _) => dialog.Close();

        panel.Children.Add(messageText);
        panel.Children.Add(okButton);
        dialog.Content = panel;

        await dialog.ShowDialog(mainWindow);
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}
