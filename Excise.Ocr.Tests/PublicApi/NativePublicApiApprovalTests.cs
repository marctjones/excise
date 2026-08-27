using System;
using System.IO;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Excise.Ocr.Tests.PublicApi;

/// <summary>
/// Public-API snapshot for <c>Excise.Ocr.Native</c> (#1139), mirroring the
/// <c>Excise.Ocr</c> gate: the point is INVENTORY, not SemVer.
/// <c>scripts/check-unwired-api.sh</c> reads <c>PublicApi/*.approved.txt</c> as
/// its universe of public members, so a new binding project's surface must be
/// snapshotted or it becomes invisible to the implemented-but-unreachable
/// check. Accept changes with <c>APPROVE_PUBLIC_API=1</c>; the diff is the
/// review.
/// </summary>
public class NativePublicApiApprovalTests
{
    [Fact]
    public void ExciseOcrNative_PublicApi_MatchesApprovedBaseline()
    {
        var api = typeof(Excise.Ocr.Native.NativeOcrEngine).Assembly
            .GeneratePublicApi(new ApiGeneratorOptions { IncludeAssemblyAttributes = false })
            .Replace("\r\n", "\n")
            .TrimEnd() + "\n";
        var file = Path.Combine(ApprovedDir(), "Excise.Ocr.Native.approved.txt");
        if (Environment.GetEnvironmentVariable("APPROVE_PUBLIC_API") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, api);
            return;
        }
        File.Exists(file).Should().BeTrue(
            $"missing baseline {file} — run once with APPROVE_PUBLIC_API=1");
        var approved = File.ReadAllText(file).Replace("\r\n", "\n");
        api.Should().Be(approved,
            "the Excise.Ocr.Native public API must not change without an intentional review. " +
            "If deliberate, re-run with APPROVE_PUBLIC_API=1 and commit the updated baseline.");
    }

    private static string ApprovedDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;
}
