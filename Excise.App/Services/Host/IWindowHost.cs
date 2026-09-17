using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Excise.App.Services.Host;

/// <summary>
/// The one owner of "which window are we, and how do we reach the platform
/// through it" (#1500 step 1). Before this, the view model read
/// <c>Application.Current.ApplicationLifetime</c> itself from four places and
/// every picker call started with a <c>GetStorageProvider()</c> of its own.
/// </summary>
/// <remarks>
/// <para>
/// The two settable members are not test-only afterthoughts — they are this
/// adapter's configuration, and they are why the seam can move without
/// touching a single existing test. Avalonia's <see cref="IStorageProvider"/>
/// is sealed against user implementations and
/// <c>Application.ApplicationLifetime</c> may be set exactly once (a headless
/// test cannot stand up a desktop lifetime after <c>SetupWithoutStarting</c>),
/// so a test steers the real production path by overriding the lookup rather
/// than by faking the platform (#816).
/// <c>MainWindowViewModel.StorageProviderOverride</c> and
/// <c>MainWindowViewModel.MainWindowResolver</c> now forward here, so every
/// test that sets them keeps steering the real adapter.
/// </para>
/// <para>
/// Deliberately NOT here yet, and each for a measured reason rather than
/// tidiness:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The OS clipboard owner. <see cref="AvaloniaTextClipboard"/> keeps its own
/// lifetime read so that a test which sets <c>MainWindowResolver</c> does not
/// newly acquire a real clipboard — <c>GuiExpectedEffectTests</c> and
/// <c>KeyboardShortcutEffectTests</c> both set the resolver AND copy text, so
/// routing the clipboard through <see cref="MainWindow"/> would change their
/// behaviour.
/// </description></item>
/// <item><description>
/// <c>ShowDialogAsync</c>. The Bates/Security/Searchable/
/// Preferences/About dialogs still call <c>GetMainWindow()</c> and
/// <c>ShowDialog(owner)</c> themselves; folding those into this interface is
/// the design's step 12, and adding an unused member now would be
/// speculative.
/// </description></item>
/// </list>
/// </remarks>
internal interface IWindowHost
{
    /// <summary>
    /// Overrides the storage provider <see cref="StorageProvider"/> hands out.
    /// Null in production, so the real main window's provider is always used.
    /// </summary>
    IStorageProvider? StorageProviderOverride { get; set; }

    /// <summary>
    /// How <see cref="MainWindow"/> is resolved. Defaults to the classic
    /// desktop lifetime's main window.
    /// </summary>
    Func<Window?> MainWindowResolver { get; set; }

    /// <summary>The owner window for modal dialogs, or null if there is none.</summary>
    Window? MainWindow { get; }

    /// <summary>
    /// The storage provider for file and folder pickers, or null when there is
    /// no window to host one.
    /// </summary>
    IStorageProvider? StorageProvider { get; }

    /// <summary>
    /// Asks the application to shut down. A no-op when there is no desktop
    /// lifetime, which is what makes <c>ExitCommand</c> safe to execute in a
    /// headless test.
    /// </summary>
    void RequestShutdown();
}
