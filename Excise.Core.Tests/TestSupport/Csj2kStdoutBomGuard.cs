using System;
using System.Runtime.CompilerServices;

namespace Excise.TestSupport;

/// <summary>
/// Keeps CSJ2K's one-time UTF-8 BOM out of the xunit v3 result channel (#1701).
/// </summary>
/// <remarks>
/// <para>
/// CSJ2K's <c>FacilityManager</c> static constructor builds a
/// <c>DotnetMsgLogger</c>, whose base <c>StreamMsgLogger</c> wraps the RAW
/// stdout and stderr streams in <c>new StreamWriter(stream, Encoding.UTF8) {
/// AutoFlush = true }</c>. Setting AutoFlush flushes the preamble, so the first
/// JPX decode in a process writes EF BB BF to file descriptor 1, bypassing
/// <c>Console.Out</c>.
/// </para>
/// <para>
/// Under <c>dotnet test</c> the xunit v3 VSTest adapter runs the test assembly
/// out of process and reads one JSON message per stdout line. The BOM lands in
/// front of the next message, which is normally the decoding test's own
/// <c>test-passed</c>; the adapter logs "Received unparseable output from test
/// process" (visible only with <c>xUnit.InternalDiagnosticMessages=true</c>) and
/// drops the result. The test is discovered, runs, passes, and is absent from the
/// trx. Measured 2026-10-19: ImageRequirementVerificationDifferentialTests listed
/// 32 and reported 31, losing JpxEncodedSolidColor_MatchesMutoolDecode, the
/// registry's only differential for image-filter:JPXDecode.
/// </para>
/// <para>
/// This runs the constructor before xunit writes anything and ends the BOM's
/// line, so the adapter sees one unparseable line holding only the BOM and no
/// message is lost. Every test assembly that can reach <c>JpxDecoder</c>
/// compiles this file. The CLI keeps the BOM off its own stdout with
/// <c>Csj2kQuietStartup</c> (#2084); test hosts never run the CLI's
/// <c>Main</c>, so this guard is still needed.
/// </para>
/// </remarks>
internal static class Csj2kStdoutBomGuard
{
    [ModuleInitializer]
    internal static void EmitCsj2kPreambleBeforeTheResultChannelOpens()
    {
        RuntimeHelpers.RunClassConstructor(typeof(CSJ2K.j2k.util.FacilityManager).TypeHandle);
        // Not disposed: these wrap the process's own descriptors 1 and 2.
        var stdout = Console.OpenStandardOutput();
        stdout.Write("\n"u8);
        stdout.Flush();
        var stderr = Console.OpenStandardError();
        stderr.Write("\n"u8);
        stderr.Flush();
    }
}
