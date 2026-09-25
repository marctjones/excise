using Excise.Core.Signatures;
using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // #1551: one dependency-injection SCOPE per document session
        // (DocumentSessionFactory). Everything that carries the state of one
        // document, or belongs to the window that shows it, is scoped, so two
        // open documents never share it. ValidateScopes (App.axaml.cs) rejects
        // a scoped service resolved from the root, which is what keeps a later
        // registration from quietly making one of these app-wide again.
        services.AddScoped<PdfDocumentService>();
        services.AddScoped<DocumentSearchSession>();
        services.AddScoped<DocumentTextIndexSession>();
        services.AddScoped<PageOrganizationWorkflowService>();
        services.AddScoped<AnnotationWorkflowService>();
        services.AddScoped<SignatureVerificationWorkflowService>();
        services.AddScoped<ToastService>();

        // Stateless, or app-wide by design (§7.2 of
        // docs/architecture/main-window-architecture.md).
        services.AddSingleton<IPageImageRenderer, PageImageRenderer>();
        services.AddSingleton<RedactionService>();
        services.AddSingleton<RedactedCopyDialogFormatter>();
        services.AddSingleton<RedactionWorkflowService>();
        services.AddSingleton<PdfTextExtractionService>();
        services.AddSingleton<PdfSearchService>();
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SignatureVerificationService>>();
            return new SignatureVerificationService(msg => logger.LogInformation("{Message}", msg));
        });
        services.AddSingleton<SignatureVerificationSummaryFormatter>();
        services.AddSingleton<DocumentImageExportWorkflowService>();
        services.AddSingleton<FilenameSuggestionService>();
        // #1481: one instance, shared by every session (close/replace) and the
        // cache-trim coordinators (OS pressure), so their requests coalesce.
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
        // The window host is one instance PER SESSION (#1551): the session
        // points it at the window that shows the document, and the file
        // picker and the dialog service resolve their owner from that same
        // instance, so a dialog opened from the second window is owned by the
        // second window.
        services.AddScoped<IWindowHost>(_ => new AvaloniaWindowHost());
        services.AddScoped<IFilePicker>(provider => new AvaloniaFilePicker(
            provider.GetRequiredService<IWindowHost>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AvaloniaFilePicker>>()));
        services.AddScoped<IUserDialogService>(provider => new AvaloniaUserDialogService(
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AvaloniaUserDialogService>>(),
            provider.GetRequiredService<IWindowHost>()));
        services.AddSingleton<ITextClipboard>(_ => new AvaloniaTextClipboard());
        services.AddSingleton<ISettingsStore>(_ => new FileSettingsStore());
        services.AddSingleton<IRecentFilesStore>(_ => new FileRecentFilesStore());

        // One view model per document session. Use an explicit factory so
        // constructor selection cannot fall back to MainWindowViewModel's
        // temporary test/design-time graph when a production registration is
        // missing.
        services.AddScoped(CreateMainWindowViewModel);

        // The application's open documents (#1463).
        services.AddSingleton(provider => new Workspace.DocumentSessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton(provider => new Workspace.DocumentWorkspace(
            provider.GetRequiredService<Workspace.DocumentSessionFactory>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Workspace.DocumentWorkspace>>()));

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
