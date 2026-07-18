using System.Threading.Tasks;

namespace Excise.App.Services;

public interface IUserDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
        Task.FromResult<string?>(defaultValue);

    Task<string?> PromptPasswordAsync(string title, string message) =>
        PromptTextAsync(title, message);

    /// <summary>
    /// Ask the user to confirm a consequential action. Fail-closed: the
    /// default implementation returns <c>false</c> (do not proceed), so a
    /// caller that forgets to check for a main window, or a headless
    /// context with no UI, never silently treats "couldn't ask" as "yes."
    /// </summary>
    Task<bool> ShowConfirmAsync(string title, string message) =>
        Task.FromResult(false);
}
