using System;
using System.IO;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Pdfe.Avalonia.Tests;

/// <summary>
/// Public-API gate for the packable viewer libraries <c>Pdfe.Avalonia</c> and
/// <c>Pdfe.Rendering</c> (issue #384 — the DX/stability treatment #383 gave the
/// writer, applied to the viewer/render side).
///
/// <para>
/// Each test snapshots the full public surface of one assembly and compares it to
/// a committed baseline under <c>PublicApi/</c>. Any change to the public API — a
/// new/removed/renamed public type or member, a changed signature — fails the
/// test, forcing an intentional SemVer review before it ships.
/// </para>
///
/// <para>
/// To accept an intentional change: re-run with the <c>APPROVE_PUBLIC_API=1</c>
/// env var (the test regenerates the baseline) and commit the updated
/// <c>*.approved.txt</c>. The diff in review *is* the API-change review. This is a
/// pure-reflection test (no Avalonia runtime), so it runs reliably outside the
/// flaky headless GUI suite.
/// </para>
/// </summary>
public class ViewerPublicApiApprovalTests
{
    [Fact]
    public void PdfeAvalonia_PublicApi_MatchesApprovedBaseline()
        => AssertPublicApi(typeof(global::Pdfe.Avalonia.Controls.PdfViewerControl), "Pdfe.Avalonia.approved.txt");

    [Fact]
    public void PdfeRendering_PublicApi_MatchesApprovedBaseline()
        => AssertPublicApi(typeof(global::Pdfe.Rendering.SkiaRenderer), "Pdfe.Rendering.approved.txt");

    private static void AssertPublicApi(Type anchor, string approvedFile)
    {
        var api = anchor.Assembly
            .GeneratePublicApi(new ApiGeneratorOptions { IncludeAssemblyAttributes = false })
            .Replace("\r\n", "\n")
            .TrimEnd();

        var approvedPath = Path.Combine(ApprovedDir(), approvedFile);

        bool approveMode = Environment.GetEnvironmentVariable("APPROVE_PUBLIC_API") == "1";
        if (approveMode || !File.Exists(approvedPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(approvedPath)!);
            File.WriteAllText(approvedPath, api + "\n");
            File.Exists(approvedPath).Should().BeTrue();
            return;
        }

        var approved = File.ReadAllText(approvedPath).Replace("\r\n", "\n").TrimEnd();
        api.Should().Be(approved,
            $"the {anchor.Assembly.GetName().Name} public API must not change without an intentional " +
            "SemVer review. If deliberate, re-run with APPROVE_PUBLIC_API=1 and commit the updated " +
            approvedFile + ".");
    }

    /// <summary>Locate the PublicApi folder next to this test source file.</summary>
    private static string ApprovedDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "PublicApi");
}
