using System;
using System.IO;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Excise.Cli.Tests.PublicApi;

/// <summary>
/// Public-API snapshot for <c>Excise.Cli</c> (#910). Unlike the packable-library
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
    public void ExciseCli_ExportsNoPublicTypes()
    {
        // The CLI is deliberately ALL-INTERNAL (tests reach it via
        // InternalsVisibleTo). That is the correct state for an app assembly —
        // public surface on an executable is API nobody can consume but the
        // unwired check must still track (#910). So the gate here is the
        // INVERSE of a snapshot: the assembly must stay internal-only, and any
        // new public type is a deliberate decision that converts this test
        // into a baseline snapshot like its siblings.
        var publics = typeof(Excise.Cli.Program).Assembly
            .GetExportedTypes();
        publics.Should().BeEmpty(
            "Excise.Cli is internal-only by design; a new public type is either " +
            "an accident or the moment this test should become a snapshot gate");
    }

}
