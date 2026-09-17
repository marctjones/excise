namespace Excise.App.Models;

/// <summary>
/// Where a document opens when the requesting window already shows one
/// (#1463, Preferences ▸ Documents). A window with no document always takes
/// the file itself, and a file that is already open is brought forward
/// instead of being opened twice.
/// </summary>
public enum DocumentOpenMode
{
    /// <summary>
    /// The platform default: a new window. On macOS the system setting
    /// "Prefer tabs when opening documents" then decides whether that window
    /// joins the current one as a native tab.
    /// </summary>
    Automatic,

    /// <summary>Always a new top-level window.</summary>
    NewWindow,

    /// <summary>A new tab in the requesting window (#1554).</summary>
    NewTab,

    /// <summary>
    /// Replace the requesting window's document, after the unsaved-changes
    /// prompt: the single-document behaviour excise had before #1463.
    /// </summary>
    ReplaceCurrent,
}
