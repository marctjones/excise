using System;
using System.IO;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Excise.App.Tests.PublicApi;

/// <summary>
/// Public-API snapshot for <c>Excise.App</c> (#910). Unlike the packable-library
/// gates (#383/#384), the point here is not SemVer stability — it is
/// INVENTORY: <c>scripts/check-unwired-api.sh</c> reads
/// <c>PublicApi/*.approved.txt</c> as its universe, and until this file
/// existed the assembly's ~public surface was invisible to the
/// implemented-but-unreachable check. #920's 283 dead lines and #928's
/// mis-documented redaction path both lived in exactly that blind spot.
/// Accept changes with <c>APPROVE_PUBLIC_API=1</c>; the diff is the review.
/// </summary>
public class PublicApiApprovalTests
{
    [Fact]
    public void ExciseApp_PublicApi_MatchesApprovedBaseline()
    {
        var api = typeof(Excise.App.Services.PdfDocumentService).Assembly
            .GeneratePublicApi(new ApiGeneratorOptions { IncludeAssemblyAttributes = false })
            .Replace("\r\n", "\n")
            .TrimEnd() + "\n";
        var file = Path.Combine(ApprovedDir(), "Excise.App.approved.txt");
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
            "the Excise.App public API must not change without an intentional review. " +
            "If deliberate, re-run with APPROVE_PUBLIC_API=1 and commit the updated Excise.App.approved.txt.");
    }

    private static string ApprovedDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;   // this file lives in PublicApi/ already
}
