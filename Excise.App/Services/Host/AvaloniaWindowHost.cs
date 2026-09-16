using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Excise.App.Services.Host;

/// <summary>
/// The production <see cref="IWindowHost"/>: the classic desktop lifetime's
/// main window, and the storage provider that window hosts.
/// </summary>
/// <remarks>
/// One instance per application (registered as a singleton), so that setting
/// <see cref="StorageProviderOverride"/> through the view model's forwarding
/// property steers the same host the file picker resolves from.
/// </remarks>
internal sealed class AvaloniaWindowHost : IWindowHost
{
    /// <inheritdoc />
    public IStorageProvider? StorageProviderOverride { get; set; }

    /// <inheritdoc />
    public Func<Window?> MainWindowResolver { get; set; } = ResolveDesktopMainWindow;

    /// <inheritdoc />
    public Window? MainWindow => MainWindowResolver();

    /// <inheritdoc />
    public IStorageProvider? StorageProvider => StorageProviderOverride ?? MainWindow?.StorageProvider;

    /// <inheritdoc />
    public void RequestShutdown() => DesktopLifetime?.TryShutdown();

    private static IClassicDesktopStyleApplicationLifetime? DesktopLifetime =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static Window? ResolveDesktopMainWindow() => DesktopLifetime?.MainWindow;
}
