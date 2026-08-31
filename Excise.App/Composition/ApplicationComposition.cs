using Excise.App.Services;
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
            services.GetRequiredService<AnnotationWorkflowService>());
}
