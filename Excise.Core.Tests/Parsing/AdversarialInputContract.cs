using System;
using System.Threading.Tasks;
using Excise.Core.Parsing;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// The shared contract the adversarial-input suites assert (#960): on a
/// hostile document excise must either parse it or fail with a *typed* PDF
/// exception. A raw CLR exception (NullReference, IndexOutOfRange, …) is a
/// missing guard; a hang is a missing bound; and a StackOverflowException is
/// worse than either, because .NET cannot catch it — it takes the process
/// down past every timeout and try/catch in the product (#969).
///
/// Kept in one place because three suites assert it —
/// <see cref="HostileStructureTests"/> (hand-built structures),
/// <see cref="StructureAwareFuzzTests"/> (mutated real fixtures), and the
/// pre-existing <see cref="ParserFuzzTests"/> shape it was factored from.
/// </summary>
internal static class AdversarialInputContract
{
    /// <summary>
    /// Exception types that indicate a *handled* malformed-input condition
    /// rather than a missing guard. Deliberately the same set as
    /// <see cref="ParserFuzzTests"/>'s — widening it here would silently
    /// weaken the older gate's sibling.
    /// </summary>
    public static bool IsGraceful(Exception ex) =>
        ex is PdfParseException
        || ex is PdfEncryptionNotSupportedException
        || ex is NotSupportedException          // e.g. unknown stream filter
        || ex is System.IO.EndOfStreamException  // truncated stream
        // PdfPage's strict content-stream read refuses an image-only filter
        // (/JBIG2Decode) on a page content stream by design, and that refusal
        // is itself pinned by PdfPageTests (`strictRead.Should()
        // .Throw<InvalidDataException>()`). It is a documented decision about
        // a well-understood document, not a missing guard — the one addition
        // to #352's set, made because the fuzzer reached an EXISTING contract
        // rather than because the fuzzer was inconvenient.
        || ex is System.IO.InvalidDataException
        // Likewise pinned, by PdfPageTests
        // (`strictRead.Should().Throw<InvalidOperationException>()
        // .WithMessage("Stream has not been decoded*")`): a strict read of a
        // malformed FILTERED content stream refuses by design.
        //
        // Matched on the MESSAGE deliberately. InvalidOperationException is a
        // generic CLR type and blanket-accepting it would let every genuine
        // invalid-state bug through this gate — the exact hole the contract
        // exists to close. Narrow and brittle beats wide and quiet; if the
        // message changes, this line failing is the correct outcome.
        //
        // (Arguably the product should raise PdfParseException there — an
        // undecodable stream is a document problem, and "Call Decode() first"
        // is an internal API-contract message leaking to end users. That is a
        // caller-visible change to a pinned contract, so it is recorded on
        // #974 rather than made silently here.)
        || (ex is InvalidOperationException
            && ex.Message.StartsWith("Stream has not been decoded", StringComparison.Ordinal));

    /// <summary>
    /// Rethrows as a test failure unless <paramref name="ex"/> is graceful.
    /// <paramref name="repro"/> must identify the exact input — a seed, or a
    /// named structure — because a fuzz failure nobody can reproduce is a
    /// flake report, not a bug report.
    /// </summary>
    public static void AssertGraceful(Exception ex, string repro, byte[] input)
    {
        if (IsGraceful(ex)) return;
        throw new Xunit.Sdk.XunitException(
            $"{repro} (len={input.Length}): excise threw a raw {ex.GetType().Name} " +
            $"(\"{ex.Message}\") on hostile input instead of a typed PdfParseException. " +
            "This is a missing guard — fix the parser, do not widen IsGraceful. First bytes: " +
            BitConverter.ToString(input, 0, Math.Min(32, input.Length)) +
            "\nSTACK:\n" + ex.StackTrace);
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a hard wall-clock budget so a
    /// missing loop bound fails the test in seconds instead of hanging the
    /// suite. Same shape as the #648 cmap-format-12 regression.
    ///
    /// A budget breach cannot cancel the runaway work (there is nothing to
    /// cancel it with — that is the defect being reported), so the worker
    /// thread is left running and the test fails. That is the correct trade:
    /// the run ends either way, and it ends with a diagnosis.
    /// </summary>
    public static async Task WithinBudget(string repro, TimeSpan budget, Action body)
    {
        var work = Task.Run(body);
        var winner = await Task.WhenAny(work, Task.Delay(budget));
        if (winner != work)
            throw new Xunit.Sdk.XunitException(
                $"{repro}: still running after {budget.TotalSeconds:F0}s — a hostile " +
                "structure must be bounded, not merely survivable.");
        await work; // surface the real exception, if any, to the caller
    }
}
