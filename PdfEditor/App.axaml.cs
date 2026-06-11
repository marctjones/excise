using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using System;

namespace PdfEditor;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ReactiveUI is wired into Avalonia's dispatcher in Program.cs via
        // AppBuilder.UseReactiveUI(b => b.WithAvalonia()) — that's the
        // RxUI-23 + ReactiveUI.Avalonia-12 replacement for the old
        // RxApp.MainThreadScheduler = AvaloniaScheduler.Instance assignment.

        // Configure dependency injection and logging
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("=================================================");
        logger.LogInformation("PDF Editor Application Starting");
        logger.LogInformation("=================================================");
        logger.LogInformation("Framework initialization completed");
        logger.LogInformation("ReactiveUI configured to use Avalonia scheduler");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            logger.LogInformation("Creating main window");

            var vm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            logger.LogInformation("Main window created successfully");

            // Open a PDF that was passed on the command line once the window is
            // shown (Windows/Linux "Open With", `pdfe file.pdf`, demos). On macOS
            // a double-clicked file does NOT arrive as a command-line arg — it
            // comes through the activation event wired up below.
            var args = desktop.Args;
            if (args != null && args.Length > 0 && System.IO.File.Exists(args[0]))
            {
                var path = args[0];
                desktop.MainWindow.Opened += (_, _) => OpenPathOnUiThread(vm, path, logger);
            }
        }

        // macOS (and other platforms) deliver "open this document" as an
        // activation event, not a command-line arg — Finder double-click, the
        // Dock, or `open -a pdfe file.pdf` against an already-running instance all
        // come through here. Requires /CFBundleDocumentTypes in the .app's
        // Info.plist so the OS routes PDFs to us. (#420)
        if (ApplicationLifetime is IActivatableLifetime activatable
            && _serviceProvider != null)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is not FileActivatedEventArgs fileArgs)
                    return;

                // Resolve the VM lazily: on a warm activation the main window's
                // VM is the live one; fall back to a fresh resolve if needed.
                var vm = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                             ?.MainWindow?.DataContext as MainWindowViewModel
                         ?? _serviceProvider.GetRequiredService<MainWindowViewModel>();

                foreach (var file in fileArgs.Files)
                {
                    var path = file.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        OpenPathOnUiThread(vm, path, logger);
                        break;   // single-window app: open the first document
                    }
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Load a PDF on the UI thread, logging (not throwing) on failure. Shared by
    /// the command-line and OS file-activation open paths.
    /// </summary>
    private static void OpenPathOnUiThread(MainWindowViewModel vm, string path, ILogger logger)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await vm.LoadDocumentAsync(path); }
            catch (Exception ex) { logger.LogError(ex, "Failed to open {Path}", path); }
        });
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);

            // Configure console formatter for better readability
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = false;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });

        // Explicitly register ILoggerFactory as singleton (required by RedactionService)
        services.AddSingleton<ILoggerFactory, LoggerFactory>();

        // Register services
        services.AddSingleton<PdfDocumentService>();
        services.AddSingleton<PdfRenderService>();
        services.AddSingleton<RedactionService>();
        services.AddSingleton<PdfTextExtractionService>();
        services.AddSingleton<PdfSearchService>();
        services.AddSingleton<SignatureVerificationService>();
        services.AddSingleton<SignatureVerificationSummaryFormatter>();
        services.AddSingleton<SignatureVerificationWorkflowService>();
        services.AddSingleton<PageOrganizationWorkflowService>();
        services.AddSingleton<AnnotationWorkflowService>();
        services.AddSingleton<FilenameSuggestionService>();
        services.AddSingleton<IUserDialogService, AvaloniaUserDialogService>();

        // Register ViewModels
        services.AddTransient<MainWindowViewModel>();

        var tempProvider = services.BuildServiceProvider();
        var logger = tempProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Dependency injection container configured");
        logger.LogInformation(
            "Services registered: PdfDocumentService, PdfRenderService, RedactionService, PdfTextExtractionService, PdfSearchService, SignatureVerificationService, PageOrganizationWorkflowService, AnnotationWorkflowService");
        logger.LogInformation("Logging level set to: INFO");
    }
}

public class BooleanToBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not bool boolValue)
            return Brushes.Transparent;

        var paramString = parameter?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(paramString))
            return boolValue ? Brushes.Green : Brushes.Transparent;

        var parts = paramString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var trueBrush = parts.Length > 0 ? parts[0] : "Green";
        var falseBrush = parts.Length > 1 ? parts[1] : "Transparent";

        return boolValue ? Brush.Parse(trueBrush) : Brush.Parse(falseBrush);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
