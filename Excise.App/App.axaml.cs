using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Excise.App;

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

        MainWindowViewModel? mainViewModel = null;
        string? pendingActivationPath = null;

        void OpenOrQueueActivatedPath(string path)
        {
            if (mainViewModel != null)
            {
                OpenPathOnUiThread(mainViewModel, path, logger);
                return;
            }

            logger.LogInformation("Queued activated PDF until main window is ready: {Path}", path);
            pendingActivationPath = path;
        }

        // Register this before building the main window. macOS Launch Services
        // may deliver document-open activation while the Avalonia lifetime is
        // still starting, and queueing here avoids dropping that early event.
        if (ApplicationLifetime is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is not FileActivatedEventArgs fileArgs)
                    return;

                var path = ResolveActivatedPdfPath(fileArgs.Files);
                if (path != null)
                    OpenOrQueueActivatedPath(path);
            };
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            logger.LogInformation("Creating main window");

            var vm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            mainViewModel = vm;
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            logger.LogInformation("Main window created successfully");

            // Open a PDF that was passed on the command line (Windows/Linux
            // "Open With", `excise file.pdf`, demos). Avalonia's Window.Opened
            // event is not a reliable handoff point for every backend, so post
            // the load directly to the dispatcher after the VM/window exist.
            // On macOS
            // a double-clicked file does NOT arrive as a command-line arg — it
            // comes through the activation event wired up below.
            var processArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var responsivenessReportPath = StartupDocumentResolver.ResolveResponsivenessReportPath(
                desktop.Args,
                processArgs)
                ?? ResponsivenessReportWriter.ConsumeOneShotReportRequest(logger);
            if (!string.IsNullOrWhiteSpace(responsivenessReportPath))
            {
                Environment.SetEnvironmentVariable(
                    ResponsivenessReportWriter.ReportPathEnvironmentVariable,
                    responsivenessReportPath);
                logger.LogInformation("Configured responsiveness report from startup args: {Path}",
                    responsivenessReportPath);
            }

            var path = StartupDocumentResolver.Resolve(
                desktop.Args,
                processArgs);

            if (path != null)
            {
                logger.LogInformation("Opening startup PDF: {Path}", path);
                desktop.Startup += (_, _) =>
                {
                    DispatcherTimer.RunOnce(
                        () => OpenPathOnUiThread(vm, path, logger),
                        TimeSpan.FromMilliseconds(250),
                        DispatcherPriority.Background);
                };
            }

            if (pendingActivationPath != null)
            {
                var pathToOpen = pendingActivationPath;
                pendingActivationPath = null;
                logger.LogInformation("Opening queued activated PDF: {Path}", pathToOpen);
                desktop.Startup += (_, _) =>
                {
                    DispatcherTimer.RunOnce(
                        () => OpenPathOnUiThread(vm, pathToOpen, logger),
                        TimeSpan.FromMilliseconds(250),
                        DispatcherPriority.Background);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Load a PDF on the UI thread, logging (not throwing) on failure. Shared by
    /// the command-line and OS file-activation open paths.
    /// </summary>
    private static void OpenPathOnUiThread(MainWindowViewModel vm, string path, ILogger logger)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = OpenPathAsync(vm, path, logger);
            return;
        }

        Dispatcher.UIThread.Post(() => _ = OpenPathAsync(vm, path, logger));
    }

    private static async Task OpenPathAsync(MainWindowViewModel vm, string path, ILogger logger)
    {
        try
        {
            logger.LogInformation("Loading PDF from startup/open event: {Path}", path);
            await vm.LoadDocumentAsync(path);
            logger.LogInformation("Loaded PDF from startup/open event: {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open {Path}", path);
        }
    }

    private static string? ResolveActivatedPdfPath(IReadOnlyList<IStorageItem> files)
    {
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (!string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            if (System.IO.File.Exists(path))
                return System.IO.Path.GetFullPath(path);
        }

        return null;
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            // EXCISE_LOG_LEVEL=Debug|Trace|Information… overrides the minimum
            // level — used for live execution-path tracing of GUI sessions.
            builder.SetMinimumLevel(
                Enum.TryParse<LogLevel>(
                    Environment.GetEnvironmentVariable("EXCISE_LOG_LEVEL"), true, out var lvl)
                    ? lvl : LogLevel.Information);

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
        services.AddSingleton<RedactedCopySafetyService>();
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
            "Services registered: PdfDocumentService, PdfRenderService, RedactionService, RedactedCopySafetyService, PdfTextExtractionService, PdfSearchService, SignatureVerificationService, PageOrganizationWorkflowService, AnnotationWorkflowService");
        logger.LogInformation("Logging level set to: INFO");
    }
}

public class BooleanToBrushConverter : global::Avalonia.Data.Converters.IValueConverter
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
