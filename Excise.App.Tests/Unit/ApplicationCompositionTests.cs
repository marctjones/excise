using AwesomeAssertions;
using Excise.App.Composition;
using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace Excise.App.Tests.Unit;

public class ApplicationCompositionTests
{
    [FixedAvaloniaFact]
    public async Task ProductionComposition_UsesRegisteredSessionServicesAndDialog()
    {
        var dialog = new RecordingUserDialogService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExciseApplicationServices();

        // A later registration is the normal Microsoft DI override mechanism.
        // It lets this canary prove the resolved VM receives the composition
        // root's dialog rather than the parameterless constructor's null dialog.
        services.AddSingleton<IUserDialogService>(dialog);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // #1551: the view model is per document session, so it is resolved from
        // a scope, and the toast service it uses is that scope's.
        using var scope = provider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var registeredToast = scope.ServiceProvider.GetRequiredService<ToastService>();

        viewModel.ToastService.Should().BeSameAs(registeredToast,
            "the production VM must use the registered session graph, not its private test graph");

        await viewModel.PrintCommand.Execute();

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].Title.Should().Be("Print");
        dialog.Messages[0].Message.Should().Contain("Open a PDF before printing");
    }

    /// <summary>
    /// #1551: two document sessions must not share any document-shaped
    /// service. Planted-defect check: registering <see cref="PdfDocumentService"/>
    /// (or the toast service, or the window host) as a singleton again turns
    /// this red.
    /// </summary>
    [FixedAvaloniaFact]
    public void ProductionComposition_GivesEachDocumentSessionItsOwnDocumentServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExciseApplicationServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        T Resolve<T>(IServiceScope scope) where T : notnull =>
            scope.ServiceProvider.GetRequiredService<T>();

        Resolve<MainWindowViewModel>(first).Should().NotBeSameAs(Resolve<MainWindowViewModel>(second));
        Resolve<PdfDocumentService>(first).Should().NotBeSameAs(Resolve<PdfDocumentService>(second),
            "each session holds its own open document");
        Resolve<DocumentSearchSession>(first).Should().NotBeSameAs(Resolve<DocumentSearchSession>(second));
        Resolve<DocumentTextIndexSession>(first).Should().NotBeSameAs(Resolve<DocumentTextIndexSession>(second));
        Resolve<PageOrganizationWorkflowService>(first)
            .Should().NotBeSameAs(Resolve<PageOrganizationWorkflowService>(second));
        Resolve<AnnotationWorkflowService>(first).Should().NotBeSameAs(Resolve<AnnotationWorkflowService>(second));
        Resolve<ToastService>(first).Should().NotBeSameAs(Resolve<ToastService>(second),
            "a toast belongs to the window that shows its document");
        Resolve<IWindowHost>(first).Should().NotBeSameAs(Resolve<IWindowHost>(second),
            "dialogs are owned by the session's own window");
        Resolve<IUserDialogService>(first).Should().NotBeSameAs(Resolve<IUserDialogService>(second));

        Resolve<ReleasedMemoryReclaimer>(first).Should().BeSameAs(Resolve<ReleasedMemoryReclaimer>(second),
            "close and trim requests from every session coalesce in one reclaimer");
        Resolve<ISettingsStore>(first).Should().BeSameAs(Resolve<ISettingsStore>(second),
            "preferences are app-wide");

        var rootResolution = () => provider.GetRequiredService<PdfDocumentService>();
        rootResolution.Should().Throw<System.InvalidOperationException>(
            "a document service resolved outside a session would be shared by every window");
    }

    private sealed class RecordingUserDialogService : IUserDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }

        public Task<string?> PromptTextAsync(
            string title,
            string message,
            string? defaultValue = null) =>
            Task.FromResult(defaultValue);

        public Task<string?> PromptPasswordAsync(string title, string message) =>
            Task.FromResult<string?>(null);

        public Task<bool> ShowConfirmAsync(string title, string message) =>
            Task.FromResult(false);
    }
}
