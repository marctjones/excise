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
    /// #1551: a document session's dialogs are owned by that session's window,
    /// as <paramref name="windowHost"/> resolves it. With no host, or no window
    /// yet, the desktop main window is the owner, as before.
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
            // Was Height = 200. MinHeight keeps the one-line case exactly as
            // large as before; SizeToContent grows it for a long report (#1622).
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
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

        SetScrollingContent(dialog, messageText, okButton, MessageContentFloor);

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
            MinHeight = 260,
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

        // The input and the buttons are the footer: a long prompt scrolls, the
        // field the user has to type in never scrolls away (#1622).
        SetScrollingContent(
            dialog,
            new TextBlock
            {
                Text = message,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 410
            },
            new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Children =
                {
                    textBox,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton }
                    }
                }
            },
            PromptContentFloor);

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
            MinHeight = 220,
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

        SetScrollingContent(
            dialog,
            new TextBlock
            {
                Text = message,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 410
            },
            new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Children =
                {
                    passwordBox,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton }
                    }
                }
            },
            PasswordContentFloor);

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
            MinHeight = 220,
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

        SetScrollingContent(
            dialog,
            messageText,
            new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                Spacing = 8,
                Children = { cancelButton, continueButton }
            },
            ConfirmContentFloor);

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
            MinHeight = 240,
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

        SetScrollingContent(
            dialog,
            messageText,
            new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                Spacing = 8,
                Children = { cancelButton, discardButton, saveButton }
            },
            UnsavedChangesContentFloor);

        return await dialog.ShowDialog<UnsavedChangesDecision>(mainWindow);
    }


    /// <summary>
    /// The tallest a message dialog may grow before its text starts scrolling
    /// instead. Well under any modern display, and the message is scrollable
    /// past it, so this cannot hide content.
    /// </summary>
    private const double MaxDialogHeight = 640;

    /// <summary>
    /// Content-height floors, one per dialog: the height each dialog used to be
    /// fixed at, less the 20 px margin on each side. A short message therefore
    /// opens the same size as it always did.
    /// </summary>
    private const double MessageContentFloor = 160;
    private const double PromptContentFloor = 220;
    private const double PasswordContentFloor = 180;
    private const double ConfirmContentFloor = 180;
    private const double UnsavedChangesContentFloor = 200;

    /// <summary>
    /// #1622: lay a dialog out so a LONG message cannot push the buttons off
    /// the bottom of the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dialog here used to be a fixed-size <see cref="Window"/>
    /// (<c>CanResize=false</c>) holding a <see cref="StackPanel"/> of message
    /// plus buttons. A StackPanel gives its children unbounded height in its
    /// orientation, so a message taller than the window simply overflowed and
    /// the buttons went with it. Measured on the #1586 redacted-copy report:
    /// the text block laid out 400x420 inside a 450x228 window with the OK
    /// button at y=493 — the "this copy is NO LONGER accessible or
    /// interactive" warning and the button were both off-screen, and the
    /// report was only readable through the accessibility tree.
    /// </para>
    /// <para>
    /// Two changes fix it together, and neither is sufficient alone:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a <c>*,Auto</c> <see cref="Grid"/> instead of a
    /// StackPanel, so the footer row is measured FIRST and the message row
    /// gets only what is left. This is what keeps the buttons on screen.</description></item>
    /// <item><description><see cref="SizeToContent.Height"/> with a
    /// <see cref="Layoutable.MaxHeight"/>, so a short message keeps a small
    /// dialog (<see cref="Layoutable.MinHeight"/> preserves the old size) and
    /// a long one grows to the cap and then scrolls.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="minContentHeight">
    /// The floor on the CONTENT's height, not the window's. Measured 2026-09-17
    /// in a headless window: <c>Window.MinHeight</c> is NOT applied under
    /// <see cref="SizeToContent.Height"/> — a one-line message laid out 90 px
    /// tall with <c>MinHeight = 200</c> set, and raising that MinHeight to 520
    /// changed nothing. So the floor that keeps a short dialog from collapsing
    /// has to live on the content, where the size-to-content measure reads it.
    /// The window MinHeight is kept as well, for platforms that do honour it.
    /// </param>
    private static void SetScrollingContent(
        Window dialog, Control message, Control footer, double minContentHeight)
    {
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = MaxDialogHeight;

        var scroller = new ScrollViewer
        {
            Content = message,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var grid = new Grid
        {
            Margin = new Thickness(20),
            MinHeight = minContentHeight,
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(footer, 1);
        grid.Children.Add(scroller);
        grid.Children.Add(footer);
        dialog.Content = grid;
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
