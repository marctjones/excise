using System;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Env-gated execution-path tracing for live GUI sessions
/// (<c>EXCISE_TRACE_VIEWER=1</c>). The viewer had no logging at all, which made
/// user-reported display issues (the mode-switch report behind #693) impossible
/// to trace against the real interaction sequence. Zero overhead when off.
/// </summary>
public partial class PdfViewerControl
{
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("EXCISE_TRACE_VIEWER") == "1";

    private static void Trace(string message)
    {
        if (TraceEnabled)
            Console.WriteLine($"[viewer {DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}
