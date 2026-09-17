using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Excise.App.Composition;

/// <summary>
/// Owns the production service graph for the Excise desktop application.
/// Keeping construction here prevents a missing registration from silently
/// selecting a different <see cref="MainWindowViewModel"/> constructor.
/// </summary>
internal static class ApplicationComposition
{
    internal static IServiceCollection AddExciseApplicationServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PdfDocumentService>();
        services.AddSingleton<IPageImageRenderer, PageImageRenderer>();
        services.AddSingleton<RedactionService>();
        services.AddSingleton<RedactedCopyDialogFormatter>();
        services.AddSingleton<RedactionWorkflowService>();
        services.AddSingleton<PdfTextExtractionService>();
        services.AddSingleton<PdfSearchService>();
        services.AddSingleton<DocumentSearchSession>();
        services.AddSingleton<DocumentTextIndexSession>();
        services.AddSingleton<SignatureVerificationService>();
        services.AddSingleton<SignatureVerificationSummaryFormatter>();
        services.AddSingleton<SignatureVerificationWorkflowService>();
        services.AddSingleton<PageOrganizationWorkflowService>();
        services.AddSingleton<DocumentImageExportWorkflowService>();
        services.AddSingleton<AnnotationWorkflowService>();
        services.AddSingleton<FilenameSuggestionService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IUserDialogService, AvaloniaUserDialogService>();
        // #1481: one instance, shared by the view model (close/replace) and the
        // cache-trim coordinator (OS pressure), so their requests coalesce.
        services.AddSingleton(_ => new ReleasedMemoryReclaimer());

        // #1545: printing. The platform half is chosen here (PDFKit on macOS,
        // PrintDlgEx + PrintDocument on Windows (#1546), an honest refusal
        // elsewhere); the workflow that writes
        // and deletes the print copy is platform-neutral. Explicit factories
        // for the same reason as the host adapters below: internal types.
        services.AddSingleton<Excise.App.Services.Printing.IDocumentPrinter>(provider =>
            Excise.App.Services.Printing.DocumentPrinterFactory.CreateForCurrentPlatform(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton(provider => new Excise.App.Services.Printing.DocumentPrintWorkflowService(
            provider.GetRequiredService<RedactionWorkflowService>(),
            provider.GetRequiredService<Excise.App.Services.Printing.IDocumentPrinter>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Excise.App.Services.Printing.DocumentPrintWorkflowService>>()));

        // Host and persistence adapters (#1500 steps 1-2). Explicit factories,
        // like ReleasedMemoryReclaimer above: these types and their constructors
        // are internal (design principle "new units are internal in Phase A"),
        // and Microsoft.Extensions.DependencyInjection only discovers PUBLIC
        // constructors.
        //
        // AvaloniaWindowHost is one instance on purpose: the file picker
        // resolves its storage provider from the same host the view model's
        // StorageProviderOverride forwards to, so setting that override steers
        // the real picker rather than a second, unused host.
        services.AddSingleton<IWindowHost>(_ => new AvaloniaWindowHost());
        services.AddSingleton<IFilePicker>(provider => new AvaloniaFilePicker(
            provider.GetRequiredService<IWindowHost>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AvaloniaFilePicker>>()));
        services.AddSingleton<ITextClipboard>(_ => new AvaloniaTextClipboard());
        services.AddSingleton<ISettingsStore>(_ => new FileSettingsStore());
        services.AddSingleton<IRecentFilesStore>(_ => new FileRecentFilesStore());

        // The desktop lifetime has one main window and therefore one document
        // session. Use an explicit factory so constructor selection cannot fall
        // back to MainWindowViewModel's temporary test/design-time graph when a
        // production registration is missing.
        services.AddSingleton(CreateMainWindowViewModel);

        return services;
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        IServiceProvider services) =>
        new(
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MainWindowViewModel>>(),
            services.GetRequiredService<PdfDocumentService>(),
            services.GetRequiredService<RedactionService>(),
            services.GetRequiredService<RedactedCopyDialogFormatter>(),
            services.GetRequiredService<RedactionWorkflowService>(),
            services.GetRequiredService<PdfTextExtractionService>(),
            services.GetRequiredService<DocumentSearchSession>(),
            services.GetRequiredService<DocumentTextIndexSession>(),
            services.GetRequiredService<FilenameSuggestionService>(),
            services.GetRequiredService<ToastService>(),
            services.GetRequiredService<IUserDialogService>(),
            services.GetRequiredService<SignatureVerificationWorkflowService>(),
            services.GetRequiredService<PageOrganizationWorkflowService>(),
            services.GetRequiredService<DocumentImageExportWorkflowService>(),
            services.GetRequiredService<AnnotationWorkflowService>(),
            services.GetRequiredService<ReleasedMemoryReclaimer>(),
            services.GetRequiredService<Excise.App.Services.Printing.DocumentPrintWorkflowService>(),
            services.GetRequiredService<IFilePicker>(),
            services.GetRequiredService<IWindowHost>(),
            services.GetRequiredService<ITextClipboard>(),
            services.GetRequiredService<ISettingsStore>(),
            services.GetRequiredService<IRecentFilesStore>());
}
