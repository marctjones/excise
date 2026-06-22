using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Xunit;

namespace Pdfe.Cli.Tests;

public class CorpusScanClassificationTests
{
    [Fact]
    public void ClassifyCorpusFailure_OpenPhase_ReturnsParseError()
    {
        Program.ClassifyCorpusFailure(
                new InvalidDataException("bad xref"),
                Program.CorpusFailurePhase.Open)
            .Should().Be("MALFORMED_PDF");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhaseCompression_ReturnsUnsupportedCompression()
    {
        Program.ClassifyCorpusFailure(
                new InvalidDataException("unsupported deflate compression method"),
                Program.CorpusFailurePhase.Open)
            .Should().Be("UNSUPPORTED_COMPRESSION");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhasePasswordRequired_ReturnsPasswordRequired()
    {
        Program.ClassifyCorpusFailure(
                new PdfEncryptionNotSupportedException("Password verification failed. The file requires a non-empty user password."),
                Program.CorpusFailurePhase.Open)
            .Should().Be("PASSWORD_REQUIRED");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhaseUnsupportedEncryption_ReturnsUnsupportedEncrypted()
    {
        Program.ClassifyCorpusFailure(
                new PdfEncryptionNotSupportedException("Encryption algorithm V=99 is not supported."),
                Program.CorpusFailurePhase.Open)
            .Should().Be("UNSUPPORTED_ENCRYPTED");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderDecodeFailure_ReturnsDecodeError()
    {
        Program.ClassifyCorpusFailure(
                new PdfParseException("Invalid hex digit in ASCIIHexDecode"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderFilterFailure_ReturnsDecodeError()
    {
        Program.ClassifyCorpusFailure(
                new NotSupportedException("Unknown filter: BogusDecode"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderNonDecodeFailure_ReturnsRenderError()
    {
        Program.ClassifyCorpusFailure(
                new InvalidOperationException("renderer state failed"),
                Program.CorpusFailurePhase.Render)
            .Should().Be("RENDER_ERROR");
    }

    [Fact]
    public void BuildOracleDiagnostic_IncludesBothOracleStatuses()
    {
        var entry = new Program.CorpusScanEntry
        {
            mutoolStatus = "TIMEOUT",
            mutoolError = "mutool exceeded 15000ms",
            cairoStatus = "EXIT_CODE",
            cairoError = "pdftocairo exited 1",
            ghostscriptStatus = "OK",
            pdfboxStatus = "TOOL_UNAVAILABLE",
            pdfiumStatus = "TOOL_UNAVAILABLE",
        };

        Program.BuildOracleDiagnostic(entry)
            .Should().Be("mutool=TIMEOUT (mutool exceeded 15000ms); pdftocairo=EXIT_CODE (pdftocairo exited 1); ghostscript=OK; pdfbox=TOOL_UNAVAILABLE; pdfium=TOOL_UNAVAILABLE");
    }

    [Fact]
    public void BuildCorpusScanSummary_AggregatesVisualAndOracleSignals()
    {
        var entries = new[]
        {
            new Program.CorpusScanEntry
            {
                path = "pass.pdf",
                pageNumber = 1,
                status = "PASS",
                oracleComparisonPairs = 1,
                oracleDisagreeingPairs = 0,
            },
            new Program.CorpusScanEntry
            {
                path = "low-color.pdf",
                pageNumber = 1,
                status = "DIFF",
                visualHumanImpact = "low",
                visualCategory = "color-tone-or-texture",
                bestOracle = "pdftocairo",
                diffFraction = 0.12,
                mae = 4.2,
                oracleComparisonPairs = 6,
                oracleDisagreeingPairs = 0,
                oracleMeanMae = 0.4,
            },
            new Program.CorpusScanEntry
            {
                path = "high-missing.pdf",
                pageNumber = 2,
                status = "DIFF",
                visualHumanImpact = "high",
                visualCategory = "localized-content-or-geometry",
                bestOracle = "mutool",
                diffFraction = 0.4,
                mae = 70,
                oracleComparisonPairs = 6,
                oracleDisagreeingPairs = 6,
                oracleMeanMae = 34,
            },
            new Program.CorpusScanEntry
            {
                path = "partial.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
                visualHumanImpact = "medium",
                visualCategory = "mixed",
                oracleComparisonPairs = 6,
                oracleDisagreeingPairs = 4,
            },
        };

        var summary = Program.BuildCorpusScanSummary(entries);

        summary.statusCounts.Should().ContainKey("PASS").WhoseValue.Should().Be(1);
        summary.statusCounts.Should().ContainKey("DIFF").WhoseValue.Should().Be(2);
        summary.nonPassCount.Should().Be(3);
        summary.trueDiffCount.Should().Be(2);
        summary.passOneCount.Should().Be(1);
        summary.nonPassVisualHumanImpactCounts.Should().ContainKey("high").WhoseValue.Should().Be(1);
        summary.nonPassVisualHumanImpactCounts.Should().ContainKey("medium").WhoseValue.Should().Be(1);
        summary.nonPassVisualHumanImpactCounts.Should().ContainKey("low").WhoseValue.Should().Be(1);
        summary.nonPassVisualCategoryCounts.Should().ContainKey("color-tone-or-texture").WhoseValue.Should().Be(1);
        summary.oracleDisagreementBuckets.Should().ContainKey("none").WhoseValue.Should().Be(2);
        summary.oracleDisagreementBuckets.Should().ContainKey("some").WhoseValue.Should().Be(1);
        summary.oracleDisagreementBuckets.Should().ContainKey("all").WhoseValue.Should().Be(1);
        summary.topNonPass.Select(entry => entry.path)
            .Should().Equal("high-missing.pdf", "partial.pdf", "low-color.pdf");
        summary.topNonPass[0].oracleDisagreementBucket.Should().Be("all");
    }

    [Fact]
    public void TryParseCorpusExtraOracles_AllowsCommaSeparatedValues()
    {
        Program.TryParseCorpusExtraOracles("ghostscript,pdfbox,pdfium", out var value, out var error)
            .Should().BeTrue(error);

        value.Should().Be(
            Program.CorpusExtraOracles.Ghostscript
            | Program.CorpusExtraOracles.PdfBox
            | Program.CorpusExtraOracles.Pdfium);
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryParseCorpusExtraOracles_RejectsUnknownValue()
    {
        Program.TryParseCorpusExtraOracles("ghostscript,bogus", out _, out var error)
            .Should().BeFalse();

        error.Should().Contain("Bad --extra-oracles");
    }

    [Fact]
    public void DiscoverCorpusPdfs_RecursesAndKeepsStableRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdfe-corpus-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "b"));
            Directory.CreateDirectory(Path.Combine(root, "a", "nested"));
            File.WriteAllText(Path.Combine(root, "top.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(root, "b", "middle.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(root, "a", "nested", "deep.pdf"), "%PDF");

            var all = Program.DiscoverCorpusPdfs(root, chunkIndex: 0, chunkTotal: 1);

            all.Select(p => p.RelativePath).Should().Equal(
                "a/nested/deep.pdf",
                "b/middle.pdf",
                "top.pdf");

            var chunk = Program.DiscoverCorpusPdfs(root, chunkIndex: 1, chunkTotal: 2);
            chunk.Select(p => p.RelativePath).Should().Equal("b/middle.pdf");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoverCorpusPdfs_WithIncludeSet_FiltersBeforeChunking()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdfe-corpus-filter-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "a", "one.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(root, "b", "two.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(root, "three.pdf"), "%PDF");

            var filtered = Program.DiscoverCorpusPdfs(
                root,
                chunkIndex: 0,
                chunkTotal: 1,
                includeRelativePaths: new[] { "b/two.pdf", "missing.pdf" });

            filtered.Select(p => p.RelativePath).Should().Equal("b/two.pdf");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadCorpusPageManifest_ReadsPathPageTsv()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfe-page-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            File.WriteAllText(path,
                "path\tpageNumber\tstatus\n" +
                "pdfjs/a.pdf\t3\tDIFF\n" +
                "pdfjs/a.pdf\t1\tPASS_ONE\n" +
                "pdfjs/b.pdf\t0\tMALFORMED_PDF\n");

            var manifest = Program.LoadCorpusPageManifest(new FileInfo(path))!;

            manifest.Keys.Should().Equal("pdfjs/a.pdf", "pdfjs/b.pdf");
            manifest["pdfjs/a.pdf"].Should().BeEquivalentTo(new[] { 1, 3 });
            manifest["pdfjs/b.pdf"].Should().BeEquivalentTo(new[] { 0 });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadCorpusPasswordManifest_ReadsPathPasswordTsv()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfe-password-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            File.WriteAllText(path,
                "path\tuserPassword\tnote\n" +
                "pdfjs/a.pdf\tHello\tascii\n" +
                "poppler/Gday.pdf\tgarçon\tpdfdoc\n");

            var manifest = Program.LoadCorpusPasswordManifest(new FileInfo(path))!;

            manifest.Should().ContainKey("pdfjs/a.pdf").WhoseValue.Should().Be("Hello");
            manifest.Should().ContainKey("poppler/Gday.pdf").WhoseValue.Should().Be("garçon");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SelectCorpusPages_WithManifest_UsesExactPages()
    {
        Program.SelectCorpusPages(10, Program.CorpusPageMode.All, new HashSet<int> { 5, 2, 99 })
            .Should().Equal(2, 5);
    }

    [Fact]
    public void SelectCorpusPages_WithOnlyOpenFailureSentinel_RendersAllPagesInAllPageMode()
    {
        Program.SelectCorpusPages(10, Program.CorpusPageMode.All, new HashSet<int> { 0 })
            .Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
    }

    [Fact]
    public void SelectCorpusPages_WithOnlyOpenFailureSentinel_RendersFirstPageInFocusedModes()
    {
        Program.SelectCorpusPages(10, Program.CorpusPageMode.First, new HashSet<int> { 0 })
            .Should().Equal(1);
    }

    [Fact]
    public void ComputeCorpusScanWallBudget_AllOracles_AllowsEveryOracleTimeout()
    {
        var budget = Program.ComputeCorpusScanWallBudgetMs(
            oracleTimeoutMs: 30_000,
            Program.CorpusPageMode.First,
            Program.CorpusExtraOracles.All);

        budget.Should().BeGreaterThanOrEqualTo(7 * 30_000,
            "pdfe plus two primary references and three escalation references need room to return structured oracle statuses before the outer stuck-task guard fires");
    }

    [Fact]
    public void ComputeCorpusScanWallBudget_ManifestPages_ScalesWithSelectedPages()
    {
        var budget = Program.ComputeCorpusScanWallBudgetMs(
            oracleTimeoutMs: 15_000,
            Program.CorpusPageMode.First,
            Program.CorpusExtraOracles.Ghostscript,
            new HashSet<int> { 1, 7, 25 });

        budget.Should().BeGreaterThanOrEqualTo(14 * 15_000);
    }
}
