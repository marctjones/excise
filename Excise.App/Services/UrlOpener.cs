using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Excise.App.Services;

/// <summary>
/// Cross-platform "open in default handler" for a URL or file path.
/// Extracted from <c>AboutWindow.OpenUrl</c> so the external-link-click
/// feature (#625) doesn't duplicate the same platform fallback chain.
/// </summary>
internal static class UrlOpener
{
    public static void Open(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            // ProcessStartInfo with UseShellExecute=true is the
            // cross-platform "open in default handler" idiom.
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch
        {
            // Fall back to platform-specific commands if shell execute
            // refuses (some hardened distros don't have a default handler).
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch { /* best-effort; nothing to do if the OS won't open URLs */ }
        }
    }
}
