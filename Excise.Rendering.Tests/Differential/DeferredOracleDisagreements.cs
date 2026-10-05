using System.Security.Cryptography;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Narrow, observed classifications approved for deferral (#1953, #1954).
/// These are not Unicode ground truth or product fixes. Changed fixtures,
/// missing references and changed product results must still fail the gate.
/// </summary>
internal static class DeferredOracleDisagreements
{
    internal const string MappingFixtureHash = "ac32f419e728d1fa96daa24172871f8d38cf7ad6a7f3076a2f86b76a6811517e";
    internal const string CountFixtureHash = "bea02a5e94be1ffb526a98f3dd6c5e50fd53a3b14f0c70cb13be04868ffd0cb3";

    internal static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static bool IsUnresolvedMapping(string hash, int excise, int mutool, int? poppler)
        => hash == MappingFixtureHash && excise == 2 && mutool == 36 && poppler == 1;

    internal static bool CountIsCorroborated(string hash, int reported,
        int? structuredBefore, int? structuredAfter, int? popplerBefore, int? popplerAfter)
        => hash == CountFixtureHash && reported > 0
            && structuredBefore == reported && structuredAfter == 0
            && popplerBefore == reported && popplerAfter == 0;
}
