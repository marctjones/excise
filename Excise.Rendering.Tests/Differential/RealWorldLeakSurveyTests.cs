using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1590/#1603 — the bench's real-world rows, scored positionally.
///
/// <para>Until this class existed the manifest was VALIDATED but never USED:
/// <c>UnredactionBenchManifestTests</c> checks the file's shape, and nothing
/// opened a single document it names. Two vetted PDFs sat in the corpus
/// directory scored by nothing.</para>
/// </summary>
public class RealWorldLeakSurveyTests
{
    private readonly Xunit.ITestOutputHelper _out;
    public RealWorldLeakSurveyTests(Xunit.ITestOutputHelper o) => _out = o;

    private static IReadOnlyList<RealWorldLeakSurvey.Document> Fetched(out string? corpus)
    {
        corpus = RealWorldLeakSurvey.CorpusDirectory;
        if (corpus == null) return Array.Empty<RealWorldLeakSurvey.Document>();
        var dir = corpus;
        return RealWorldLeakSurvey.Documents().Where(d => d.Path(dir) != null).ToList();
    }

    /// <summary>
    /// ⚠️ THE #1602 GATE, and the reason this survey may point at tier D at all.
    ///
    /// <para>A survey row must have no field that can carry document text. This
    /// is checked by REFLECTION over the record's own properties rather than by
    /// reading the code, because the failure mode is somebody adding a
    /// convenient <c>RecoveredText</c> field later and every other test still
    /// passing. Counts, enums, booleans and the channel-name list are allowed;
    /// a free-form string is not.</para>
    /// </summary>
    [Fact]
    public void SurveyRowsHoldNoRecoveredText()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Identifiers from the TRACKED manifest and the report's own
            // vocabulary — none of them can hold text read out of a document.
            nameof(RealWorldLeakSurvey.MarkRow.DocumentId),
            nameof(RealWorldLeakSurvey.MarkRow.Tier),
            nameof(RealWorldLeakSurvey.MarkRow.MarkId),
            nameof(RealWorldLeakSurvey.MarkRow.Channels),
        };

        var offenders = typeof(RealWorldLeakSurvey.MarkRow).GetProperties()
            .Where(p => p.PropertyType == typeof(string)
                        || p.PropertyType == typeof(IReadOnlyList<string>))
            .Select(p => p.Name)
            .Where(n => !allowed.Contains(n))
            .ToList();

        offenders.Should().BeEmpty(
            "a real-world survey row must be incapable of holding recovered text (#1602) — " +
            "tier-D redactions cover victim names, and a row that CAN carry a value will " +
            "eventually carry one into a trx or a log");
    }

    /// <summary>
    /// The survey runs and reports leak CLASSES, with x-ray as the independent
    /// opinion. No recall, no ground truth — those need an answer key this tier
    /// does not and must not have.
    /// </summary>
    [Fact]
    public void Survey_ReportsLeakClassesAndCorroboration()
    {
        var documents = Fetched(out var corpus);
        Assert.SkipUnless(documents.Count > 0, TestRepoLayout.AbsenceReason(
            "unredaction bench documents (scripts/download-unredaction-bench.sh)",
            Path.Combine("test-pdfs", "unredaction-bench")));

        var notMeasured = new List<string>();
        if (!XRayBadRedactionDetector.IsAvailable)
            notMeasured.Add("x-ray not installed — no independent corroboration");

        var unfetched = RealWorldLeakSurvey.Documents()
            .Where(d => d.Status == "vetted" && d.Path(corpus!) == null).ToList();
        if (unfetched.Count > 0)
            notMeasured.Add($"{unfetched.Count} vetted document(s) not fetched");

        var rows = documents.SelectMany(d => RealWorldLeakSurvey.Survey(d, d.Path(corpus!)!)).ToList();
        _out.WriteLine(RealWorldLeakSurvey.Render(rows, notMeasured));

        rows.Should().NotBeEmpty("a fetched document must yield at least one mark row");
    }

    /// <summary>
    /// ⚠️ THE ROW THAT MAKES THE WHOLE BENCH WORTH HAVING.
    ///
    /// <para>Manafort 471 and 472 are the SAME BRIEF filed the same day — one
    /// with a redaction that leaked, one with it applied properly. No synthetic
    /// fixture can produce that pair, because a fixture's "properly redacted"
    /// arm is written by us and inherits our own blind spots.</para>
    ///
    /// <para>It pins BOTH directions at once, which is what separates a
    /// detector from an eager one: a change that stops finding 471's leak fails
    /// here, and so does a change that starts "finding" something in 472. A
    /// one-sided assertion would let a maximally eager detector score
    /// perfectly.</para>
    ///
    /// <para>This is the #1617 regression pin: before that fix excise read an
    /// unset fill colour as white rather than §8.6.8 black and reported 0 marks
    /// and 0 findings on 471 — total blindness on a real leaking document, with
    /// the whole synthetic suite green.</para>
    /// </summary>
    [Fact]
    public void TheManafortPair_LeaksOnTheBreachResponse_AndHoldsOnTheCorrectedRefiling()
    {
        var corpus = RealWorldLeakSurvey.CorpusDirectory;
        Assert.SkipUnless(corpus != null, TestRepoLayout.AbsenceReason(
            "unredaction bench corpus", Path.Combine("test-pdfs", "unredaction-bench")));

        var byId = RealWorldLeakSurvey.Documents().ToDictionary(d => d.Id, StringComparer.Ordinal);
        var leaky = byId["manafort-2019-breach-response-471"];
        var clean = byId["manafort-2019-breach-response-472"];

        Assert.SkipUnless(leaky.Path(corpus!) != null && clean.Path(corpus!) != null,
            TestRepoLayout.AbsenceReason(
                "the Manafort pair (scripts/download-unredaction-bench.sh)",
                Path.Combine("test-pdfs", "unredaction-bench", "manafort-2019-breach-response-471.pdf"),
                Path.Combine("test-pdfs", "unredaction-bench", "manafort-2019-breach-response-472.pdf")));

        var leakyRows = RealWorldLeakSurvey.Survey(leaky, leaky.Path(corpus!)!);
        var cleanRows = RealWorldLeakSurvey.Survey(clean, clean.Path(corpus!)!);

        _out.WriteLine(RealWorldLeakSurvey.Render(leakyRows.Concat(cleanRows).ToList(),
            XRayBadRedactionDetector.IsAvailable ? Array.Empty<string>() : new[] { "x-ray not installed" }));

        leakyRows.Count(r => r.Leaks).Should().BeGreaterThan(0,
            "471 is a documented real-world leak — pdftotext reads word types present here and " +
            "absent from 472, and excise reporting nothing on it is exactly the #1617 defect");

        cleanRows.Should().NotBeEmpty("472's covering marks must still be DETECTED");
        cleanRows.Count(r => r.Leaks).Should().Be(0,
            "472 is the corrected refiling and the bench's negative control: recovering anything " +
            "from it is a false positive, which no amount of recall on 471 excuses");
    }

    /// <summary>
    /// A vetted manifest row whose file is present must be scannable. A
    /// document that crashes or hangs the scanner is a finding, not a skip —
    /// this is the class of failure the real-world tier exists to surface.
    /// </summary>
    [Fact]
    public void EveryFetchedDocument_ScansWithoutThrowing()
    {
        var documents = Fetched(out var corpus);
        Assert.SkipUnless(documents.Count > 0, TestRepoLayout.AbsenceReason(
            "unredaction bench documents", Path.Combine("test-pdfs", "unredaction-bench")));

        foreach (var document in documents)
        {
            var act = () => RealWorldLeakSurvey.Survey(document, document.Path(corpus!)!);
            act.Should().NotThrow($"{document.Id} is a vetted bench document");
        }
    }
}
