namespace Excise.App.Services;

/// <summary>
/// The user's answer to "you have unsaved changes" (#1233).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately three-valued rather than a <c>bool</c>. The two-valued
/// <see cref="IUserDialogService.ShowConfirmAsync"/> cannot express this
/// question: "don't save" and "don't close" are both a <c>false</c>, and
/// collapsing them either discards work the user wanted kept or refuses a
/// close the user asked for.
/// </para>
/// <para>
/// <see cref="Cancel"/> is the fail-closed value — see
/// <see cref="IUserDialogService.ShowUnsavedChangesAsync"/>. A context that
/// cannot ask (headless, no main window) must keep the document open and
/// dirty, never silently discard.
/// </para>
/// </remarks>
public enum UnsavedChangesDecision
{
    /// <summary>
    /// Keep the document open with its edits intact. The close/quit/open that
    /// prompted the question must be abandoned.
    /// </summary>
    Cancel = 0,

    /// <summary>
    /// Persist the edits before proceeding. For an original source this means
    /// a COPY — the routing in
    /// <c>MainWindowViewModel.SaveFileAsync</c> is reused verbatim so the
    /// source file is never overwritten.
    /// </summary>
    Save = 1,

    /// <summary>
    /// Proceed and throw the edits away. Only ever reached by an explicit
    /// user choice.
    /// </summary>
    Discard = 2,
}
