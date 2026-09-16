using AwesomeAssertions;
using Xunit;

using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

/// <summary>
/// #1519. The <c>render-quality-scan</c> row of tier <c>full</c> could not go
/// red for any rendering defect: its exit code was
/// <c>!strictContracts || missingContractPages == 0</c>, so an expectation
/// departure, a quality FAIL, a MISSING_CONTENT and an EXCISE_SIDE_GAP all
/// exited 0 — while the row's own note in <c>tests/gates.tsv</c> claimed
/// "--strict-contracts fails on departure".
///
/// These tests drive <c>render-quality-classify</c>, which shares
/// <see cref="RenderProgram.ReportRenderingQualityVerdict"/> with the scan, so
/// the rule they pin is the rule the scan exits on. The end-to-end proof that
/// the PROCESS exits non-zero is <c>scripts/test-render-quality-verdict.sh</c>
/// (a SELFTEST row); this file is the fast, per-rule half.
/// </summary>
public class RenderQualityVerdictTests
{
    private const string CleanContract = """
        {
          "Path": "synthetic/one.pdf",
          "Owner": "rendering:quality",
          "RootCause": "SYNTHETIC_FIXTURE",
          "Pages": {
            "1": {
              "ExpectedRawStatus": "PASS",
              "ReleaseStatus": "PASS",
              "QualityStatus": "PIXEL_EXACT",
              "PixelAgreement": "MATCHES_ALL_REQUIRED",
              "ReferenceSituation": "REFS_AGREE",
              "ReviewStatus": "REVIEWED",
              "QualityReason": "Synthetic fixture for the #1519 verdict tests.",
              "Target": { "Mode": "REFERENCE_CONSENSUS", "Primary": "mutool" }
            }
          }
        }
        """;

    private static string RawReport(string entriesJson) => $$"""
        {
          "generatedUtc": "2026-09-16T00:00:00.0000000Z",
          "corpus": "synthetic",
          "counts": {},
          "entries": [{{entriesJson}}]
        }
        """;

    private static string Entry(string path, int page, string status) => $$"""
        {
          "path": "{{path}}",
          "pageNumber": {{page}},
          "status": "{{status}}",
          "resultStatus": "PASS",
          "comparedOracles": 3,
          "agreeingOracles": 3,
          "oracleComparisonPairs": 3,
          "oracleDisagreeingPairs": 0
        }
        """;

    private sealed class Fixture : IDisposable
    {
        public Fixture(string rawReportJson, params (string Name, string Json)[] contracts)
        {
            Root = Path.Combine(Path.GetTempPath(), "excise-1519-" + Guid.NewGuid().ToString("N"));
            ContractsDir = Path.Combine(Root, "contracts");
            Directory.CreateDirectory(ContractsDir);
            foreach (var (name, json) in contracts)
                File.WriteAllText(Path.Combine(ContractsDir, name), json);

            RawPath = Path.Combine(Root, "raw.json");
            File.WriteAllText(RawPath, rawReportJson);
            OutputPath = Path.Combine(Root, "quality.json");
        }

        public string Root { get; }
        public string ContractsDir { get; }
        public string RawPath { get; }
        public string OutputPath { get; }

        public bool Classify(bool strictContracts = true)
            => RenderProgram.RunRenderQualityClassify(RawPath, ContractsDir, OutputPath, strictContracts);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void CleanScan_MatchingEveryContract_Passes()
    {
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "PASS")),
            ("one.json", CleanContract));

        fixture.Classify().Should().BeTrue(
            "every scanned page matched its pin and every contract page was scanned");
    }

    [Fact]
    public void ExpectationDeparture_Fails()
    {
        // The rule the manifest note already claimed and the code did not have.
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "DIFF")),
            ("one.json", CleanContract));

        fixture.Classify().Should().BeFalse(
            "the page is pinned PASS and came back DIFF — that is the ratchet");
    }

    [Fact]
    public void ExpectationDeparture_IsNotGatedWithoutStrictContracts()
    {
        // --strict-contracts is what ARMS the verdict, so the ad-hoc triage
        // workflow (run the scan by hand to look at pages) still exits 0.
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "DIFF")),
            ("one.json", CleanContract));

        fixture.Classify(strictContracts: false).Should().BeTrue();
    }

    [Fact]
    public void ScannedPageWithNoContract_Fails()
    {
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "PASS")
                      + "," + Entry("synthetic/unpinned.pdf", 1, "PASS")),
            ("one.json", CleanContract));

        fixture.Classify().Should().BeFalse(
            "a scanned page nobody pinned cannot depart from anything, so it is "
            + "invisible to every other check");
    }

    [Fact]
    public void ContractPageNeverScanned_Fails()
    {
        // #1527's lesson: a gate whose input set collapses to near-zero read
        // green. Here the page that DID run matched, and that used to be the
        // whole verdict.
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "PASS")),
            ("one.json", CleanContract),
            ("two.json", CleanContract.Replace("synthetic/one.pdf", "synthetic/two.pdf")));

        fixture.Classify().Should().BeFalse("synthetic/two.pdf#p1 is pinned but was not scanned");
    }

    [Fact]
    public void ZeroPagesScanned_Fails()
    {
        using var fixture = new Fixture(RawReport(string.Empty), ("one.json", CleanContract));

        fixture.Classify().Should().BeFalse("a scan of nothing is not a passing scan");
    }

    [Fact]
    public void ExciseSideGap_FailsEvenWhenTheContractPinsIt()
    {
        // The one class that is unambiguously an excise defect (an oracle
        // rendered a page excise refused). A pin must not buy it off, because
        // a contract's QualityStatus OVERWRITES the inferred one — which is
        // why this reads the RAW status instead.
        var pinnedGap = CleanContract
            .Replace("\"ExpectedRawStatus\": \"PASS\"", "\"ExpectedRawStatus\": \"EXCISE_SIDE_GAP\"");

        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "EXCISE_SIDE_GAP")),
            ("one.json", pinnedGap));

        fixture.Classify().Should().BeFalse(
            "re-pinning an excise-side gap is the one escape CLAUDE.md forbids");
    }

    [Fact]
    public void PinnedQualityStatus_StillOverwritesTheInferredOne()
    {
        // Not a wish — a measurement of why the verdict cannot rest on
        // report.failures. A DIFF page infers qualityStatus FAIL, the pin
        // replaces it with PIXEL_EXACT, and `failures` comes back EMPTY. On
        // 2026-09-16 all 4,971 checked-in page contracts pinned a non-FAIL
        // QualityStatus and ReleaseStatus PASS, so gating on `failures` alone
        // would have been a second gate that cannot go red.
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "DIFF")),
            ("one.json", CleanContract));

        fixture.Classify().Should().BeFalse();

        var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture.OutputPath)).RootElement;
        report.GetProperty("failures").GetArrayLength().Should().Be(
            0, "the contract's pinned PIXEL_EXACT masked the inferred FAIL");
        report.GetProperty("summary").GetProperty("expectationFailurePages").GetInt32().Should().Be(
            1, "the expectation comparison is the term that survives the pin");
    }

    [Fact]
    public void Report_NamesTheOffendingPages()
    {
        // A 2h28m row that goes red with a bare count is a row people start
        // accepting. missingContractPages used to be a number and nothing else.
        using var fixture = new Fixture(
            RawReport(Entry("synthetic/one.pdf", 1, "DIFF")
                      + "," + Entry("synthetic/unpinned.pdf", 2, "PASS")),
            ("one.json", CleanContract));

        fixture.Classify().Should().BeFalse();

        var json = File.ReadAllText(fixture.OutputPath);
        var report = System.Text.Json.JsonDocument.Parse(json).RootElement;
        report.GetProperty("expectationFailures")[0].GetProperty("path")
            .GetString().Should().Be("synthetic/one.pdf");
        report.GetProperty("missingContracts")[0].GetProperty("pageNumber")
            .GetInt32().Should().Be(2);
    }

    [Fact]
    public void PdfjsPathPrefixFallback_CountsAsCoveredNotAsTwoFailures()
    {
        // FindPage accepts a bare pdf.js filename for a "pdfjs/"-prefixed
        // contract. The coverage check resolves through the same lookup, so
        // such a page must not read as BOTH "scanned with no contract" and
        // "contract page never scanned".
        using var fixture = new Fixture(
            RawReport(Entry("one.pdf", 1, "PASS")),
            ("one.json", CleanContract.Replace("synthetic/one.pdf", "pdfjs/one.pdf")));

        fixture.Classify().Should().BeTrue();
    }
}
