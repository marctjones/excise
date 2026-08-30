using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// Deterministic headless dialog behavior for tests that do not exercise
/// user interaction. This type deliberately lives outside the shipping app.
/// </summary>
internal sealed class NullUserDialogService : IUserDialogService
{
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
        Task.FromResult<string?>(defaultValue);

    public Task<string?> PromptPasswordAsync(string title, string message) =>
        Task.FromResult<string?>(null);

    public Task<bool> ShowConfirmAsync(string title, string message) =>
        Task.FromResult(false);
}
