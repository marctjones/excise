using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace Excise.App.Services;

public sealed class AvaloniaUserDialogService : IUserDialogService
{
    private readonly ILogger<AvaloniaUserDialogService> _logger;
    private readonly System.Func<Window?>? _ownerResolver;

    public AvaloniaUserDialogService(ILogger<AvaloniaUserDialogService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// #1551: a document session's dialogs are owned by that session's window.
    /// <paramref name="ownerResolver"/> is the session's
    /// <see cref="Host.IWindowHost"/>; null falls back to the desktop main window.
    /// </summary>
    internal AvaloniaUserDialogService(ILogger<AvaloniaUserDialogService> logger, Host.IWindowHost? windowHost)
        : this(logger)
    {
        if (windowHost != null)
            _ownerResolver = () => windowHost.MainWindow;
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
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 400
        };
        AutomationProperties.SetName(messageText, title);
        AutomationProperties.SetHelpText(messageText, message);

        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
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
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
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
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 410
                },
                textBox,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
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
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 410
                },
                passwordBox,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton }
                }
            }
        };

        return await dialog.ShowDialog<string?>(mainWindow);
    }

    /// <summary>
    /// Yes/No confirmation for a consequential action. Unlike
    /// <see cref="PromptTextAsync"/>/<see cref="PromptPasswordAsync"/> (where
    /// the affirmative button is the default), the Cancel button here is both
    /// <c>IsDefault</c> and <c>IsCancel</c> — the caller decides whether the
    /// action is destructive enough that Enter should not trigger it. Safe to
    /// reuse for any confirm dialog with that property.
    /// </summary>
    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            _logger.LogWarning("Could not show confirm dialog: Main window not found. Message was: {Message}", message);
            return false;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 410
        };
        AutomationProperties.SetName(messageText, title);
        AutomationProperties.SetHelpText(messageText, message);

        var continueButton = new Button
        {
            Content = "Continue",
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(continueButton, $"Continue - {title}");
        AutomationProperties.SetHelpText(continueButton, "Proceed with this action.");

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsDefault = true,
            IsCancel = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(cancelButton, $"Cancel - {title}");
        AutomationProperties.SetHelpText(cancelButton, "Do not proceed with this action.");

        continueButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                messageText,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, continueButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(mainWindow);
    }

    /// <summary>
    /// Three-way Save / Discard / Cancel prompt for unsaved document changes
    /// (#1233).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Button roles are chosen so that neither of the two keys a user hits
    /// reflexively can destroy work:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Enter</b> (<c>IsDefault</c>) is the SAVE button —
    /// the issue requires Save-a-Copy to be the primary action for an original
    /// source.</description></item>
    /// <item><description><b>Escape</b> (<c>IsCancel</c>) is Cancel, and
    /// closing the dialog by any other means also yields
    /// <see cref="UnsavedChangesDecision.Cancel"/> via the
    /// <c>ShowDialog</c> default.</description></item>
    /// <item><description><b>Discard</b> is neither, so it takes a deliberate
    /// click.</description></item>
    /// </list>
    /// <para>
    /// This inverts <see cref="ShowConfirmAsync"/>'s "Cancel is IsDefault"
    /// choice on purpose: there, Enter would perform a destructive action;
    /// here, Enter performs the SAFE one.
    /// </para>
    /// </remarks>
    public async Task<UnsavedChangesDecision> ShowUnsavedChangesAsync(
        string title, string message, string saveActionText)
    {
        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            // Fail-closed toward keeping the document: never report "discard"
            // just because there was nobody to ask.
            _logger.LogWarning(
                "Could not show unsaved-changes dialog: Main window not found. Message was: {Message}",
                message);
            return UnsavedChangesDecision.Cancel;
        }

        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 470
        };
        AutomationProperties.SetName(messageText, title);
        AutomationProperties.SetHelpText(messageText, message);

        var saveButton = new Button
        {
            Content = string.IsNullOrWhiteSpace(saveActionText) ? "Save a Copy" : saveActionText,
            IsDefault = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(saveButton, $"{saveButton.Content} - {title}");
        AutomationProperties.SetHelpText(saveButton, "Save the changes, then continue. The original file is not overwritten.");

        var discardButton = new Button
        {
            Content = "Discard Changes",
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(discardButton, $"Discard Changes - {title}");
        AutomationProperties.SetHelpText(discardButton, "Continue and permanently lose the unsaved changes.");

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(24, 5)
        };
        AutomationProperties.SetName(cancelButton, $"Cancel - {title}");
        AutomationProperties.SetHelpText(cancelButton, "Keep the document open with its unsaved changes.");

        saveButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Save);
        discardButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Discard);
        cancelButton.Click += (_, _) => dialog.Close(UnsavedChangesDecision.Cancel);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                messageText,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, discardButton, saveButton }
                }
            }
        };

        return await dialog.ShowDialog<UnsavedChangesDecision>(mainWindow);
    }

    private Window? GetMainWindow()
    {
        if (_ownerResolver?.Invoke() is { } owner)
            return owner;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}
