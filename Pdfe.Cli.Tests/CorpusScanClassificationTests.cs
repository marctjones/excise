using AwesomeAssertions;
using Pdfe.Core.Parsing;
using System.Diagnostics;
using Xunit;

using RenderProgram = Pdfe.RenderTools.Program;

namespace Pdfe.Cli.Tests;

public class CorpusScanClassificationTests
{
    [Fact]
    public void RenderingQualityContractSet_LoadsPerPdfContractsAndExpandsRanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var contractPath = Path.Combine(dir, "issue.json");
            File.WriteAllText(contractPath, """
                {
                  "Path": "pdfjs/issue.pdf",
                  "Password": "secret",
                  "RootCause": "FONT_TEXT",
                  "Target": {
                    "Mode": "REFERENCE_RENDERER",
                    "Primary": "mutool"
                  },
                  "Pages": {
                    "1-2": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "TARGET_MATCH"
                    }
                  }
                }
                """);

            var set = RenderProgram.RenderingQualityContractSet.Load(dir);

            set.Contracts.Should().HaveCount(1);
            set.CreatePageManifest()["pdfjs/issue.pdf"].Should().BeEquivalentTo(new[] { 1, 2 });
            set.CreatePasswordManifest()!["pdfjs/issue.pdf"].Should().Be("secret");
            set.CreateExpectationManifest()
                .Should().ContainKey(new RenderProgram.CorpusPageKey("pdfjs/issue.pdf", 1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenderingQualityContractSet_PageManifestKeepsFullContractCoverage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "long.json"), """
                {
                  "Path": "isartor/long.pdf",
                  "Pages": {
                    "1-10000": {
                      "ExpectedRawStatus": "PASS"
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(dir, "focused.json"), """
                {
                  "Path": "pdfjs/focused.pdf",
                  "Pages": {
                    "129": {
                      "ExpectedRawStatus": "PASS_ONE"
                    }
                  }
                }
                """);

            var set = RenderProgram.RenderingQualityContractSet.Load(dir);

            set.CreatePageManifest()["isartor/long.pdf"]
                .Should().HaveCount(10_000);
            set.CreatePageManifest()["isartor/long.pdf"]
                .Should().Contain(new[] { 1, 2, 5, 20, 10_000 });
            set.CreatePageManifest()["pdfjs/focused.pdf"]
                .Should().BeEquivalentTo(new[] { 129 });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_AnnotatesQualityColumns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "issue.json"), """
                {
                  "Path": "pdfjs/issue19326.pdf",
                  "Issue": 403,
                  "RootCause": "JPX_ALPHA_LIMITATION",
                  "Target": {
                    "Mode": "PDF_SPEC",
                    "Primary": "mutool"
                  },
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "DIFF",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "ACCEPTED_LIMITATION",
                      "PixelAgreement": "MATCHES_NONE",
                      "ReferenceSituation": "REFS_DISAGREE",
                      "ImprovementPriority": "P2",
                      "Confidence": "HIGH",
                      "QualityReason": "Visible content is present; JPX alpha fidelity remains tracked."
                    }
                  }
                }
                """);
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfjs/issue19326.pdf",
                    pageNumber = 1,
                    status = "DIFF",
                    bestOracle = "mutool",
                    comparedOracles = 4,
                    oracleDisagreeingPairs = 2,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: true);

            entries[0].contractStatus.Should().Be("APPLIED");
            entries[0].releaseStatus.Should().Be("PASS");
            entries[0].qualityStatus.Should().Be("ACCEPTED_LIMITATION");
            entries[0].pixelAgreement.Should().Be("MATCHES_NONE");
            entries[0].referenceSituation.Should().Be("REFS_DISAGREE");
            entries[0].targetBasis.Should().Be("PDF_SPEC");
            entries[0].targetRenderer.Should().Be("mutool");
            entries[0].rootCause.Should().Be("JPX_ALPHA_LIMITATION");
            entries[0].trackedBy.Should().Be("#403");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_StrictMissingContractMarksNeedsReview()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "other.json"), """
                {
                  "Path": "pdfjs/other.pdf",
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "PASS"
                    }
                  }
                }
                """);
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfjs/uncontracted.pdf",
                    pageNumber = 1,
                    status = "PASS",
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: true);

            entries[0].contractStatus.Should().Be("MISSING");
            entries[0].releaseStatus.Should().Be("NEEDS_REVIEW");
            entries[0].qualityStatus.Should().Be("NEEDS_REVIEW");
            entries[0].qualityReason.Should().Contain("No rendering quality contract");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunRenderQualityClassify_AppliesContractsToExistingRawReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(Path.GetTempPath(), "pdfe-raw-report-" + Guid.NewGuid().ToString("N") + ".json");
        var outputPath = Path.Combine(Path.GetTempPath(), "pdfe-quality-report-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(Path.Combine(dir, "issue.json"), """
                {
                  "Path": "pdfjs/issue.pdf",
                  "RootCause": "IMAGE_ORACLE_DISAGREEMENT",
                  "ReviewStatus": "REVIEWED",
                  "Target": {
                    "Mode": "REFERENCE_RENDERER",
                    "Primary": "mutool",
                    "Reason": "Human-reviewed image fixture target."
                  },
                  "QualityReason": "pdfe matches the reviewed image target.",
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "MATCHES_ACCEPTED_REFERENCE",
                      "PixelAgreement": "MATCHES_SOME",
                      "ReferenceSituation": "REFS_DISAGREE",
                      "Confidence": "HIGH"
                    }
                  }
                }
                """);
            File.WriteAllText(rawPath, """
                {
                  "generatedUtc": "2026-06-26T00:00:00Z",
                  "corpus": "test-pdfs",
                  "counts": { "PASS_ONE": 1 },
                  "entries": [
                    {
                      "path": "pdfjs/issue.pdf",
                      "pageNumber": 1,
                      "status": "PASS_ONE",
                      "bestOracle": "mutool",
                      "comparedOracles": 4,
                      "agreeingOracles": 2,
                      "oracleDisagreeingPairs": 1
                    }
                  ]
                }
                """);

            RenderProgram.RunRenderQualityClassify(rawPath, dir, outputPath, strictContracts: true)
                .Should().BeTrue();

            using var stream = File.OpenRead(outputPath);
            var report = System.Text.Json.JsonSerializer.Deserialize<RenderProgram.RenderingQualityReport>(stream);
            report.Should().NotBeNull();
            report!.summary.missingContractPages.Should().Be(0);
            report.summary.qualityStatusCounts.Should().ContainKey("MATCHES_ACCEPTED_REFERENCE")
                .WhoseValue.Should().Be(1);
            report.summary.passOneReviewStatusCounts.Should().ContainKey("ACCEPTED_PASS_ONE")
                .WhoseValue.Should().Be(1);
            report.unreviewedPassOne.Should().BeEmpty();
            report.passOneTriage.Should().ContainSingle()
                .Which.passOneReviewStatus.Should().Be("ACCEPTED_PASS_ONE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (File.Exists(rawPath)) File.Delete(rawPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_FlagsGeneratedPassOneAsUnreviewed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "baseline.json"), """
                {
                  "Path": "pdfjs/freeculture.pdf",
                  "RootCause": "REFERENCE_ORACLE_DISAGREEMENT",
                  "Target": {
                    "Mode": "REFERENCE_CONSENSUS",
                    "Primary": "mutool",
                    "Reason": "Baseline full-corpus contract inferred from raw all-pages scan."
                  },
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "MATCHES_ACCEPTED_REFERENCE",
                      "ReferenceSituation": "REFS_DISAGREE",
                      "QualityReason": "Baseline classification inferred from full all-pages raw corpus scan; promote to a reviewed contract when triaged."
                    }
                  }
                }
                """);
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfjs/freeculture.pdf",
                    pageNumber = 1,
                    status = "PASS_ONE",
                    bestOracle = "mutool",
                    comparedOracles = 4,
                    agreeingOracles = 1,
                    oracleDisagreeingPairs = 3,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].releaseStatus.Should().Be("PASS");
            entries[0].qualityStatus.Should().Be("MATCHES_ACCEPTED_REFERENCE");
            entries[0].passOneReviewStatus.Should().Be("UNREVIEWED_PASS_ONE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_FlagsFailingPassOneAsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "altona.json"), """
                {
                  "Path": "altona/composite.pdf",
                  "RootCause": "ALTONA_COMPOSITE_COLOR_TRANSPARENCY",
                  "Target": {
                    "Mode": "PDF_SPEC",
                    "Primary": "pdftocairo",
                    "Reason": "Use the print-semantic target until pdfe implements the visible composite behavior."
                  },
                  "Pages": {
                    "7": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "FAIL",
                      "ReferenceSituation": "REFS_DISAGREE",
                      "QualityReason": "pdfe still misses the reviewed composite print target."
                    }
                  }
                }
                """);
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "altona/composite.pdf",
                    pageNumber = 7,
                    status = "PASS_ONE",
                    bestOracle = "pdftocairo",
                    comparedOracles = 4,
                    agreeingOracles = 1,
                    oracleDisagreeingPairs = 3,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].passOneReviewStatus.Should().Be("REJECTED_PASS_ONE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenderingQualityContractSet_LoadsRepositoryContracts()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, "test-pdfs", "rendering-contracts");
        Directory.Exists(dir).Should().BeTrue("rendering quality contracts are versioned test metadata");

        var set = RenderProgram.RenderingQualityContractSet.Load(dir);

        set.Contracts.Should().NotBeEmpty();
        set.CreateExpectationManifest()
            .Should().ContainKey(new RenderProgram.CorpusPageKey("pdfjs/issue19326.pdf", 1));
        var issue19326 = set.FindPage("pdfjs/issue19326.pdf", 1);
        issue19326.Should().NotBeNull();
        issue19326!.Page.QualityStatus.Should().Be("MATCHES_ACCEPTED_REFERENCE");
        issue19326.Page.RootCause.Should().Be("JPX_CDEF_OPACITY_ORACLE_DISAGREEMENT");
        issue19326.Page.Target!.Mode.Should().Be("PDF_SPEC");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhase_ReturnsParseError()
    {
        RenderProgram.ClassifyCorpusFailure(
                new InvalidDataException("bad xref"),
                RenderProgram.CorpusFailurePhase.Open)
            .Should().Be("MALFORMED_PDF");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhaseCompression_ReturnsUnsupportedCompression()
    {
        RenderProgram.ClassifyCorpusFailure(
                new InvalidDataException("unsupported deflate compression method"),
                RenderProgram.CorpusFailurePhase.Open)
            .Should().Be("UNSUPPORTED_COMPRESSION");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhasePasswordRequired_ReturnsPasswordRequired()
    {
        RenderProgram.ClassifyCorpusFailure(
                new PdfEncryptionNotSupportedException("Password verification failed. The file requires a non-empty user password."),
                RenderProgram.CorpusFailurePhase.Open)
            .Should().Be("PASSWORD_REQUIRED");
    }

    [Fact]
    public void ClassifyCorpusFailure_OpenPhaseUnsupportedEncryption_ReturnsUnsupportedEncrypted()
    {
        RenderProgram.ClassifyCorpusFailure(
                new PdfEncryptionNotSupportedException("Encryption algorithm V=99 is not supported."),
                RenderProgram.CorpusFailurePhase.Open)
            .Should().Be("UNSUPPORTED_ENCRYPTED");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderDecodeFailure_ReturnsDecodeError()
    {
        RenderProgram.ClassifyCorpusFailure(
                new PdfParseException("Invalid hex digit in ASCIIHexDecode"),
                RenderProgram.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderFilterFailure_ReturnsDecodeError()
    {
        RenderProgram.ClassifyCorpusFailure(
                new NotSupportedException("Unknown filter: BogusDecode"),
                RenderProgram.CorpusFailurePhase.Render)
            .Should().Be("DECODE_ERROR");
    }

    [Fact]
    public void ClassifyCorpusFailure_RenderNonDecodeFailure_ReturnsRenderError()
    {
        RenderProgram.ClassifyCorpusFailure(
                new InvalidOperationException("renderer state failed"),
                RenderProgram.CorpusFailurePhase.Render)
            .Should().Be("RENDER_ERROR");
    }

    [Fact]
    public void BuildOracleDiagnostic_IncludesBothOracleStatuses()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            mutoolStatus = "TIMEOUT",
            mutoolError = "mutool exceeded 15000ms",
            cairoStatus = "EXIT_CODE",
            cairoError = "pdftocairo exited 1",
            ghostscriptStatus = "OK",
            pdfboxStatus = "TOOL_UNAVAILABLE",
            pdfiumStatus = "TOOL_UNAVAILABLE",
        };

        RenderProgram.BuildOracleDiagnostic(entry)
            .Should().Be("mutool=TIMEOUT (mutool exceeded 15000ms); pdftocairo=EXIT_CODE (pdftocairo exited 1); ghostscript=OK; pdfbox=TOOL_UNAVAILABLE; pdfium=TOOL_UNAVAILABLE");
    }

    [Fact]
    public void TryApplyRecoveredMalformedContentShortCircuit_ClassifiesWithoutOracleWork()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfjs/bomb_giant.pdf",
            pageNumber = 1,
            diagnostic = "pdfe=ContentStreamReadWarning { Code = IMAGE_ONLY_FILTER_IN_CONTENT_STREAM }",
            renderMs = 1,
        };

        RenderProgram.TryApplyRecoveredMalformedContentShortCircuit(entry, Stopwatch.StartNew())
            .Should().BeTrue();

        entry.status.Should().Be("RECOVERED_MALFORMED_CONTENT");
        entry.errorPhase.Should().Be("render");
        entry.errorType.Should().Be("RecoveredMalformedContent");
        entry.diagnostic.Should().Contain("Skipped reference oracles");
        entry.mutoolStatus.Should().BeNull();
        entry.cairoStatus.Should().BeNull();
    }

    [Fact]
    public void TryApplyRecoveredMalformedContentShortCircuit_IgnoresOrdinaryDiagnostics()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfjs/normal.pdf",
            pageNumber = 1,
            diagnostic = "pdfe=ordinary render warning",
        };

        RenderProgram.TryApplyRecoveredMalformedContentShortCircuit(entry, Stopwatch.StartNew())
            .Should().BeFalse();

        entry.status.Should().Be("UNKNOWN");
        entry.errorPhase.Should().BeNull();
        entry.errorType.Should().BeNull();
    }

    [Fact]
    public void BuildCorpusScanSummary_AggregatesVisualAndOracleSignals()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "pass.pdf",
                pageNumber = 1,
                status = "PASS",
                oracleComparisonPairs = 1,
                oracleDisagreeingPairs = 0,
            },
            new RenderProgram.CorpusScanEntry
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
            new RenderProgram.CorpusScanEntry
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
            new RenderProgram.CorpusScanEntry
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

        var summary = RenderProgram.BuildCorpusScanSummary(entries);

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
        RenderProgram.TryParseCorpusExtraOracles("ghostscript,pdfbox,pdfium", out var value, out var error)
            .Should().BeTrue(error);

        value.Should().Be(
            RenderProgram.CorpusExtraOracles.Ghostscript
            | RenderProgram.CorpusExtraOracles.PdfBox
            | RenderProgram.CorpusExtraOracles.Pdfium);
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryParseCorpusExtraOracles_RejectsUnknownValue()
    {
        RenderProgram.TryParseCorpusExtraOracles("ghostscript,bogus", out _, out var error)
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

            var all = RenderProgram.DiscoverCorpusPdfs(root, chunkIndex: 0, chunkTotal: 1);

            all.Select(p => p.RelativePath).Should().Equal(
                "a/nested/deep.pdf",
                "b/middle.pdf",
                "top.pdf");

            var chunk = RenderProgram.DiscoverCorpusPdfs(root, chunkIndex: 1, chunkTotal: 2);
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

            var filtered = RenderProgram.DiscoverCorpusPdfs(
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

            var manifest = RenderProgram.LoadCorpusPageManifest(new FileInfo(path))!;

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

            var manifest = RenderProgram.LoadCorpusPasswordManifest(new FileInfo(path))!;

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
    public void RenderingContracts_DocumentKnownCorpusPasswords()
    {
        var repoRoot = FindRepoRoot();
        var contractsDir = Path.Combine(repoRoot, "test-pdfs", "rendering-contracts");

        var set = RenderProgram.RenderingQualityContractSet.Load(contractsDir);
        var passwords = set.CreatePasswordManifest();

        passwords.Should().NotBeNull();
        passwords!.Should().Contain(new KeyValuePair<string, string>("pdfjs/bug1782186.pdf", "Hello"));
        passwords.Should().Contain(new KeyValuePair<string, string>("pdfjs/issue15893_reduced.pdf", "test"));
        passwords.Should().Contain(new KeyValuePair<string, string>("pdfjs/issue3371.pdf", "ELXRTQWS"));
        passwords.Should().Contain(new KeyValuePair<string, string>("poppler/unittestcases/Gday garçon - open.pdf", "garçon"));
        passwords.Should().Contain(new KeyValuePair<string, string>("poppler/unittestcases/PasswordEncrypted.pdf", "password"));
        passwords.Should().Contain(new KeyValuePair<string, string>("poppler/unittestcases/PasswordEncryptedReconstructed.pdf", "test"));
        passwords.Should().Contain(new KeyValuePair<string, string>("poppler/unittestcases/encrypted-256.pdf", "user-secret"));
    }

    [Fact]
    public void TryGetCorpusPassword_MatchesPdfjsPrefixedManifestAgainstBarePdfjsCorpusPath()
    {
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pdfjs/bug1782186.pdf"] = "Hello",
        };

        RenderProgram.TryGetCorpusPassword(passwords, "bug1782186.pdf", out var password)
            .Should().BeTrue();
        password.Should().Be("Hello");
    }

    [Fact]
    public void TryGetCorpusPassword_MatchesBareManifestAgainstPdfjsPrefixedPath()
    {
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issue3371.pdf"] = "ELXRTQWS",
        };

        RenderProgram.TryGetCorpusPassword(passwords, "pdfjs/issue3371.pdf", out var password)
            .Should().BeTrue();
        password.Should().Be("ELXRTQWS");
    }

    [Fact]
    public void LoadCorpusExpectationManifest_ReadsOptionalResultMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfe-expectation-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            File.WriteAllText(path,
                "path\tpageNumber\texpectedStatus\texpectedErrorContains\tnote\tresultStatus\tresultCategory\tresultReason\n" +
                "pdfjs/semantic.pdf\t1\tPASS_ONE\t\taccepted by majority\tPASS\tPASS_ONE_SEMANTIC_OK\tpdfe matches semantic majority\n" +
                "pdfjs/legacy.pdf\t0\tMALFORMED_PDF\tbad xref\tlegacy note\n");

            var manifest = RenderProgram.LoadCorpusExpectationManifest(new FileInfo(path))!;

            var semantic = manifest[new RenderProgram.CorpusPageKey("pdfjs/semantic.pdf", 1)];
            semantic.ExpectedStatus.Should().Be("PASS_ONE");
            semantic.ExpectedResultStatus.Should().Be("PASS");
            semantic.ExpectedResultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
            semantic.ExpectedResultReason.Should().Be("pdfe matches semantic majority");

            var legacy = manifest[new RenderProgram.CorpusPageKey("pdfjs/legacy.pdf", 0)];
            legacy.ExpectedStatus.Should().Be("MALFORMED_PDF");
            legacy.ExpectedResultStatus.Should().BeEmpty();
            legacy.ExpectedResultCategory.Should().BeEmpty();
            legacy.ExpectedResultReason.Should().BeEmpty();
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
        RenderProgram.SelectCorpusPages(10, RenderProgram.CorpusPageMode.All, new HashSet<int> { 5, 2, 99 })
            .Should().Equal(2, 5);
    }

    [Fact]
    public void SelectCorpusPages_WithOnlyOpenFailureSentinel_RendersAllPagesInAllPageMode()
    {
        RenderProgram.SelectCorpusPages(10, RenderProgram.CorpusPageMode.All, new HashSet<int> { 0 })
            .Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
    }

    [Fact]
    public void SelectCorpusPages_WithOnlyOpenFailureSentinel_RendersFirstPageInFocusedModes()
    {
        RenderProgram.SelectCorpusPages(10, RenderProgram.CorpusPageMode.First, new HashSet<int> { 0 })
            .Should().Equal(1);
    }

    [Fact]
    public void ComputeCorpusScanWallBudget_AllOracles_AllowsEveryOracleTimeout()
    {
        var budget = RenderProgram.ComputeCorpusScanWallBudgetMs(
            oracleTimeoutMs: 30_000,
            RenderProgram.CorpusPageMode.First,
            RenderProgram.CorpusExtraOracles.All);

        budget.Should().BeGreaterThanOrEqualTo(7 * 30_000,
            "pdfe plus two primary references and three escalation references need room to return structured oracle statuses before the outer stuck-task guard fires");
    }

    [Fact]
    public void ComputeCorpusScanWallBudget_ManifestPages_ScalesWithSelectedPages()
    {
        var budget = RenderProgram.ComputeCorpusScanWallBudgetMs(
            oracleTimeoutMs: 15_000,
            RenderProgram.CorpusPageMode.First,
            RenderProgram.CorpusExtraOracles.Ghostscript,
            new HashSet<int> { 1, 7, 25 });

        budget.Should().BeGreaterThanOrEqualTo(14 * 15_000);
    }

    [Fact]
    public void ApplyCorpusExpectations_MatchingExpectedFailureKeepsRawStatusAndPassesResult()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "pdfjs/bad.pdf",
                pageNumber = 0,
                status = "MALFORMED_PDF",
                errorMessage = "Document has no Pages dictionary",
            },
            new RenderProgram.CorpusScanEntry
            {
                path = "pdfjs/renderable.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<RenderProgram.CorpusPageKey, RenderProgram.CorpusExpectedOutcome>
        {
            [new RenderProgram.CorpusPageKey("pdfjs/bad.pdf", 0)] =
                new("MALFORMED_PDF", "no Pages dictionary", "accepted malformed fixture"),
        };

        RenderProgram.ApplyCorpusExpectations(entries, expectations);
        var summary = RenderProgram.BuildCorpusScanSummary(entries);

        entries[0].status.Should().Be("MALFORMED_PDF");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("ACCEPTED_DEGENERATE_INPUT");
        entries[0].resultReason.Should().Be("accepted malformed fixture");
        entries[0].expectationResult.Should().Be("PASS");
        entries[1].status.Should().Be("PASS_ONE");
        entries[1].resultStatus.Should().Be("PASS");
        summary.statusCounts.Should().ContainKey("MALFORMED_PDF").WhoseValue.Should().Be(1);
        summary.resultStatusCounts.Should().ContainKey("PASS").WhoseValue.Should().Be(2);
        summary.resultCategoryCounts.Should().ContainKey("ACCEPTED_DEGENERATE_INPUT").WhoseValue.Should().Be(1);
        summary.resultNonPassCount.Should().Be(0);
        summary.expectedPassCount.Should().Be(1);
    }

    [Fact]
    public void ApplyCorpusExpectations_UsesExplicitSemanticResultMetadata()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "pdfjs/reference-refusal.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<RenderProgram.CorpusPageKey, RenderProgram.CorpusExpectedOutcome>
        {
            [new RenderProgram.CorpusPageKey("pdfjs/reference-refusal.pdf", 1)] =
                new(
                    "PASS_ONE",
                    "",
                    "one reference refused",
                    "PASS",
                    "PASS_ONE_REFERENCE_REFUSAL",
                    "pdfe agrees with the renderable references"),
        };

        RenderProgram.ApplyCorpusExpectations(entries, expectations);
        var summary = RenderProgram.BuildCorpusScanSummary(entries);

        entries[0].status.Should().Be("PASS_ONE");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("PASS_ONE_REFERENCE_REFUSAL");
        entries[0].resultReason.Should().Be("pdfe agrees with the renderable references");
        summary.resultCategoryCounts.Should().ContainKey("PASS_ONE_REFERENCE_REFUSAL").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void ApplyCorpusExpectations_AllowsWildcardRawStatusForSemanticAcceptance()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "pdfjs/font-policy.pdf",
                pageNumber = 1,
                status = "DIFF",
            },
        };
        var expectations = new Dictionary<RenderProgram.CorpusPageKey, RenderProgram.CorpusExpectedOutcome>
        {
            [new RenderProgram.CorpusPageKey("pdfjs/font-policy.pdf", 1)] =
                new(
                    "*",
                    "",
                    "accepted by semantic review",
                    "PASS",
                    "PASS_ONE_SEMANTIC_OK",
                    "raw oracle class may vary by oracle set"),
        };

        RenderProgram.ApplyCorpusExpectations(entries, expectations);

        entries[0].status.Should().Be("DIFF");
        entries[0].expectedStatus.Should().Be("*");
        entries[0].expectationResult.Should().Be("PASS");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
    }

    [Fact]
    public void ApplyCorpusExpectations_MatchesPdfjsPrefixedManifestAgainstBarePdfjsCorpusPath()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "bug920426.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<RenderProgram.CorpusPageKey, RenderProgram.CorpusExpectedOutcome>
        {
            [new RenderProgram.CorpusPageKey("pdfjs/bug920426.pdf", 1)] =
                new(
                    "PASS_ONE",
                    "",
                    "semantic pass",
                    "PASS",
                    "PASS_ONE_SEMANTIC_OK",
                    "bare default pdf.js corpus path should still match"),
        };

        RenderProgram.ApplyCorpusExpectations(entries, expectations);

        entries[0].expectationResult.Should().Be("PASS");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "test-pdfs")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
