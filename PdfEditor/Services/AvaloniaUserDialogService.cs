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

    public async Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            _logger.LogWarning("Could not show text prompt: Main window not found. Prompt was: {Message}", message);
            return defaultValue;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var textBox = new TextBox
        {
            Text = defaultValue ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 80,
            MaxWidth = 410
        };

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            Padding = new Thickness(24, 5)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(24, 5)
        };

        okButton.Click += (_, _) => dialog.Close(textBox.Text);
        cancelButton.Click += (_, _) => dialog.Close(null);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 410
                },
                textBox,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton }
                }
            }
        };

        return await dialog.ShowDialog<string?>(mainWindow);
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
