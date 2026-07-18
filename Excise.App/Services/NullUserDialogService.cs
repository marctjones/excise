using System.Threading.Tasks;

namespace Excise.App.Services;

public sealed class NullUserDialogService : IUserDialogService
{
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
        Task.FromResult<string?>(defaultValue);

    public Task<string?> PromptPasswordAsync(string title, string message) =>
        Task.FromResult<string?>(null);

    public Task<bool> ShowConfirmAsync(string title, string message) =>
        Task.FromResult(false);
}
