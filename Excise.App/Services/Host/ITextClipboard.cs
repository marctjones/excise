using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Excise.App.Services.Host;

/// <summary>
/// The OS clipboard as a dependency (#1500 step 1), so the view model stops
/// reaching through <c>Application.Current.ApplicationLifetime</c> for a
/// <c>TopLevel</c> just to copy a string.
/// </summary>
/// <remarks>
/// Named <c>ITextClipboard</c>, not <c>IClipboard</c> as the design doc calls
/// it: <see cref="Avalonia.Input.Platform.IClipboard"/> already exists, and a
/// same-named interface inside an Avalonia app is a trap for the next reader —
/// whichever of the two a file's <c>using</c>s happened to import would win
/// silently. The narrower name is also the honest one, since this puts text on
/// the clipboard and nothing else.
/// </remarks>
internal interface ITextClipboard
{
    /// <summary>
    /// Puts <paramref name="text"/> on the OS clipboard. Returns false when no
    /// clipboard is available — the caller decides what that means, which is
    /// how the view model keeps its "recorded in history only" branch.
    /// </summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>
/// The production <see cref="ITextClipboard"/>.
/// </summary>
/// <remarks>
/// This resolves the desktop lifetime's main window directly rather than
/// through <see cref="IWindowHost"/>, and that is deliberate: the pre-#1500
/// code did the same, so honouring <c>MainWindowResolver</c> here would give a
/// real clipboard to tests that set the resolver and copy text
/// (<c>GuiExpectedEffectTests</c>, <c>KeyboardShortcutEffectTests</c> both do).
/// A structural step must not change what those observe. Consolidating the
/// read into <see cref="IWindowHost"/> belongs to whichever step deliberately
/// revisits clipboard ownership (the design's step 9).
/// </remarks>
internal sealed class AvaloniaTextClipboard : ITextClipboard
{
    /// <inheritdoc />
    public async Task<bool> SetTextAsync(string text)
    {
        var topLevel = Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (topLevel?.Clipboard == null)
            return false;

        await topLevel.Clipboard.SetTextAsync(text);
        return true;
    }
}
