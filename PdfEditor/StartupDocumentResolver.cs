using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PdfEditor;

internal static class StartupDocumentResolver
{
    public static string? Resolve(IEnumerable<string>? lifetimeArgs, IEnumerable<string>? processArgs)
    {
        return EnumeratePdfCandidates(lifetimeArgs)
            .Concat(EnumeratePdfCandidates(processArgs))
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumeratePdfCandidates(IEnumerable<string>? args)
    {
        if (args == null)
            yield break;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || IsPlatformArgument(arg))
                continue;

            var path = NormalizePathArgument(arg);
            if (path == null)
                continue;

            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(path))
                yield return path;
        }
    }

    private static bool IsPlatformArgument(string arg)
    {
        return arg.StartsWith("-", StringComparison.Ordinal);
    }

    private static string? NormalizePathArgument(string arg)
    {
        try
        {
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                    return null;

                return Path.GetFullPath(uri.LocalPath);
            }

            return Path.GetFullPath(arg);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
