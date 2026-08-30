using AwesomeAssertions;
using Excise.App.Composition;
using Excise.App.Services;
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

        var viewModel = provider.GetRequiredService<MainWindowViewModel>();
        var registeredToast = provider.GetRequiredService<ToastService>();

        viewModel.ToastService.Should().BeSameAs(registeredToast,
            "the production VM must use the registered session graph, not its private test graph");

        await viewModel.PrintCommand.Execute();

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].Title.Should().Be("Print");
        dialog.Messages[0].Message.Should().Contain("Open a PDF before printing");
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
