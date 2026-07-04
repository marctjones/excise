using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEditor.Services;

namespace PdfEditor;

internal static class StartupDocumentResolver
{
    public static string? Resolve(IEnumerable<string>? lifetimeArgs, IEnumerable<string>? processArgs)
    {
        return EnumeratePdfCandidates(lifetimeArgs)
            .Concat(EnumeratePdfCandidates(processArgs))
            .FirstOrDefault();
    }

    public static string? ResolveResponsivenessReportPath(
        IEnumerable<string>? lifetimeArgs,
        IEnumerable<string>? processArgs)
    {
        return EnumerateResponsivenessReportCandidates(lifetimeArgs)
            .Concat(EnumerateResponsivenessReportCandidates(processArgs))
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

    private static IEnumerable<string> EnumerateResponsivenessReportCandidates(IEnumerable<string>? args)
    {
        if (args == null)
            yield break;

        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            string? value = null;
            if (arg.Equals(ResponsivenessReportWriter.ReportPathArgument, StringComparison.Ordinal))
            {
                if (enumerator.MoveNext())
                    value = enumerator.Current;
            }
            else if (arg.StartsWith($"{ResponsivenessReportWriter.ReportPathArgument}=", StringComparison.Ordinal))
            {
                value = arg[$"{ResponsivenessReportWriter.ReportPathArgument}=".Length..];
            }

            if (string.IsNullOrWhiteSpace(value))
                continue;

            var path = NormalizePathArgument(value);
            if (path != null)
                yield return path;
        }
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
