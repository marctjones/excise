using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Which carrier traps still hold their token after a DEFAULT redaction, on
/// today's develop (#1581, #1583).
///
/// <para>Both issues were filed before the redaction profiles (#1586), which
/// removes all JavaScript and every external-effect action by walking the
/// reachable object graph, drops <c>/PieceInfo</c>, and strips <c>/Info</c> and
/// XMP wholesale under Standard. So some of what they report is very likely
/// already closed and some is not — and planning off the issue text without
/// re-measuring is what CLAUDE.md warns about.</para>
///
/// <para>This prints the survey and asserts only the part that is settled: a
/// trap the project believes is closed must stay closed. The printed table is
/// the input to fixing the rest.</para>
/// </summary>
public class CarrierTrapSurveyTests
{
    private readonly ITestOutputHelper _out;

    public CarrierTrapSurveyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Survey_WhichCarriersStillHoldTheTokenAfterADefaultRedaction()
    {
        var surviving = new List<string>();
        var scrubbed = new List<string>();

        foreach (var trap in CarrierTrapFixtures.All)
        {
            string verdict;
            try
            {
                using var document = PdfDocument.Open(trap.Build(true));
                document.RedactText(trap.Token);
                var saved = document.SaveToBytes();
                var hits = SavedPdfLeakScanner.FindTerm(saved, trap.Token);
                verdict = hits.Count == 0 ? "scrubbed" : $"SURVIVES in {string.Join(", ", hits.Take(3))}";
            }
            catch (Exception ex)
            {
                verdict = $"ERROR {ex.GetType().Name}: {ex.Message}";
            }

            _out.WriteLine($"{trap.Id,-34} {trap.ExpectedCarrier,-30} {verdict}");
            (verdict == "scrubbed" ? scrubbed : surviving).Add(trap.Id);
        }

        _out.WriteLine("");
        _out.WriteLine($"scrubbed: {scrubbed.Count}   still holding the token: {surviving.Count}");
        if (surviving.Count > 0)
            _out.WriteLine("still holding: " + string.Join(", ", surviving));

        // Every trap must produce a verdict. A survey that silently skipped the
        // leaking ones would read exactly like a clean sweep.
        (scrubbed.Count + surviving.Count).Should().Be(CarrierTrapFixtures.All.Count,
            "every trap must be surveyed, or the table understates the leak");

        // A RATCHET, not a snapshot: exactly one carrier may still hold its
        // token, and only because the term is two characters long. The
        // sanitizer refuses to strip a term below three characters from a
        // free-text carrier (RedactionOptions: "the 3-character scrub floor is
        // REPORTED, not configurable"), because excising "of" from every /Alt
        // corrupts unrelated values. That refusal is reported to the caller, so
        // it is a declared limit rather than a silent leak.
        //
        // Anything else appearing here is a regression in the core guarantee.
        surviving.Should().BeEquivalentTo(["structure-alt-short-term"],
            "every other carrier must be scrubbed by a default redaction; the one exception is the "
            + "documented short-term floor, whose token is two characters");
        CarrierTrapFixtures.Get("structure-alt-short-term").Token.Length.Should().BeLessThan(3,
            "if that trap's token grows past the floor it stops being an exception and must scrub");
    }
}
