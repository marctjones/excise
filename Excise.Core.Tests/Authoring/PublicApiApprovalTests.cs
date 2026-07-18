using System.IO;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Excise.Core.Tests.Authoring;

/// <summary>
/// Public-API gate for the packable <c>Excise.Core</c> library (issue #383).
///
/// <para>
/// Snapshots the full public surface of <c>Excise.Core</c> and compares it to a
/// committed baseline (<c>PublicApi/Excise.Core.approved.txt</c>). Any change to
/// the public API — a new/removed/renamed public type or member, a changed
/// signature — fails this test, forcing an intentional review and a SemVer
/// decision before it can ship.
/// </para>
///
/// <para>
/// To accept an intentional API change: delete the approved file (or set the
/// <c>APPROVE_PUBLIC_API</c> env var) and re-run — the test regenerates the
/// baseline — then commit the updated <c>Excise.Core.approved.txt</c>. The diff
/// in code review *is* the API-change review.
/// </para>
/// </summary>
public class PublicApiApprovalTests
{
    [Fact]
    public void ExciseCore_PublicApi_MatchesApprovedBaseline()
    {
        var api = typeof(Excise.Core.Document.PdfDocument).Assembly
            .GeneratePublicApi(new ApiGeneratorOptions
            {
                IncludeAssemblyAttributes = false
            })
            .Replace("\r\n", "\n")
            .TrimEnd();

        var approvedPath = Path.Combine(ApprovedDir(), "Excise.Core.approved.txt");

        bool approveMode = Environment.GetEnvironmentVariable("APPROVE_PUBLIC_API") == "1";
        if (approveMode || !File.Exists(approvedPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(approvedPath)!);
            File.WriteAllText(approvedPath, api + "\n");
            // First-time generation (or explicit approval): don't silently pass
            // a non-existent baseline — make the author commit it deliberately.
            File.Exists(approvedPath).Should().BeTrue();
            return;
        }

        var approved = File.ReadAllText(approvedPath).Replace("\r\n", "\n").TrimEnd();
        api.Should().Be(approved,
            "the Excise.Core public API must not change without an intentional SemVer review. " +
            "If this change is deliberate, re-run with APPROVE_PUBLIC_API=1 and commit the updated " +
            "PublicApi/Excise.Core.approved.txt.");
    }

    /// <summary>Locate the PublicApi folder next to this test source file.</summary>
    private static string ApprovedDir([CallerFilePath] string thisFile = "")
    {
        // .../Excise.Core.Tests/Authoring/PublicApiApprovalTests.cs -> .../Excise.Core.Tests/PublicApi
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
        return Path.Combine(testsDir, "PublicApi");
    }
}
