using System;
using System.Collections.Generic;
using System.Linq;
using Excise.App.Services.Printing;

namespace Excise.App.Tests.Utilities.Fakes;

/// <summary>
/// A scripted <see cref="ICupsProcessRunner"/> (#1710): no subprocess, so the
/// whole Linux print path runs on macOS and Windows too. It records every
/// invocation, which is how the tests assert that a denied document never
/// reaches <c>lp</c> at all.
/// </summary>
internal sealed class FakeCupsProcessRunner : ICupsProcessRunner
{
    /// <summary>One recorded invocation.</summary>
    internal sealed record Invocation(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
    {
        /// <summary>The command as one string, for readable assertions.</summary>
        internal string CommandLine => string.Join(' ', new[] { FileName }.Concat(Arguments));
    }

    private readonly Dictionary<string, CupsProcessResult> _responses = new(StringComparer.Ordinal);

    /// <summary>What an unscripted command returns. Default: a clean empty success.</summary>
    internal CupsProcessResult Fallback { get; set; } = CupsProcessResult.Ran(0, string.Empty, string.Empty);

    internal List<Invocation> Invocations { get; } = new();

    /// <summary>Every command line that was run, in order.</summary>
    internal IReadOnlyList<string> CommandLines => Invocations.Select(i => i.CommandLine).ToArray();

    /// <summary>Script the answer for a command whose line CONTAINS <paramref name="match"/>.</summary>
    internal FakeCupsProcessRunner When(string match, CupsProcessResult result)
    {
        _responses[match] = result;
        return this;
    }

    /// <summary>The happy path: one queue named <paramref name="queue"/>, and it is the default.</summary>
    internal static FakeCupsProcessRunner WithQueues(params string[] queues)
    {
        var runner = new FakeCupsProcessRunner();
        runner.When("lpstat -e", CupsProcessResult.Ran(0, string.Join('\n', queues) + "\n", string.Empty));
        runner.When("lpstat -p", CupsProcessResult.Ran(
            0,
            string.Join('\n', queues.Select(q => $"printer {q} is idle.  enabled since Sun 21 Sep 2026 10:00:00 AM UTC")) + "\n",
            string.Empty));
        runner.When("lpstat -d", CupsProcessResult.Ran(
            0,
            queues.Length > 0 ? $"system default destination: {queues[0]}\n" : "no system default destination\n",
            string.Empty));
        runner.When("lp -d", CupsProcessResult.Ran(0, "request id is " + (queues.FirstOrDefault() ?? "none") + "-1 (1 file(s))\n", string.Empty));
        return runner;
    }

    public CupsProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var invocation = new Invocation(fileName, arguments.ToArray(), timeout);
        Invocations.Add(invocation);
        foreach (var (match, result) in _responses)
        {
            if (invocation.CommandLine.Contains(match, StringComparison.Ordinal))
                return result;
        }
        return Fallback;
    }
}

/// <summary>
/// A scripted <see cref="ILinuxPrintDialog"/> (#1710): the chooser is the one
/// piece that needs a window, so every test supplies its answer directly, the
/// same split <c>WindowsDocumentPrinterTests</c> uses for <c>PrintDlgExW</c>.
/// </summary>
internal sealed class ScriptedLinuxPrintDialog(LinuxPrintTicket ticket) : ILinuxPrintDialog
{
    /// <summary>The queues the chooser was offered, one entry per call.</summary>
    internal List<IReadOnlyList<CupsPrintQueue>> OfferedQueues { get; } = new();

    /// <summary>The page counts the chooser was told about, one entry per call.</summary>
    internal List<int> PageCounts { get; } = new();

    /// <summary>Builds the ticket from what it was offered, when set.</summary>
    internal Func<IReadOnlyList<CupsPrintQueue>, int, LinuxPrintTicket>? Respond { get; set; }

    // global:: because this assembly's own Excise.Avalonia.Controls namespace
    // shadows the framework's Avalonia.Controls here.
    public System.Threading.Tasks.Task<LinuxPrintTicket> ShowAsync(
        global::Avalonia.Controls.Window? owner,
        IReadOnlyList<CupsPrintQueue> queues,
        int pageCount)
    {
        OfferedQueues.Add(queues);
        PageCounts.Add(pageCount);
        return System.Threading.Tasks.Task.FromResult(Respond?.Invoke(queues, pageCount) ?? ticket);
    }
}
