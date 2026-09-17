using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Excise.App.Services;
using Excise.App.Composition;
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
    private MetricsJsonlSink? _metricsSink;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // #834: set the macOS application menu here, during Initialize — BEFORE
        // Avalonia constructs its MenuTarget.Application exporter, whose one-shot
        // DoLayoutReset installs the default "About Avalonia" if no app menu is
        // present yet and never re-reads. Setting it in OnFrameworkInitialization-
        // Completed (even before the first window) is already too late. The items
        // dispatch to the current main window's view-model at click time, so no
        // view-model is needed this early.
        if (OperatingSystem.IsMacOS())
            NativeMenu.SetMenu(this, Views.MacApplicationMenu.Build(() => CurrentMainViewModel));
    }

    private Workspace.DocumentWorkspace? _workspace;

    // #1551: the app menu acts on the document in the ACTIVE window, not on
    // whichever window the lifetime calls "main".
    private MainWindowViewModel? CurrentMainViewModel =>
        _workspace?.ActiveSession?.ViewModel
        ?? (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext
            as MainWindowViewModel;

    public override void OnFrameworkInitializationCompleted()
    {
        // ReactiveUI is wired into Avalonia's dispatcher in Program.cs via
        // AppBuilder.UseReactiveUI(b => b.WithAvalonia()) — that's the
        // RxUI-23 + ReactiveUI.Avalonia-12 replacement for the old
        // RxApp.MainThreadScheduler = AvaloniaScheduler.Instance assignment.

        // Configure dependency injection and logging
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Dependency injection container configured and validated");
        logger.LogInformation("=================================================");
        logger.LogInformation("PDF Editor Application Starting");
        logger.LogInformation("=================================================");
        logger.LogInformation("Framework initialization completed");
        logger.LogInformation("ReactiveUI configured to use Avalonia scheduler");

        // #1491: EXCISE_TRACE_VIEWER=<path> writes live metrics as JSONL. Started
        // before the main window so the first document open is captured.
        _metricsSink = MetricsJsonlSink.TryStartFromEnvironment(logger);

        MainWindowViewModel? mainViewModel = null;
        var pendingActivationPaths = new List<string>();

        void OpenOrQueueActivatedPaths(IReadOnlyList<string> paths)
        {
            if (mainViewModel != null)
            {
                OpenPathsOnUiThread(mainViewModel, paths, logger);
                return;
            }

            logger.LogInformation("Queued {Count} activated PDF(s) until the main window is ready", paths.Count);
            pendingActivationPaths.AddRange(paths);
        }

        // Register this before building the main window. macOS Launch Services
        // may deliver document-open activation while the Avalonia lifetime is
        // still starting, and queueing here avoids dropping that early event.
        if (ResolveActivatableLifetime(this) is { } activatable)
            SubscribeFileActivation(activatable, OpenOrQueueActivatedPaths);
        else if (OperatingSystem.IsMacOS())
            logger.LogWarning("No activatable lifetime: files opened from Finder will not reach excise");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            logger.LogInformation("Creating main window");

            if (_metricsSink != null)
                desktop.Exit += (_, _) => _metricsSink.Dispose();

            // #1551: every document window, the first included, comes from the
            // workspace: one DI scope and one view model per document.
            var workspace = _serviceProvider.GetRequiredService<Workspace.DocumentWorkspace>();
            _workspace = workspace;
            var reclaimer = _serviceProvider.GetRequiredService<ReleasedMemoryReclaimer>();
            var cacheTrims = new Dictionary<MainWindow, ViewerCacheTrimCoordinator>();
            // One tile budget and one idle reclaim for the whole app, however
            // many document windows are open (#1551 follow-up).
            var tileBudget = new Excise.Avalonia.Controls.PdfViewerTileBudget(
                Models.PerformanceSettings.Balanced.TileCacheBudgetMb * 1024L * 1024L);
            var idleReclaimGate = new IdleReclaimGate();
            workspace.WindowCreated += window =>
            {
                AttachSharedTileBudget(window, tileBudget);
                var coordinator = AttachCacheTrim(window, workspace, reclaimer, idleReclaimGate, logger);
                if (coordinator != null)
                {
                    cacheTrims[window] = coordinator;
                    window.Closed += (_, _) => cacheTrims.Remove(window);
                }
            };
            desktop.Exit += (_, _) =>
            {
                foreach (var coordinator in cacheTrims.Values.ToArray())
                    coordinator.Dispose();
                cacheTrims.Clear();
            };

            // #1463: Cmd+Q and every other platform quit review ALL open
            // documents first. An approved quit (File > Exit, or this handler's
            // own retry) passes straight through.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (workspace.QuitApproved || !workspace.HasUnsavedChanges)
                    return;
                e.Cancel = true;
                _ = workspace.RequestQuitAsync();
            };

            var session = workspace.CreateSession();
            var vm = session.ViewModel;
            mainViewModel = vm;
            var mainWindow = workspace.ShowInNewWindow(session);
            desktop.MainWindow = mainWindow;

            // #1553: documents a second launch hands over (Windows, Linux).
            if (Workspace.SingleInstanceChannel.IsEnabled)
            {
                var channel = Workspace.SingleInstanceChannel.Server.TryStart(
                    Workspace.SingleInstanceChannel.DefaultPipeName(),
                    paths => Dispatcher.UIThread.Post(() => OpenPathsOnUiThread(vm, paths, logger)),
                    logger);
                if (channel != null)
                    desktop.Exit += (_, _) => channel.Dispose();
            }

            logger.LogInformation("Main window created successfully");

            // #1497: the performance-scenario runner drives a trim in process
            // instead of asking a human for `sudo memory_pressure`, so it needs
            // the first window's live coordinator.
            cacheTrims.TryGetValue(mainWindow, out var scenarioCacheTrim);
            var (_, trimPolicy) = mainWindow.CacheTrimTarget();

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

            // #1463: every PDF on the command line opens; the first in the
            // initial window, the rest by the open-mode preference.
            var startupPaths = StartupDocumentResolver.ResolveAll(
                desktop.Args,
                processArgs);
            var path = startupPaths.Count > 0 ? startupPaths[0] : null;

            if (path != null)
            {
                logger.LogInformation("Opening {Count} startup PDF(s), first: {Path}", startupPaths.Count, path);
                desktop.Startup += (_, _) =>
                {
                    DispatcherTimer.RunOnce(
                        () => OpenPathsOnUiThread(vm, startupPaths, logger),
                        TimeSpan.FromMilliseconds(250),
                        DispatcherPriority.Background);
                };

                // #846/#695: live visual-stability trace. Gated behind
                // EXCISE_VISUAL_TRACE_OUT — a no-op in normal use. Runs a
                // page-mutation in the REAL app (compositor drives the continuous
                // re-render the headless host cannot) and records the ink-centroid
                // trajectory so the shell harness can detect the post-mutation bounce.
                if (Automation.VisualTraceRunner.IsRequested)
                {
                    logger.LogInformation("Visual trace requested; running after document load");
                    desktop.Startup += (_, _) =>
                    {
                        DispatcherTimer.RunOnce(
                            () => _ = Automation.VisualTraceRunner.RunAsync(desktop.MainWindow!, vm),
                            TimeSpan.FromMilliseconds(1200),
                            DispatcherPriority.Background);
                    };
                }
            }

            // #1497: the in-app performance-scenario runner. Deliberately wired
            // OUTSIDE the `path != null` block above, unlike VisualTraceRunner:
            // a scenario opens (and closes, and replaces) its own documents, and
            // the calibration baseline is a launch with NO document at all — so
            // gating it on a startup argument would make the null scenario and
            // every open-from-scratch measurement impossible. A no-op unless
            // EXCISE_PERF_SCENARIO names a scenario file.
            if (Automation.PerfScenarioOptions.IsRequested)
            {
                logger.LogInformation("Performance scenario requested; running after startup");
                var trimForScenario = scenarioCacheTrim;
                desktop.Startup += (_, _) =>
                {
                    DispatcherTimer.RunOnce(
                        () => _ = Automation.PerfScenarioHost.RunAsync(
                            desktop.MainWindow!, vm, trimForScenario, trimPolicy, logger,
                            workspace,
                            scenarioWindow => cacheTrims.TryGetValue(scenarioWindow, out var scenarioTrim) ? scenarioTrim : null),
                        TimeSpan.FromMilliseconds(1200),
                        DispatcherPriority.Background);
                };
            }

            if (pendingActivationPaths.Count > 0)
            {
                var pathsToOpen = pendingActivationPaths.ToArray();
                pendingActivationPaths.Clear();
                logger.LogInformation("Opening {Count} queued activated PDF(s)", pathsToOpen.Length);
                desktop.Startup += (_, _) =>
                {
                    DispatcherTimer.RunOnce(
                        () => OpenPathsOnUiThread(vm, pathsToOpen, logger),
                        TimeSpan.FromMilliseconds(250),
                        DispatcherPriority.Background);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The session an OS-delivered open should start from: the active one, or
    /// <paramref name="fallback"/> before the workspace exists.
    /// </summary>
    private MainWindowViewModel ActiveViewModel(MainWindowViewModel fallback) =>
        _workspace?.ActiveSession?.ViewModel ?? fallback;

    /// <summary>
    /// #1478: release a document window's viewer and thumbnail caches under OS
    /// memory pressure, and (opt-in) in the background or idle. Wired here, in
    /// the real application only: headless tests build their windows under
    /// TestApp, so no test installs a live OS pressure source. One coordinator
    /// per window, because each window has its own viewer (#1551).
    /// </summary>
    private static ViewerCacheTrimCoordinator? AttachCacheTrim(
        MainWindow window,
        Workspace.DocumentWorkspace workspace,
        ReleasedMemoryReclaimer reclaimer,
        IdleReclaimGate idleReclaimGate,
        ILogger logger)
    {
        var (trimViewer, trimPolicy) = window.CacheTrimTarget();
        if (trimViewer == null)
            return null;

        // #1481: the reclaimer every session uses for close/replace, so a trim
        // and a close coalesce into one GC. The thumbnail tier trimmed is that
        // of every session this window hosts.
        var cacheTrim = ViewerCacheTrimCoordinator.Attach(
            window, trimViewer, trimPolicy, logger,
            level =>
            {
                // #1554: a tab nobody is looking at gives up its thumbnails
                // entirely; the shown one keeps what the level allows.
                var shown = workspace.SessionShownIn(window);
                foreach (var session in workspace.SessionsIn(window))
                {
                    session.ViewModel.TrimThumbnailCaches(
                        ReferenceEquals(session, shown) || level == Excise.Avalonia.Controls.PdfViewerCacheTrimLevel.Background
                            ? level
                            : Excise.Avalonia.Controls.PdfViewerCacheTrimLevel.Critical);
                }
            },
            reclaimer,
            idleReclaimGate);
        // Preferences → Performance changes soft trims live (#1478).
        window.CacheTrimPolicyChanged += cacheTrim.UpdatePolicy;
        window.Closed += (_, _) => cacheTrim.Dispose();
        return cacheTrim;
    }

    /// <summary>
    /// #1551 follow-up: every document window's viewer draws on ONE tile
    /// budget, so N windows hold one Preferences → Performance budget rather
    /// than N. The focused window is the budget's foreground: its tiles are
    /// the last to go, and background windows give way first.
    /// </summary>
    private static void AttachSharedTileBudget(MainWindow window, Excise.Avalonia.Controls.PdfViewerTileBudget budget)
    {
        window.UseSharedTileBudget(budget);
        var (viewer, _) = window.CacheTrimTarget();
        if (viewer == null)
            return;
        window.Activated += (_, _) => budget.Foreground = viewer;
        window.Deactivated += (_, _) =>
        {
            if (ReferenceEquals(budget.Foreground, viewer))
                budget.Foreground = null;
        };
        window.Closed += (_, _) => window.UseSharedTileBudget(null);
        if (window.IsActive)
            budget.Foreground = viewer;
    }

    /// <summary>
    /// Open PDFs on the UI thread, logging (not throwing) on failure. Shared by
    /// the command-line and OS file-activation open paths. The list goes to the
    /// workspace as ONE request, so the files are routed in order rather than
    /// racing each other for the same empty window.
    /// </summary>
    private void OpenPathsOnUiThread(MainWindowViewModel fallback, IReadOnlyList<string> paths, ILogger logger)
    {
        if (paths.Count == 0)
            return;

        void Open() => _ = OpenPathsAsync(ActiveViewModel(fallback), paths, logger);

        if (Dispatcher.UIThread.CheckAccess())
        {
            Open();
            return;
        }

        Dispatcher.UIThread.Post(Open);
    }

    private static async Task OpenPathsAsync(MainWindowViewModel vm, IReadOnlyList<string> paths, ILogger logger)
    {
        if (vm.SessionHost is not { } host)
        {
            await OpenPathAsync(vm, paths[0], logger);
            return;
        }

        try
        {
            logger.LogInformation("Opening {Count} PDF(s) from a startup/open event", paths.Count);
            await host.OpenDocumentsAsync(paths, replaceConfirmed: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open {Count} PDF(s) from a startup/open event", paths.Count);
        }
    }

    // internal (not private): #979 — this is the "resolved path actually gets
    // loaded" half of both the command-line/file-association launch path and
    // the macOS file-activation path. Exercised directly by
    // StartupActivationWorkflowTests so that half of "open via command-line
    // launch" has a real xunit method behind it rather than only a shell
    // script and a source-text doc-claim check.
    internal static async Task OpenPathAsync(MainWindowViewModel vm, string path, ILogger logger)
    {
        try
        {
            logger.LogInformation("Loading PDF from startup/open event: {Path}", path);

            // #1463: in a workspace the file may open elsewhere, an already
            // open file is brought forward, and the unsaved-changes question is
            // asked only when this session's document would be replaced.
            if (vm.SessionHost is { } host)
            {
                await host.OpenDocumentsAsync([path], replaceConfirmed: false);
                return;
            }

            // #1233: at STARTUP nothing is open and this is a no-op, but the
            // same method serves macOS Launch Services file activation, which
            // fires whenever the user double-clicks a PDF in Finder while
            // excise is already running with a dirty document. Without this
            // guard that is a silent-data-loss path -- and on macOS it is a
            // very ordinary way to open a file.
            if (!await vm.ConfirmDiscardUnsavedChangesAsync("open a different document"))
            {
                logger.LogInformation("Open of {Path} cancelled at the unsaved-changes prompt", path);
                return;
            }

            await vm.LoadDocumentAsync(path);
            logger.LogInformation("Loaded PDF from startup/open event: {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open {Path}", path);
        }
    }

    /// <summary>
    /// #1585: the platform lifetime that raises macOS open-documents events.
    /// Avalonia 12's desktop lifetime does not implement
    /// <see cref="IActivatableLifetime"/>; it is an application feature
    /// (Avalonia.Native raises <see cref="FileActivatedEventArgs"/> on it).
    /// Checking <c>ApplicationLifetime</c> alone found nothing, so every file
    /// Finder handed over was dropped without a trace.
    /// </summary>
    internal static IActivatableLifetime? ResolveActivatableLifetime(Application application) =>
        application.TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime
        ?? application.ApplicationLifetime as IActivatableLifetime;

    /// <summary>
    /// Route every PDF in a file activation to <paramref name="open"/>.
    /// #1463: every PDF Finder hands over, each in its own session.
    /// </summary>
    internal static void SubscribeFileActivation(
        IActivatableLifetime lifetime, Action<IReadOnlyList<string>> open)
    {
        lifetime.Activated += (_, e) =>
        {
            if (e is not FileActivatedEventArgs fileArgs)
                return;

            var paths = ResolveActivatedPdfPaths(fileArgs.Files);
            if (paths.Count > 0)
                open(paths);
        };
    }

    // internal (not private): see OpenPathAsync's note — this is the
    // file-selection half of the macOS file-association activation path
    // (IActivatableLifetime.Activated -> FileActivatedEventArgs.Files).
    internal static string? ResolveActivatedPdfPath(IReadOnlyList<IStorageItem> files)
    {
        // #1002: the rule now lives in DroppedPdfResolver so drag-and-drop and
        // file activation cannot drift apart. Behaviour is unchanged.
        return Excise.App.Services.DroppedPdfResolver.ResolveFirstPdf(files);
    }

    // #1463: every PDF in the activation, same per-item rule.
    internal static IReadOnlyList<string> ResolveActivatedPdfPaths(IReadOnlyList<IStorageItem> files) =>
        Excise.App.Services.DroppedPdfResolver.ResolveAllPdfs(files);

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

        services.AddExciseApplicationServices();
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
