using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Excise.App.Workspace;

/// <summary>
/// Creates a <see cref="DocumentSession"/>: one dependency-injection scope per
/// document (#1551).
/// </summary>
internal sealed class DocumentSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    internal DocumentSessionFactory(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    internal DocumentSession Create(DocumentWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var scope = _scopeFactory.CreateScope();
        try
        {
            var viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();
            var windowHost = scope.ServiceProvider.GetRequiredService<IWindowHost>();
            return new DocumentSession(workspace, scope, viewModel, windowHost, ActiveDesktopWindow);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The owner to use while a session has no window of its own yet: the
    /// active desktop window, else the lifetime's main window.
    /// </summary>
    internal static Window? ActiveDesktopWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        foreach (var window in desktop.Windows)
        {
            if (window.IsActive)
                return window;
        }

        return desktop.MainWindow;
    }
}
