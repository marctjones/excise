using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// <b>The guard for #1527: a corpus-gated gate must COLLECT its rows.</b>
///
/// <para>#1527's failure was not a skip. The gate classes build their theory
/// rows by enumerating the corpus at DISCOVERY time, so when the corpus was
/// unreachable the <c>MemberData</c> source yielded almost nothing and
/// <b>1,071 rows never materialised as tests at all</b>. There was no
/// <c>NotExecuted</c> result to inspect, so #1172's skip-reason gate had
/// nothing to read and #894's test-count gate had nothing to count.
/// <c>Passed! Failed: 0</c> on 39 rows is byte-identical to
/// <c>Passed! Failed: 0</c> on 1,110.</para>
///
/// <para>What actually caught it was somebody thinking 24 skips looked like too
/// many for a class that had just gained two tests — luck about the size of a
/// number. At 2 instead of 24 it would have shipped. A guard that depends on
/// noticing an implausible number is not a guard, so this one is deterministic:
/// <b>if the corpus is reachable and the class collected no rows, fail.</b></para>
///
/// <para><b>Why it lives here and not inside the gate classes.</b> It resolves
/// the corpus INDEPENDENTLY, through <see cref="TestRepoLayout"/>, and then
/// calls each class's own public row source. The failure it detects is exactly
/// a DISAGREEMENT between "the corpus is reachable" and "the class found
/// nothing" — which is undetectable from inside a class that resolves the
/// corpus wrongly, and unprovable in a guard that reverts along with the code
/// it is guarding. Reverting the three classes' locators to their shipped
/// bound-6 form must make this class red while it stays unchanged.</para>
///
/// <para>CLAUDE.md's rule, which #1527 shows was silently false for a month:
/// <i>"the redaction gate is non-negotiable at every tier that produces a
/// binary anyone could redact with."</i> There was no flag. There was a path
/// depth.</para>
/// </summary>
public class CorpusRowFloorGateTests
{
    /// <summary>
    /// How many rows a class must collect, given how many fixture files its
    /// required corpora actually hold on this machine. Always relative to what
    /// is PRESENT, never a hard-coded absolute — a partial corpus must relax
    /// the floor, not redden the gate.
    /// </summary>
    private enum Floor
    {
        /// <summary>
        /// Every fixture file becomes a row: the class filters nothing, so the
        /// floor is the exact count of distinct basenames. Derived from the
        /// class's code, not measured, so it cannot go stale.
        /// </summary>
        EveryFixture,

        /// <summary>
        /// The class drops some fixtures for a declared content reason, so an
        /// exact floor is not derivable. Half is far above any plausible yield
        /// loss and far below the collapse this gate exists to catch.
        /// ⚠️ The exact measured ratchet is OWED from #1527's first
        /// main-checkout run; this is deliberately loose, not calibrated.
        /// </summary>
        AtLeastHalfTheFixtures,
    }

    private sealed record CorpusGate(
        string Name,
        string[] Corpora,
        Func<TheoryData<string>> Rows,
        Floor RowFloor,
        string Why);

    /// <summary>
    /// Every corpus-gated redaction gate, with the corpora it needs and the
    /// row source it publishes. <b>Adding a corpus-gated theory means adding a
    /// row here.</b>
    /// </summary>
    private static readonly CorpusGate[] Gates =
    {
        new("RedactionCollateralHarness",
            new[] { "test-pdfs/smoke", "test-pdfs/federal" },
            RedactionCollateralHarness.Fixtures,
            Floor.EveryFixture,
            "the corpus-wide collateral ratchet — how much of a document a redaction destroys " +
            "beyond the term. This is the gate for #942/#899-class defects, where redaction " +
            "removed 5-36% of a document per term. 101 rows were unexercised."),

        new("ConservationGateTests",
            new[] { "test-pdfs/smoke" },
            ConservationGateTests.Fixtures,
            // EnumerateFixtures keeps only fixtures of 1..20 pages (MaxPages),
            // so its yield is below the file count by however many long
            // fixtures the corpus holds. 80 rows were unexercised.
            Floor.AtLeastHalfTheFixtures,
            "content conservation across page ops, form fill, flatten, merge and split"),

        new("RedactionRemoteCollateralTests",
            new[] { "test-pdfs/smoke", "test-pdfs/federal" },
            RedactionRemoteCollateralTests.Cases,
            Floor.EveryFixture,
            "text destroyed REMOTE from any match — the #942/#944 defect inventory. " +
            "12 of 14 rows were unexercised."),

        // Found by scripts/check-fixture-locators.sh, not by hand: the static
        // census of "classes that enumerate a gitignored corpus into a
        // TheoryData" returned FOUR, and #1527's own list of affected gates
        // named three plus this one as a separate #1525 casualty. Deriving the
        // population instead of writing it out is the point — a hand-listed
        // registry omits exactly the gate nobody was thinking about.
        new("ReferenceRedactorComparisonTests",
            new[] { "test-pdfs/smoke", "test-pdfs/federal" },
            ReferenceRedactorComparisonTests.Fixtures,
            Floor.EveryFixture,
            "excise's redaction against OTHER redactors — the no-self-oracle row. " +
            "Broken by the bound-8 variant of the same defect (#1525)."),
    };

    public static TheoryData<string> GateNames()
    {
        var data = new TheoryData<string>();
        foreach (var g in Gates) data.Add(g.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(GateNames))]
    public void ACorpusGatedRedactionGate_CollectsItsRows_WheneverItsCorpusIsReachable(string gateName)
    {
        var gate = Gates.Single(g => g.Name == gateName);

        // Resolved HERE, independently of the gate class. The disagreement
        // between this answer and the class's own is the defect.
        var reachable = gate.Corpora
            .Select(rel => (Rel: rel, Full: TestRepoLayout.FindDirectory(rel)))
            .Where(c => c.Full != null)
            .ToArray();

        Assert.SkipWhen(reachable.Length == 0,
            TestRepoLayout.AbsenceReason($"{gate.Name}: required corpus", gate.Corpora));

        var available = reachable
            .SelectMany(c => Directory.EnumerateFiles(c.Full!, "*.pdf"))
            .Select(p => Path.GetFileName(p)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.SkipWhen(available == 0,
            $"{gate.Name}: required corpus directories exist but hold no *.pdf " +
            $"[{TestRepoLayout.SearchedMarker} " +
            $"{string.Join(TestRepoLayout.SearchedSeparator, reachable.Select(c => c.Full))}]");

        // A sentinel row is the class saying "I found nothing" — it is not a
        // fixture. Recognised by shape, so this holds both before and after the
        // classes are converted onto the shared locator.
        var rows = gate.Rows().Select(r => r.Data).ToArray();
        var real = rows.Where(r => !r.StartsWith("(", StringComparison.Ordinal)).ToArray();
        var sentinels = rows.Except(real, StringComparer.Ordinal).ToArray();

        var floor = gate.RowFloor switch
        {
            Floor.EveryFixture => available,
            Floor.AtLeastHalfTheFixtures => Math.Max(1, available / 2),
            _ => throw new InvalidOperationException($"unhandled floor {gate.RowFloor}"),
        };

        var evidence =
            $"\n  gate            {gate.Name} — {gate.Why}" +
            $"\n  corpora wanted  {string.Join(", ", gate.Corpora)}" +
            $"\n  corpora found   {string.Join(", ", reachable.Select(c => c.Full))}" +
            $"\n  fixture files   {available}" +
            $"\n  rows collected  {real.Length}" +
            (sentinels.Length == 0 ? "" : $"\n  sentinel rows   {string.Join(", ", sentinels)}") +
            $"\n  floor required  {floor} ({gate.RowFloor})" +
            $"\n  search roots    {string.Join(", ", TestRepoLayout.SearchRoots)}" +
            "\n\n  A corpus this gate needs IS reachable from here, and the gate did not " +
            "collect\n  rows for it. That is #1527: the class resolves the corpus differently " +
            "from\n  TestRepoLayout and loses. Rows that are never COLLECTED are not skipped " +
            "and\n  not counted — nothing else in the suite can see them go missing, and the\n  " +
            "run reports itself green. Fix the class's locator (it must use\n  " +
            "TestRepoLayout); do NOT lower this floor.";

        real.Should().NotBeEmpty("a reachable corpus must produce rows." + evidence);

        real.Length.Should().BeGreaterThanOrEqualTo(floor,
            "a reachable corpus must produce close to as many rows as it has fixtures." + evidence);

        sentinels.Should().BeEmpty(
            "the \"no corpus\" sentinel row exists so an absent corpus skips instead of " +
            "erroring; emitting it alongside real rows means the class half-resolved its " +
            "corpus." + evidence);
    }

    /// <summary>
    /// The specific blindness #1527 names: <c>RedactionCollateralHarness</c>
    /// resolved its own ratchet file — <c>tests/redaction-collateral/baseline.json</c>,
    /// which is TRACKED and so present in every checkout — through the same
    /// bounded helper as the corpus. The baseline therefore always loaded,
    /// comparisons always ran and the ratchet always looked intact while the
    /// corpus it measures against was absent.
    ///
    /// <para><i>A gate whose self-reporting works in both the healthy and the
    /// broken configuration cannot report its own breakage.</i> So assert the
    /// two resolve TOGETHER.</para>
    /// </summary>
    [Fact]
    public void TheCollateralRatchetAndItsCorpus_AreReachableTogether_OrNeitherIs()
    {
        var baseline = TestRepoLayout.FindFile("tests/redaction-collateral/baseline.json");
        var corpus = TestRepoLayout.FindDirectory("test-pdfs/smoke")
                     ?? TestRepoLayout.FindDirectory("test-pdfs/federal");

        Assert.SkipWhen(baseline == null,
            TestRepoLayout.AbsenceReason("collateral ratchet baseline", "tests/redaction-collateral/baseline.json"));

        // The reverse asymmetry is legitimate: a machine may have the corpus
        // without the tracked baseline only if the checkout is broken, which
        // the skip above already reports. What must never happen again is
        // "baseline yes, corpus no" passing for a month.
        if (corpus == null)
            Assert.Skip(TestRepoLayout.AbsenceReason(
                "redaction corpus (the baseline resolved, so this checkout is intact and the " +
                "corpus is genuinely not downloaded)",
                "test-pdfs/smoke", "test-pdfs/federal"));

        Path.GetFullPath(baseline!).Should().StartWith(TestRepoLayout.SearchRoots[0],
            "the ratchet must be read from the checkout the code under test came from, not " +
            "from another checkout at another commit");
    }

    /// <summary>
    /// Every gate in <see cref="Gates"/> must name at least one corpus and a
    /// row source that is actually callable — otherwise a row could be added
    /// here that asserts nothing, which is the shape of the defect.
    /// </summary>
    [Fact]
    public void TheGateRegistry_IsWellFormed()
    {
        Gates.Should().NotBeEmpty();
        Gates.Select(g => g.Name).Should().OnlyHaveUniqueItems();

        foreach (var gate in Gates)
        {
            gate.Corpora.Should().NotBeEmpty($"{gate.Name} must declare what corpus it needs");
            gate.Corpora.Should().OnlyContain(c => c.StartsWith("test-pdfs/", StringComparison.Ordinal));
            gate.Why.Should().NotBeNullOrWhiteSpace();

            // Calling the row source proves it exists and does not throw. It
            // may legitimately return only a sentinel when no corpus is
            // present; that is the theory above's business, not this one's.
            gate.Rows().Should().NotBeNull($"{gate.Name}'s row source must be callable");
        }
    }
}
