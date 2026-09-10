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

    /// <summary>
    /// Ask what to do about unsaved document changes before a close, quit, or
    /// document replacement (#1233).
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">What is about to be lost.</param>
    /// <param name="saveActionText">
    /// Label for the affirmative button. The caller passes
    /// <c>DocumentStateManager.GetSaveButtonText()</c>, so an unmodified source
    /// reads "Save a Copy" / "Save Redacted Version" rather than "Save" — the
    /// button must not imply the original will be overwritten, because it never
    /// is.
    /// </param>
    /// <remarks>
    /// Fail-closed like <see cref="ShowConfirmAsync"/>, but the safe answer is
    /// the opposite one: the default returns
    /// <see cref="UnsavedChangesDecision.Cancel"/>, so a context that cannot
    /// ask abandons the CLOSE and keeps the edits, rather than proceeding and
    /// discarding them. "Couldn't ask" must never destroy work.
    /// </remarks>
    Task<UnsavedChangesDecision> ShowUnsavedChangesAsync(
        string title, string message, string saveActionText) =>
        Task.FromResult(UnsavedChangesDecision.Cancel);
}
