using System;

namespace Excise.App.Services;

/// <summary>
/// Service for displaying toast notifications (error, info).
/// Provides a clean interface for ViewModels to request toast displays.
/// </summary>
public class ToastService
{
    /// <summary>
    /// Event args for toast notifications.
    /// </summary>
    public class ToastEventArgs : EventArgs
    {
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
        public ToastSeverity Severity { get; set; }
    }

    /// <summary>
    /// Toast severity level.
    /// </summary>
    public enum ToastSeverity
    {
        Informational,
        Warning,
        Error,
        Success
    }

    /// <summary>
    /// Fired when a toast should be displayed.
    /// Subscribers typically display the toast in the UI for 5 seconds.
    /// </summary>
    public event EventHandler<ToastEventArgs>? ToastRequested;

    /// <summary>
    /// Show an error toast notification.
    /// </summary>
    public void ShowError(string message, string? details = null)
    {
        Show(message, details, ToastSeverity.Error);
    }

    /// <summary>
    /// Show an informational toast notification.
    /// </summary>
    public void ShowInfo(string message, string? details = null)
    {
        Show(message, details, ToastSeverity.Informational);
    }

    /// <summary>
    /// Show a warning toast notification.
    /// </summary>
    public void ShowWarning(string message, string? details = null)
    {
        Show(message, details, ToastSeverity.Warning);
    }

    /// <summary>
    /// Show a success toast notification.
    /// </summary>
    public void ShowSuccess(string message, string? details = null)
    {
        Show(message, details, ToastSeverity.Success);
    }

    /// <summary>
    /// Internal method to show a toast with specified severity.
    /// </summary>
    private void Show(string message, string? details, ToastSeverity severity)
    {
        ToastRequested?.Invoke(this, new ToastEventArgs
        {
            Message = message,
            Details = details,
            Severity = severity
        });
    }
}
