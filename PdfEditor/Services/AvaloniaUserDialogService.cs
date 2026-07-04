using Avalonia;
using Avalonia.Automation;
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
        AutomationProperties.SetName(messageText, title);
        AutomationProperties.SetHelpText(messageText, message);

        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(30, 5),
            Margin = new Thickness(0, 10, 0, 0),
            IsDefault = true,
            IsCancel = true
        };
        AutomationProperties.SetName(okButton, $"OK - {title}");
        AutomationProperties.SetHelpText(okButton, "Close this message.");

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
        AutomationProperties.SetName(textBox, title);
        AutomationProperties.SetHelpText(textBox, message);

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(okButton, $"OK - {title}");
        AutomationProperties.SetHelpText(okButton, "Accept the entered value.");

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(cancelButton, $"Cancel - {title}");
        AutomationProperties.SetHelpText(cancelButton, "Close this prompt without applying a value.");

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

    public async Task<string?> PromptPasswordAsync(string title, string message)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            _logger.LogWarning("Could not show password prompt: Main window not found. Prompt was: {Message}", message);
            return null;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var passwordBox = new TextBox
        {
            PasswordChar = '*',
            MaxWidth = 410
        };
        AutomationProperties.SetName(passwordBox, title);
        AutomationProperties.SetHelpText(passwordBox, message);

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(okButton, $"OK - {title}");
        AutomationProperties.SetHelpText(okButton, "Accept the entered password.");

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(cancelButton, $"Cancel - {title}");
        AutomationProperties.SetHelpText(cancelButton, "Close this password prompt without applying a password.");

        okButton.Click += (_, _) => dialog.Close(passwordBox.Text ?? string.Empty);
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
                passwordBox,
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
