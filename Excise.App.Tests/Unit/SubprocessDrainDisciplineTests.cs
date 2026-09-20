using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;
using Excise.TestSupport;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The regression #1068 asked for and never got (#1516).
///
/// <para>#1068 closed a hang where <c>ThirdPartyLicenseCompletenessTests</c>
/// shelled out to <c>dotnet list package</c>: MSBuild left node-reuse workers
/// alive holding the inherited stdout handle, so the pipe never reached EOF.
/// <c>WaitForExit(60_000)</c> returned <b>true</b> — the child really had
/// exited — and the <c>ReadToEnd</c> on the next line blocked forever, taking
/// all 1,300 other tests with it. Its closing note left an audit item:</para>
///
/// <para><i>"WaitForExit(ms) bounds the PROCESS, not the STREAMS … Grep for
/// WaitForExit and confirm each caller also bounds the reads."</i></para>
///
/// <para>That audit was never run, and grepping for <c>WaitForExit</c> would
/// not have found the two live instances anyway — the dangerous half of the
/// pattern is the READ, not the wait. Both sat in <c>Excise.App.Tests</c> until
/// #1516: <c>BatesNumberingWorkflowTests.PdftotextPage</c> read stdout
/// synchronously BEFORE its bounded wait (making the timeout unreachable), and
/// <c>AttachmentsPanelTests.QpdfListAttachments</c> read stdout to EOF and only
/// then stderr (a two-pipe deadlock if the child fills the stderr buffer).</para>
///
/// <para>Why this is worse here than in ordinary code: these helpers are called
/// from <c>[FixedAvaloniaFact]</c> bodies, which run on the single Avalonia
/// headless dispatcher thread. xUnit's <c>Timeout</c> cannot abort a thread
/// blocked in a synchronous native read — it reports the timeout and leaks the
/// thread — so one blocked read wedges every remaining Avalonia test in the
/// process. That is a process-wide stall at 0% CPU, not one failing test.</para>
///
/// <para>Scope is deliberately this project only. Of 47 such lines repo-wide in
/// 26 files, 3 were in this project; the remaining <b>44 lines in 24 files</b>
/// are in <c>Excise.Core.Tests</c>, <c>Excise.Rendering.Tests</c>,
/// <c>Excise.Rendering/Differential</c> and <c>tools/Excise.Reachability</c>;
/// they are enumerated in #1516 and will be folded into this gate's scope as
/// they are fixed. This repo does not keep external violation allowlists —
/// #1172 removed the last one — so the gate covers what is actually green
/// rather than carrying a list of exceptions.</para>
/// </summary>
public class SubprocessDrainDisciplineTests
{
    /// <summary>
    /// Built by concatenation on purpose: if these needles appeared as whole
    /// literals, this file would match its own scan.
    /// </summary>
    private static readonly string[] BannedReads =
    {
        "StandardOutput.ReadTo" + "End()",
        "StandardError.ReadTo" + "End()",
    };

    [Fact]
    public void NoTestInThisProject_ReadsAChildProcessStreamSynchronously()
    {
        var projectDir = Path.Combine(FindRepoRoot(), "Excise.App.Tests");
        Directory.Exists(projectDir).Should().BeTrue(
            "the scan must actually have sources to read; an empty scan is a vacuous pass");

        var sources = Directory
            .EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsGeneratedOrBuildOutput(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        sources.Should().NotBeEmpty("Excise.App.Tests must contain C# sources to scan");

        var violations = new List<string>();
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var needle in BannedReads)
                {
                    if (!lines[i].Contains(needle, StringComparison.Ordinal)) continue;
                    violations.Add(
                        $"{Path.GetRelativePath(projectDir, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        // Assert.Fail rather than Should().BeEmpty(because): AwesomeAssertions
        // treats `because` as a format string, and both the guidance below and
        // any offending source line can contain braces.
        if (violations.Count > 0)
        {
            Assert.Fail(
                "A synchronous ReadToEnd() on a redirected child-process stream can block "
                + "forever (#1068, #1516), and xUnit's Timeout cannot abort it. Drain both "
                + "pipes concurrently and bound the wait instead — start "
                + "ReadToEndAsync() on stdout AND stderr, then WaitForExit(ms), then "
                + "Kill(entireProcessTree: true) if it did not exit. Working examples: "
                + "Unit/CopyReadingOrderTests.RunPdftotext and "
                + "Unit/SignatureApplicationServiceTests. Offending lines:"
                + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", violations));
        }
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    // #1706 — TestRepoLayout, not a hand-rolled walk to .git/excise.sln. LOCAL
    // checkout, deliberately: this reads THIS worktree's own source / writes its
    // own artifacts, and the main checkout may be on a different branch.
    private static string FindRepoRoot() =>
        TestRepoLayout.LocalCheckoutRoot ?? throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
}
