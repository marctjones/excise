using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Excise.App.Workspace;

/// <summary>
/// "Reveal in Finder" / "Show in Explorer" for a document tab (#1554).
/// Arguments are passed as an argument list, never through a shell, so a file
/// name cannot become a command.
/// </summary>
internal static class FileManagerReveal
{
    /// <summary>The program and arguments that reveal <paramref name="path"/> on this platform.</summary>
    internal static (string FileName, IReadOnlyList<string> Arguments)? CommandFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (OperatingSystem.IsMacOS())
            return ("open", new[] { "-R", path });
        if (OperatingSystem.IsWindows())
            return ("explorer.exe", new[] { $"/select,{path}" });

        // Linux file managers do not agree on a "select this file" verb;
        // opening the folder is the portable subset.
        var folder = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(folder) ? null : ("xdg-open", new[] { folder });
    }

    internal static void Reveal(string path, ILogger? logger)
    {
        if (!File.Exists(path) || CommandFor(path) is not { } command)
            return;

        try
        {
            var info = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
            foreach (var argument in command.Arguments)
                info.ArgumentList.Add(argument);
            using var _ = Process.Start(info);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not reveal {Path} in the file manager", path);
        }
    }
}
