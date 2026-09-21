using System.IO;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1707 — the exit status is a function of (evidence kind × mark link), not of
/// "is it text".
///
/// <para><b>The failure this file exists to prevent, measured before the fix.</b>
/// A one-page PDF with a 2x2 grey image drawn at 120x120 and fully covered by an
/// opaque black box — the commonest viewer-based "redaction", XObject intact —
/// run with <c>--include-deferred</c>, printed:</para>
///
/// <code>
/// MARKS — 1 redaction mark(s): 0 recovered, 0 partially recovered, 1 candidates only, 0 nothing recovered.
/// QUANTIFICATION — 1 finding(s), 0 RECOVERED: 0 text present, 0 width-residue gap(s) leaking 0 bits total.
/// $ echo $?
/// 0
/// </code>
///
/// <para><b>Exit 0.</b> The tool found the leak, printed it, and then told every
/// script that the document is clean. The guard that produced it —
/// <c>all.Any(f =&gt; f.Confidence != "present-only")</c> — was defending a real
/// case (a thumbnail somewhere in the file is furniture, not a breach) with a
/// rule too broad to tell furniture from a breach. The discriminator already
/// existed and was never consulted: <c>RecoveredFinding.MarkId</c>.</para>
///
/// <para><b>Every case here is a PAIR.</b> "Image under a mark exits non-zero"
/// also passes under the wrong fix — <i>any</i> present-only exits non-zero —
/// which would break the thumbnail case the old comment was right to defend. It
/// is the no-mark control below, and
/// <c>UnredactCarrierChannelTests.Handler_PresenceOnly_DoesNotSetTheCertainExitCode</c>,
/// that make the mark link the thing under test rather than the confidence.</para>
/// </summary>
public class UnredactMarkBreachExitTests
{
    /// <summary>
    /// The planted failure. Exit 5 = material survives under a mark, not decoded.
    /// </summary>
    [Fact]
    public void MaterialSurvivingUnderAMark_DoesNotExitClean()
    {
        var path = UnredactFixtures.Write(UnredactFixtures.ImageUnderBox());
        try
        {
            var outcome = Run(path, includeDeferred: true);

            outcome.ExitCode.Should().Be(5,
                "an intact image under an opaque box is the leak this tool exists to find; " +
                "exit 0 tells a script the document is clean");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The other half of the pair: the same confidence with NO mark link stays
    /// exit 0. A page thumbnail is furniture — reporting it is right, failing a
    /// caller's pipeline over it is not.
    /// </summary>
    [Fact]
    public void PresentOnlyWithNoMarkLink_StillExitsClean()
    {
        var outcome = RunOn(Excise.TestSupport.CarrierTrapFixtures.Get("page-thumbnail").Build(false), out var path);
        try
        {
            outcome.Report!.Present.Should().NotBeNullOrEmpty(
                "the control is only a control if the fixture really carries present-only evidence");
            outcome.ExitCode.Should().Be(0,
                "present-only material that no redaction mark covers is furniture, not a breach");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The mark-linked codes must not displace the text one: a recovered string
    /// under a box is still the most serious finding and still exits 3.
    /// </summary>
    [Fact]
    public void TextRecoveredUnderAMark_StillExitsThree()
    {
        var path = UnredactFixtures.Write(UnredactFixtures.TextUnderBox("MANAFORT"));
        try
        {
            var outcome = Run(path, includeDeferred: false);

            outcome.Report!.Certain.Should().Contain(f => f.Text!.Contains("MANAFORT"),
                "the fixture is only a regression guard if the text really comes back");
            outcome.ExitCode.Should().Be(3, "verbatim text under a mark is the strongest verdict");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The wire says what happened. <c>UnredactRecoveryMapper.OutcomeOf</c> ends
    /// in <c>_ =&gt; "unknown"</c>, so a forgotten case degrades silently rather
    /// than failing to compile — assert the string, not just the enum.
    /// </summary>
    [Fact]
    public void MaterialSurvivingUnderAMark_IsNotReportedAsACandidate()
    {
        var path = UnredactFixtures.Write(UnredactFixtures.ImageUnderBox());
        try
        {
            var outcome = Run(path, includeDeferred: true);
            var recovery = outcome.Report!.Recovery!;

            recovery.MarkSummaries.Should().ContainSingle()
                .Which.Outcome.Should().Be("content-survives",
                    "there are no candidates in this report; calling it candidates-only " +
                    "printed a confirmed breach as a maybe");
            recovery.MarksContentSurvives.Should().Be(1);
            recovery.MarksCandidatesOnly.Should().Be(0);

            using var human = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, human, TextWriter.Null);
            var text = human.ToString();

            text.Should().Contain("content survives undecoded");
            text.Should().Contain("✗ p1m1",
                "the symbol a reader skims must not be the ✓ that means the redaction held");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// #1707 — the caller picks the threshold. Both halves: the default fails,
    /// the narrowed threshold passes on the SAME document. Either alone would
    /// pass on a flag that does nothing.
    /// </summary>
    [Fact]
    public void FailOnText_TolerantOfMaterialUnderAMark_WhereTheDefaultIsNot()
    {
        var path = UnredactFixtures.Write(UnredactFixtures.ImageUnderBox());
        try
        {
            Run(path, includeDeferred: true).ExitCode.Should().Be(5, "the default is --fail-on any");
            Run(path, includeDeferred: true, failOn: "text").ExitCode.Should().Be(0,
                "a caller who only cares about recovered text asked for exactly that");
            Run(path, includeDeferred: true, failOn: "present").ExitCode.Should().Be(5);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A misspelt threshold is refused. Defaulting it would make a pipeline
    /// silently stricter or laxer than its author wrote, with no output saying
    /// so — the same shape of failure as the exit code this file is about.
    /// </summary>
    [Fact]
    public void AnUnrecognisedFailOnValue_IsRefusedNotDefaulted()
    {
        var path = UnredactFixtures.Write(UnredactFixtures.ImageUnderBox());
        try
        {
            var outcome = Run(path, includeDeferred: true, failOn: "txet");

            outcome.ExitCode.Should().Be(2);
            outcome.Error.Should().Contain("--fail-on");

            // The reason has to reach a HUMAN, not just the return value: the
            // handler's Error is an API fact, and UnredactCommandOutput is what
            // decides whether anything is printed at all.
            using var stderr = new StringWriter();
            UnredactCommandOutput.Write(outcome, json: false, TextWriter.Null, stderr);
            stderr.ToString().Should().Contain("--fail-on")
                .And.Contain("any, text, constrained, or present",
                    "a refusal that does not name the accepted values makes the caller guess");
        }
        finally { File.Delete(path); }
    }

    private static UnredactCommandOutcome Run(string path, bool includeDeferred) =>
        UnredactCommandHandler.Execute(
            new UnredactCommandInput(
                path, "certain", DictionaryPath: null, Tolerance: 0.5, MaxCandidates: 200,
                UseOcr: false, NoCorroboration: false, IncludeDeferred: includeDeferred),
            TestContext.Current.CancellationToken);

    private static UnredactCommandOutcome Run(string path, bool includeDeferred, string? failOn) =>
        UnredactCommandHandler.Execute(
            new UnredactCommandInput(
                path, "certain", DictionaryPath: null, Tolerance: 0.5, MaxCandidates: 200,
                UseOcr: false, NoCorroboration: false, IncludeDeferred: includeDeferred,
                FailOn: failOn),
            TestContext.Current.CancellationToken);

    private static UnredactCommandOutcome RunOn(byte[] pdf, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"excise-unredact-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return Run(path, includeDeferred: false);
    }
}
