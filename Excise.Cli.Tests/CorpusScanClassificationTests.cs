using AwesomeAssertions;
using Excise.Core.Parsing;
using System.Diagnostics;
using Xunit;

using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

public class CorpusScanClassificationTests
{
    [Fact]
    public void RenderingQualityContractSet_LoadsPerPdfContractsAndExpandsRanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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
    public void ApplyRenderingQualityContracts_ClassifiesOracleRefusalWithoutClaimingVisualSuperiority()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfjs/encrypted-short-key.pdf",
                    pageNumber = 1,
                    status = "ALL_ORACLES_REFUSED",
                    comparedOracles = 0,
                    agreeingOracles = 0,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].qualityStatus.Should().Be("REFERENCE_REFUSAL_ACCEPTED");
            entries[0].referenceSituation.Should().Be("REFS_REFUSE");
            entries[0].pixelAgreement.Should().Be("NOT_COMPARABLE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_StrictMissingContractMarksNeedsReview()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(Path.GetTempPath(), "excise-raw-report-" + Guid.NewGuid().ToString("N") + ".json");
        var outputPath = Path.Combine(Path.GetTempPath(), "excise-quality-report-" + Guid.NewGuid().ToString("N") + ".json");
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
                  "QualityReason": "excise matches the reviewed image target.",
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
                      "expectedStatus": "DIFF",
                      "expectationResult": "FAIL",
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
            report.summary.expectationResultCounts.Should().ContainKey("PASS")
                .WhoseValue.Should().Be(1);
            report.entries.Should().ContainSingle()
                .Which.expectedRawStatus.Should().Be("PASS_ONE");
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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

            entries[0].releaseStatus.Should().Be("NEEDS_REVIEW");
            entries[0].qualityStatus.Should().Be("NEEDS_REVIEW");
            entries[0].passOneReviewStatus.Should().Be("UNREVIEWED_PASS_ONE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_FlagsOutlierPassOneAsUnreviewed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "outlier.json"), """
                {
                  "Path": "pdfjs/freeculture.pdf",
                  "ReviewStatus": "NEEDS_REVIEW",
                  "RootCause": "REFERENCE_ORACLE_DISAGREEMENT",
                  "Target": {
                    "Mode": "REFERENCE_RENDERER",
                    "Primary": "mutool",
                    "Reason": "Candidate target, pending visual/spec review."
                  },
                  "Pages": {
                    "74": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "MATCHES_ACCEPTED_REFERENCE",
                      "ReferenceSituation": "REFS_AGREE",
                      "QualityReason": "Candidate PASS_ONE target requires review because excise matches only a non-central renderer."
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
                    pageNumber = 74,
                    status = "PASS_ONE",
                    bestOracle = "mutool",
                    comparedOracles = 3,
                    agreeingOracles = 1,
                    oracleComparisonPairs = 3,
                    oracleDisagreeingPairs = 0,
                    exciseReferenceCenterRank = 3,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].releaseStatus.Should().Be("NEEDS_REVIEW");
            entries[0].qualityStatus.Should().Be("NEEDS_REVIEW");
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
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
                    "Reason": "Use the print-semantic target until excise implements the visible composite behavior."
                  },
                  "Pages": {
                    "7": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "FAIL",
                      "QualityStatus": "FAIL",
                      "PixelAgreement": "MATCHES_TARGET",
                      "ReferenceSituation": "REFS_DISAGREE",
                      "QualityReason": "excise still misses the reviewed composite print target."
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
            entries[0].pixelAgreement.Should().Be("MATCHES_TARGET",
                "raw PASS_ONE means excise matched at least one reference; semantic rejection belongs in qualityStatus/passOneReviewStatus, not MATCHES_NONE");
            entries[0].qualityStatus.Should().Be("FAIL");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenderingQualityContractSet_RejectsPassOneMatchesNoneContract()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), """
                {
                  "Path": "pdfjs/bad.pdf",
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "PASS_ONE",
                      "ReleaseStatus": "FAIL",
                      "QualityStatus": "FAIL",
                      "PixelAgreement": "MATCHES_NONE"
                    }
                  }
                }
                """);

            var load = () => RenderProgram.RenderingQualityContractSet.Load(dir);

            load.Should().Throw<InvalidDataException>()
                .WithMessage("*PixelAgreement MATCHES_NONE is incompatible with ExpectedRawStatus PASS_ONE*");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenderingQualityContractSet_RejectsFailQualityPassReleaseContract()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), """
                {
                  "Path": "pdfjs/bad.pdf",
                  "Pages": {
                    "1": {
                      "ExpectedRawStatus": "DIFF",
                      "ReleaseStatus": "PASS",
                      "QualityStatus": "FAIL",
                      "PixelAgreement": "MATCHES_NONE"
                    }
                  }
                }
                """);

            var load = () => RenderProgram.RenderingQualityContractSet.Load(dir);

            load.Should().Throw<InvalidDataException>()
                .WithMessage("*QualityStatus FAIL must not use ReleaseStatus PASS*");
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
            diagnostic = "excise=ContentStreamReadWarning { Code = IMAGE_ONLY_FILTER_IN_CONTENT_STREAM }",
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
            diagnostic = "excise=ordinary render warning",
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
        var root = Path.Combine(Path.GetTempPath(), "excise-corpus-discovery-" + Guid.NewGuid().ToString("N"));
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
        var root = Path.Combine(Path.GetTempPath(), "excise-corpus-filter-" + Guid.NewGuid().ToString("N"));
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
        var path = Path.Combine(Path.GetTempPath(), "excise-page-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
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
        var path = Path.Combine(Path.GetTempPath(), "excise-password-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
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
        var path = Path.Combine(Path.GetTempPath(), "excise-expectation-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            File.WriteAllText(path,
                "path\tpageNumber\texpectedStatus\texpectedErrorContains\tnote\tresultStatus\tresultCategory\tresultReason\n" +
                "pdfjs/semantic.pdf\t1\tPASS_ONE\t\taccepted by majority\tPASS\tPASS_ONE_SEMANTIC_OK\texcise matches semantic majority\n" +
                "pdfjs/legacy.pdf\t0\tMALFORMED_PDF\tbad xref\tlegacy note\n");

            var manifest = RenderProgram.LoadCorpusExpectationManifest(new FileInfo(path))!;

            var semantic = manifest[new RenderProgram.CorpusPageKey("pdfjs/semantic.pdf", 1)];
            semantic.ExpectedStatus.Should().Be("PASS_ONE");
            semantic.ExpectedResultStatus.Should().Be("PASS");
            semantic.ExpectedResultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
            semantic.ExpectedResultReason.Should().Be("excise matches semantic majority");

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
            "excise plus two primary references and three escalation references need room to return structured oracle statuses before the outer stuck-task guard fires");
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
                    "excise agrees with the renderable references"),
        };

        RenderProgram.ApplyCorpusExpectations(entries, expectations);
        var summary = RenderProgram.BuildCorpusScanSummary(entries);

        entries[0].status.Should().Be("PASS_ONE");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("PASS_ONE_REFERENCE_REFUSAL");
        entries[0].resultReason.Should().Be("excise agrees with the renderable references");
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

    // ---- #907: a refusal is judged by the oracles, not by excise ------------
    //
    // Every case below is a MUTATION of the evidence, not of the code: the same
    // excise failure is scored against oracles that refused, oracles that
    // rendered, and no oracles at all. A rule that cannot separate those three
    // is the rule that pinned bug_216 (nobody renders it) and bug_481363
    // (mutool renders it) as the same DECODE_ERROR.

    [Fact]
    public void ApplyRefusalCorroboration_NoOracleRendered_BecomesAgreedRefusal()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfium/bug_216.pdf",
            pageNumber = 1,
            status = "DECODE_ERROR",
            mutoolStatus = "EXIT_CODE",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("AGREED_REFUSAL");
        entry.refusedAs.Should().Be("DECODE_ERROR");
        entry.refusalCorroboration.Should().Be("corroborated");
        entry.diagnostic.Should().Contain("DECODE_ERROR");
    }

    [Fact]
    public void ApplyRefusalCorroboration_OracleRendered_BecomesExciseSideGap()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfium/bug_481363.pdf",
            pageNumber = 1,
            status = "DECODE_ERROR",
            mutoolStatus = "OK",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("EXCISE_SIDE_GAP");
        entry.refusedAs.Should().Be("DECODE_ERROR");
        entry.refusalCorroboration.Should().Be("contradicted");
    }

    [Fact]
    public void ApplyRefusalCorroboration_OracleRendersMalformedPdf_BecomesExciseSideGap()
    {
        // "This file is malformed" is excise certifying its own refusal. A
        // renderer that renders it disproves the certificate, so the
        // input-naming statuses are contradictable too.
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfium/bug_113.pdf",
            pageNumber = 0,
            status = "MALFORMED_PDF",
            mutoolStatus = "EXIT_CODE",
            cairoStatus = "OK",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("EXCISE_SIDE_GAP");
        entry.refusedAs.Should().Be("MALFORMED_PDF");
    }

    [Fact]
    public void ApplyRefusalCorroboration_CorroboratedMalformedPdf_KeepsItsStatus()
    {
        // MALFORMED_PDF already names the FIXTURE as the problem, which is what
        // the manifest header documents it as meaning. Rewriting the ~28 pages
        // that carry it would erase a distinction the ratchet is holding.
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfium/bug_113.pdf",
            pageNumber = 0,
            status = "MALFORMED_PDF",
            mutoolStatus = "EXIT_CODE",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("MALFORMED_PDF");
        entry.refusedAs.Should().BeNull();
        entry.refusalCorroboration.Should().Be("corroborated");
    }

    [Fact]
    public void ApplyRefusalCorroboration_CredentialBlocked_IsNotCorroboration()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfium/encrypted_hello_world_r6.pdf",
            pageNumber = 0,
            status = "PASSWORD_REQUIRED",
            mutoolStatus = "EXIT_CODE",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("PASSWORD_REQUIRED",
            "every renderer was locked out by the same missing password, which says nothing about the page");
        entry.refusalCorroboration.Should().Be("credential-blocked");
    }

    [Fact]
    public void ApplyRefusalCorroboration_Timeout_StaysLoadDependent()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfjs/slow.pdf",
            pageNumber = 1,
            status = "TIMEOUT",
            mutoolStatus = "EXIT_CODE",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("TIMEOUT",
            "TIMEOUT flips with CPU load, and a verdict that flips with CPU load false-reds the gate");
        entry.refusalCorroboration.Should().Be("load-dependent");
    }

    [Fact]
    public void ApplyRefusalCorroboration_NoOracleInvoked_FormsNoOpinion()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfjs/unprobed.pdf",
            pageNumber = 1,
            status = "DECODE_ERROR",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("DECODE_ERROR");
        entry.refusalCorroboration.Should().Be("unprobed");
    }

    [Fact]
    public void ApplyRefusalCorroboration_ExciseRendered_IsNotARefusal()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            path = "pdfjs/rendered.pdf",
            pageNumber = 1,
            status = "PASS_ONE",
            renderMs = 12,
            mutoolStatus = "OK",
            cairoStatus = "EXIT_CODE",
        };

        RenderProgram.ApplyRefusalCorroboration(entry);

        entry.status.Should().Be("PASS_ONE");
        entry.refusalCorroboration.Should().BeNull();
    }

    [Fact]
    public void ApplyRenderingQualityContracts_AgreedRefusal_IsAcceptedLikeAnyCorroboratedRefusal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfium/bug_216.pdf",
                    pageNumber = 1,
                    status = "AGREED_REFUSAL",
                    comparedOracles = 0,
                    agreeingOracles = 0,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].qualityStatus.Should().Be("REFERENCE_REFUSAL_ACCEPTED");
            entries[0].referenceSituation.Should().Be("REFS_REFUSE");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyRenderingQualityContracts_ExciseSideGap_IsNeverInferredAsAccepted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var set = RenderProgram.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new RenderProgram.CorpusScanEntry
                {
                    path = "pdfium/bug_481363.pdf",
                    pageNumber = 1,
                    status = "EXCISE_SIDE_GAP",
                    // What ApplyCorpusExpectations writes for a page PINNED in
                    // an expectation manifest — and bug_481363 is pinned, so
                    // this is the live combination, not a contrived one.
                    resultStatus = "PASS",
                    comparedOracles = 0,
                    agreeingOracles = 0,
                },
            };

            RenderProgram.ApplyRenderingQualityContracts(entries, set, strictContracts: false);

            entries[0].qualityStatus.Should().Be("FAIL",
                "an oracle rendered a page excise refused — the one class that is unambiguously an "
                + "excise defect. Pinning it makes resultStatus PASS, and the fallback would read "
                + "that as GOOD_ENOUGH");
            entries[0].improvementPriority.Should().Be("P1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- #932: ink locality is scored by majority, not by one oracle --------

    [Fact]
    public void CompareInkLocalityByMajority_MajorityInked_ReportsMissingTiles()
    {
        using var blank = MakeBitmap(inkedTiles: 0);
        using var a = MakeBitmap(inkedTiles: 8);
        using var b = MakeBitmap(inkedTiles: 8);
        using var c = MakeBitmap(inkedTiles: 8);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            blank, new[] { a, b, c });

        inked.Should().Be(8);
        missing.Should().Be(8, "all three oracles drew content excise left blank");
    }

    [Fact]
    public void CompareInkLocalityByMajority_LoneOutlierInked_ReportsNothingMissing()
    {
        // The annotation case (#932): pdftocairo synthesizes an appearance for a
        // /AP-less Line annotation, mutool and Ghostscript draw nothing, and
        // §12.5.5 permits both. Scoring against the MOST-INKED oracle elects the
        // outlier by construction and convicts excise for agreeing with the
        // majority.
        using var blank = MakeBitmap(inkedTiles: 0);
        using var outlier = MakeBitmap(inkedTiles: 8);
        using var agreeingA = MakeBitmap(inkedTiles: 0);
        using var agreeingB = MakeBitmap(inkedTiles: 0);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            blank, new[] { outlier, agreeingA, agreeingB });

        inked.Should().Be(0);
        missing.Should().Be(0);
    }

    [Fact]
    public void CompareInkLocalityByMajority_LoneBlankOracle_CannotAcquit()
    {
        // The other direction, and the reason "closest to excise" was rejected
        // in #883: adding a blank oracle must not weaken a verdict.
        using var blank = MakeBitmap(inkedTiles: 0);
        using var a = MakeBitmap(inkedTiles: 8);
        using var b = MakeBitmap(inkedTiles: 8);
        using var blankOracle = MakeBitmap(inkedTiles: 0);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            blank, new[] { a, b, blankOracle });

        inked.Should().Be(8);
        missing.Should().Be(8);
    }

    [Fact]
    public void CompareInkLocalityByMajority_SingleOracle_KeepsPreMajorityStrictness()
    {
        using var blank = MakeBitmap(inkedTiles: 0);
        using var only = MakeBitmap(inkedTiles: 8);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            blank, new[] { only });

        inked.Should().Be(8);
        missing.Should().Be(8);
    }

    [Fact]
    public void CompareInkLocalityByMajority_DifferentOracleResolutions_StillComparable()
    {
        using var blank = MakeBitmap(inkedTiles: 0, size: 320);
        using var a = MakeBitmap(inkedTiles: 8, size: 320);
        using var b = MakeBitmap(inkedTiles: 8, size: 640);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            blank, new[] { a, b });

        inked.Should().Be(8);
        missing.Should().Be(8);
    }

    [Fact]
    public void CompareInkLocalityByMajority_SmallPageSubPixelOffset_ReportsNothingMissing()
    {
        // bug1844576.pdf renders 181x54. On a fixed 32-square grid that is 1.7px
        // per tile row, so a one-pixel vertical difference between renderers
        // moves a whole text row into another tile and the oracles stop agreeing
        // tile-by-tile about a page they agree about completely. Under a
        // majority rule that reads as "almost nothing is majority-inked", which
        // makes the all-tiles-missing verdict trivial to satisfy by accident.
        using var mine = MakeBandBitmap(width: 181, height: 54, bandTop: 20);
        using var a = MakeBandBitmap(width: 181, height: 54, bandTop: 22);
        using var b = MakeBandBitmap(width: 181, height: 54, bandTop: 22);
        using var c = MakeBandBitmap(width: 181, height: 54, bandTop: 23);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            mine, new[] { a, b, c });

        inked.Should().BeGreaterThan(0, "the oracles all drew the band");
        missing.Should().Be(0, "excise drew the same band one pixel higher");
    }

    [Fact]
    public void CompareInkLocalityByMajority_OracleRenderedADifferentPageBox_DoesNotVote()
    {
        // bug1844576.pdf has a 181x54 pt /CropBox inside a 612x792 /MediaBox.
        // excise, mutool and pdfium render the CropBox; pdftocairo renders the
        // MediaBox, so its raster holds the whole drawing in one corner and
        // every relative tile means something different. Given a vote, that
        // oracle disagrees with the others everywhere and collapses the
        // majority-inked set to almost nothing — which makes the
        // all-tiles-missing verdict trivially true on a page excise renders
        // correctly.
        using var mine = MakeBitmap(inkedTiles: 8, size: 320);
        using var sameBox = MakeBitmap(inkedTiles: 8, size: 320);
        using var alsoSameBox = MakeBitmap(inkedTiles: 8, size: 320);
        using var differentBox = MakeWideBitmap(width: 320, height: 80);

        var (missing, _, inked) = RenderProgram.CompareInkLocalityByMajority(
            mine, new[] { sameBox, alsoSameBox, differentBox });

        inked.Should().Be(8, "the two comparable oracles agree with excise");
        missing.Should().Be(0);
    }

    [Fact]
    public void CompareInkLocalityByMajority_MostOraclesRenderedADifferentPageBox_HasNoOpinion()
    {
        using var mine = MakeBitmap(inkedTiles: 0, size: 320);
        using var differentBoxA = MakeWideBitmap(width: 320, height: 80);
        using var differentBoxB = MakeWideBitmap(width: 320, height: 80);
        using var sameBox = MakeBitmap(inkedTiles: 8, size: 320);

        var (missing, extra, inked) = RenderProgram.CompareInkLocalityByMajority(
            mine, new[] { differentBoxA, differentBoxB, sameBox });

        (missing, extra, inked).Should().Be((0, 0, 0),
            "when excise is the geometric odd one out the disagreement is about the page box, "
            + "not about content, and this check must not pretend otherwise");
    }

    [Fact]
    public void ApplyInkLocalityVerdict_AllMajorityInkedTilesBlank_FlipsPassToMissingContent()
    {
        var entry = new RenderProgram.CorpusScanEntry { status = "PASS" };

        RenderProgram.ApplyInkLocalityVerdict(entry, (8, 0, 8), oracleCount: 3, comparableOracleCount: 3);

        entry.status.Should().Be("MISSING_CONTENT");
        entry.missingInkTiles.Should().Be(8);
        entry.referenceInkedTiles.Should().Be(8);
        entry.comparableOracles.Should().Be(3);
        entry.diagnostic.Should().Contain("majority");
    }

    [Fact]
    public void ApplyInkLocalityVerdict_PartialLoss_StaysPassAndRecordsCounts()
    {
        var entry = new RenderProgram.CorpusScanEntry { status = "PASS" };

        RenderProgram.ApplyInkLocalityVerdict(entry, (5, 0, 8), oracleCount: 3, comparableOracleCount: 3);

        entry.status.Should().Be("PASS");
        entry.missingInkTiles.Should().Be(5);
    }

    [Fact]
    public void ApplyInkLocalityVerdict_TooFewInkedTiles_StaysPass()
    {
        var entry = new RenderProgram.CorpusScanEntry { status = "PASS" };

        RenderProgram.ApplyInkLocalityVerdict(entry, (2, 0, 2), oracleCount: 3, comparableOracleCount: 3);

        entry.status.Should().Be("PASS");
    }

    // ---- #976: a starved comparable pool escalates instead of going quiet ---

    [Fact]
    public void ShouldEscalateOracles_PrimariesAgreeButPoolTooSmall_Escalates()
    {
        // bug1844576.pdf: a 181x54 pt /CropBox inside a 612x792 /MediaBox.
        // pdftocairo renders the MediaBox and is excluded from the locality
        // vote, leaving mutool and pdfium. A 1-1 split between two oracles is
        // not a majority, so the MISSING_CONTENT check returns no verdict at
        // all — on a page that could be genuinely blank.
        RenderProgram.ShouldEscalateOracles(
            primariesAgree: true,
            comparableLocalityOracles: 2,
            alwaysRunAllOracles: false)
            .Should().BeTrue(
                "two comparable oracles cannot form a majority, so the blank-page check has no "
                + "verdict to give — more oracles is the only thing that changes that");
    }

    [Fact]
    public void ShouldEscalateOracles_PrimariesAgreeAndPoolIsWhole_DoesNotEscalate()
    {
        RenderProgram.ShouldEscalateOracles(
            primariesAgree: true,
            comparableLocalityOracles: 3,
            alwaysRunAllOracles: false)
            .Should().BeFalse(
                "three agreeing primaries that all rendered the same page box need neither a "
                + "59ms subprocess nor an 80ms JVM launch to settle anything");
    }

    [Fact]
    public void ShouldEscalateOracles_PrimariesDisagree_EscalatesRegardlessOfPool()
    {
        RenderProgram.ShouldEscalateOracles(
            primariesAgree: false,
            comparableLocalityOracles: 3,
            alwaysRunAllOracles: false)
            .Should().BeTrue("the page-wide disagreement rule predates #976 and still holds");
    }

    [Fact]
    public void CountComparableLocalityOracles_CountsOnlyTheOraclesThatShareThePageBox()
    {
        using var mine = MakeBitmap(inkedTiles: 8, size: 320);
        using var sameBox = MakeBitmap(inkedTiles: 8, size: 320);
        using var alsoSameBox = MakeBitmap(inkedTiles: 8, size: 320);
        using var differentBox = MakeWideBitmap(width: 320, height: 80);

        RenderProgram.CountComparableLocalityOracles(
            mine,
            new SkiaSharp.SKBitmap?[] { sameBox, alsoSameBox, differentBox, null })
            .Should().Be(2,
                "an oracle that rasterized a different page box addresses different tiles, and an "
                + "oracle that refused has no tiles at all — neither is in the pool that votes");
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    [InlineData(3, 5)]
    public void OracleMajorityAgrees_ThreeAgreeingPrimariesSurviveEscalation(int agreeing, int compared)
    {
        // #976 escalates on pages where all three primaries AGREE. If adding
        // Ghostscript and PDFBox could cost those pages their PASS, the fix
        // would be manufacturing failures instead of verdicts.
        RenderProgram.OracleMajorityAgrees(agreeing, compared).Should().BeTrue(
            "escalation exists to decide who is right, not to change who wins");
    }

    [Fact]
    public void ApplyInkLocalityVerdict_StarvedPool_RecordsHowSmallItWas()
    {
        // The 1-1 split reports (0, 0, 0) — no missing tiles, no inked
        // reference tiles — which is byte-identical to a clean page. The
        // comparable count is the only thing that distinguishes "checked and
        // fine" from "could not check" (#976).
        var entry = new RenderProgram.CorpusScanEntry { status = "PASS" };

        RenderProgram.ApplyInkLocalityVerdict(entry, (0, 0, 0), oracleCount: 3, comparableOracleCount: 2);

        entry.status.Should().Be("PASS");
        entry.comparableOracles.Should().Be(2);
        entry.comparableOracles.Should().BeLessThan(
            RenderProgram.MinComparableOraclesForLocalityMajority,
            "a scan reading this report must be able to count the pages whose blank-page check "
            + "never reached a verdict");
    }

    [Fact]
    public void BuildCorpusScanSummary_CountsPagesWhoseLocalityCheckCouldNotDecide()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry { path = "a.pdf", status = "PASS", comparableOracles = 3 },
            new RenderProgram.CorpusScanEntry { path = "b.pdf", status = "PASS", comparableOracles = 2 },
            new RenderProgram.CorpusScanEntry { path = "c.pdf", status = "PASS", comparableOracles = 0 },
            new RenderProgram.CorpusScanEntry { path = "d.pdf", status = "ALL_ORACLES_REFUSED" },
        };

        var summary = RenderProgram.BuildCorpusScanSummary(entries);

        summary.localityQuorumShortCount.Should().Be(2,
            "the two pages with a sub-majority pool are counted; the page where no locality "
            + "comparison ran at all is not, because it has no pool to be short of");
    }

    [Fact]
    public void DetermineLocalityShortReason_ThreeOraclesOk_IsGeometryMismatch()
    {
        // #989: three (or more) of the five oracles rendered OK, yet
        // comparableOracles is still under quorum — the only way that
        // happens is that some of those OK renders addressed a different
        // page box than excise's (commonly /MediaBox vs /CropBox), so the
        // shortfall is geometry, not a rendering failure of any kind.
        var entry = new RenderProgram.CorpusScanEntry
        {
            mutoolStatus = "OK", cairoStatus = "OK", ghostscriptStatus = "OK",
            pdfboxStatus = "OK", pdfiumStatus = "OK",
        };

        RenderProgram.DetermineLocalityShortReason(entry, comparableOracleCount: 2)
            .Should().Be("geometry-mismatch");
    }

    [Fact]
    public void DetermineLocalityShortReason_TimeoutWithTooFewOks_IsOracleTimeout()
    {
        // issue19517.pdf's measured shape: three primaries TIMEOUT at the
        // default --pdf-timeout-ms ceiling, leaving only two OK — a real
        // verdict may exist (mutool alone finishes in ~32s unconstrained),
        // the scan just didn't wait for it.
        var entry = new RenderProgram.CorpusScanEntry
        {
            mutoolStatus = "TIMEOUT", cairoStatus = "TIMEOUT", ghostscriptStatus = "TIMEOUT",
            pdfboxStatus = "OK", pdfiumStatus = "OK",
        };

        RenderProgram.DetermineLocalityShortReason(entry, comparableOracleCount: 2)
            .Should().Be("oracle-timeout");
    }

    [Fact]
    public void DetermineLocalityShortReason_FewerThanThreeAttempted_IsTooFewAttempted()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            mutoolStatus = "OK", cairoStatus = null, ghostscriptStatus = null,
            pdfboxStatus = null, pdfiumStatus = null,
        };

        RenderProgram.DetermineLocalityShortReason(entry, comparableOracleCount: 1)
            .Should().Be("too-few-oracles-attempted");
    }

    [Fact]
    public void DetermineLocalityShortReason_ThreeAttemptedNoTimeoutNotEnoughOk_IsOracleRefusal()
    {
        // Brotli-Prototype-FileA.pdf's measured shape: mutool and
        // ghostscript OK, but cairo/pdfbox/pdfium each fail a different,
        // non-timeout way (EXIT_CODE / MISSING_OUTPUT / PAGE_OUT_OF_RANGE) —
        // three oracles were attempted, none timed out, but only two
        // succeeded, so the shortfall is oracle-side refusal, not geometry.
        var entry = new RenderProgram.CorpusScanEntry
        {
            mutoolStatus = "OK", cairoStatus = "EXIT_CODE", ghostscriptStatus = "OK",
            pdfboxStatus = "MISSING_OUTPUT", pdfiumStatus = "PAGE_OUT_OF_RANGE",
        };

        RenderProgram.DetermineLocalityShortReason(entry, comparableOracleCount: 2)
            .Should().Be("oracle-refusal");
    }

    [Fact]
    public void ApplyInkLocalityVerdict_QuorumMet_LeavesLocalityShortReasonNull()
    {
        // The reason field only means something when the pool was actually
        // short; recording a reason for a page whose locality check reached
        // a real verdict would misrepresent a fine page as attributable.
        var entry = new RenderProgram.CorpusScanEntry
        {
            status = "PASS",
            mutoolStatus = "OK", cairoStatus = "OK", ghostscriptStatus = "OK",
        };

        RenderProgram.ApplyInkLocalityVerdict(entry, (0, 0, 0), oracleCount: 3, comparableOracleCount: 3);

        entry.localityShortReason.Should().BeNull(
            "quorum was met, so there is no shortfall to attribute a reason to");
    }

    [Fact]
    public void ApplyInkLocalityVerdict_QuorumShort_SetsLocalityShortReason()
    {
        var entry = new RenderProgram.CorpusScanEntry
        {
            status = "PASS",
            mutoolStatus = "OK", cairoStatus = "OK", ghostscriptStatus = "OK",
            pdfboxStatus = "OK", pdfiumStatus = "OK",
        };

        RenderProgram.ApplyInkLocalityVerdict(entry, (0, 0, 0), oracleCount: 5, comparableOracleCount: 2);

        entry.localityShortReason.Should().Be("geometry-mismatch",
            "five OK renders but a comparable pool of two can only be explained by page-box geometry");
    }

    [Fact]
    public void BuildCorpusScanSummary_BreaksDownLocalityShortfallByReason()
    {
        var entries = new[]
        {
            new RenderProgram.CorpusScanEntry
            {
                path = "geometry.pdf", status = "PASS", comparableOracles = 2,
                localityShortReason = "geometry-mismatch",
            },
            new RenderProgram.CorpusScanEntry
            {
                path = "timeout.pdf", status = "PASS_ONE", comparableOracles = 2,
                localityShortReason = "oracle-timeout",
            },
            new RenderProgram.CorpusScanEntry
            {
                // Quorum met: not part of the short population, and its
                // (null) reason must not pollute the breakdown.
                path = "fine.pdf", status = "PASS", comparableOracles = 3,
            },
        };

        var summary = RenderProgram.BuildCorpusScanSummary(entries);

        summary.localityShortReasonCounts.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["geometry-mismatch"] = 1, ["oracle-timeout"] = 1 },
            "only the two sub-majority pages are attributed, one to each measured cause, and the "
            + "quorum-met page contributes nothing even though it never sets localityShortReason");
    }

    /// <summary>
    /// A square bitmap whose first <paramref name="inkedTiles"/> tiles of the
    /// scanner's 32x32 grid are solid black and the rest white.
    /// </summary>
    private static SkiaSharp.SKBitmap MakeBitmap(int inkedTiles, int size = 320)
    {
        const int grid = 32;
        var bitmap = new SkiaSharp.SKBitmap(size, size);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            var tile = size / (float)grid;
            for (var i = 0; i < inkedTiles; i++)
            {
                var x = i % grid;
                var y = i / grid;
                canvas.DrawRect(x * tile, y * tile, tile, tile, paint);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// A page of a different shape entirely, ink in one corner — what a
    /// renderer produces when it rasterizes the /MediaBox where the others
    /// rasterized the /CropBox.
    /// </summary>
    private static SkiaSharp.SKBitmap MakeWideBitmap(int width, int height)
    {
        var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            canvas.DrawRect(0, 0, width / 4f, height / 4f, paint);
        }

        return bitmap;
    }

    /// <summary>
    /// A small page carrying one horizontal band of ink, positioned to the
    /// pixel — the shape of a one-line form fixture.
    /// </summary>
    private static SkiaSharp.SKBitmap MakeBandBitmap(int width, int height, int bandTop)
    {
        var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            canvas.DrawRect(0, bandTop, width, 1, paint);
        }

        return bitmap;
    }

    // ---- #977: the two descriptions of a page must not drift apart ---------

    /// <summary>
    /// THE gate. Every contract page that also has a corpus-expectation
    /// manifest row must pin the same raw status.
    ///
    /// These are two independent, checked-in descriptions of what a page does —
    /// <c>render-quality-scan</c> grades against the contract, the corpus scan
    /// grades against the TSV — and before this nothing compared them, so a
    /// page could be green in one and years stale in the other. Three of the
    /// annotation pages #932 re-pinned had contracts stuck at PASS_ONE while
    /// the manifest said MISSING_CONTENT, and had been that way since the
    /// contracts were generated.
    ///
    /// It needs no corpus and no renderer: both inputs are versioned test
    /// metadata, so this runs everywhere, including a corpus-less CI runner.
    /// </summary>
    [Fact]
    public void Contracts_AgreeWithTheCorpusExpectationManifests()
    {
        var root = FindRepoRoot();
        var contractsDir = Path.Combine(root, "test-pdfs", "rendering-contracts");
        Directory.Exists(contractsDir).Should().BeTrue("rendering quality contracts are versioned test metadata");

        var comparison = RenderProgram.CompareContractsWithExpectationManifests(contractsDir, root);

        // Not "> 0": that only catches a TOTAL collapse of the corpus->manifest
        // map. If ONE key drifts — a corpus directory renamed, a manifest moved
        // — several hundred to a few thousand comparisons vanish silently and
        // the gate stays green while covering a fraction of what it claims,
        // which is #958's failure mode. Both a floor and a per-corpus presence
        // check, because the floor alone would survive losing isartor's 205.
        comparison.ComparedPages.Should().BeGreaterThanOrEqualTo(3500,
            "3577 contract pages had a manifest row when this gate was written (2026-08-16); a "
            + "sharp drop means the corpus->manifest map stopped matching the corpus directory "
            + "names contracts use, not that the drift was fixed");

        foreach (var corpus in RenderProgram.CorpusExpectationManifests.Keys)
        {
            var manifest = Path.Combine(root, RenderProgram.CorpusExpectationManifests[corpus]);
            if (!File.Exists(manifest) ||
                !Directory.Exists(Path.Combine(contractsDir, corpus)))
            {
                continue;
            }

            comparison.ComparedPagesByCorpus.GetValueOrDefault(corpus).Should().BeGreaterThan(0,
                $"contracts and a manifest both exist for {corpus}, so the comparison must be "
                + "reaching it — zero means the two are no longer being keyed the same way");
        }

        var report = string.Join(Environment.NewLine, comparison.Disagreements.Select(d => d.ToString()));
        comparison.Disagreements.Should().BeEmpty(
            "a contract and a manifest row for the same page are two claims about the same thing; "
            + "fix whichever is stale rather than letting them describe different behaviour:"
            + Environment.NewLine + report);
    }

    [Fact]
    public void CompareContractsWithExpectationManifests_ReportsAStaleStatus()
    {
        var (contractsDir, repoRoot) = MakeContractAndManifest(
            contractStatus: "PASS_ONE",
            manifestStatus: "PASS");
        try
        {
            var comparison = RenderProgram.CompareContractsWithExpectationManifests(contractsDir, repoRoot);

            comparison.ComparedPages.Should().Be(1);
            comparison.Disagreements.Should().ContainSingle();
            comparison.Disagreements[0].ContractStatus.Should().Be("PASS_ONE");
            comparison.Disagreements[0].ManifestStatus.Should().Be("PASS");
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void CompareContractsWithExpectationManifests_ManifestWildcardIsCompatibleWithAnything()
    {
        // issue19517.pdf's manifest row is a hand-written '*' because
        // reference-renderer timeouts make its status load-dependent. A
        // consistency check that read '*' as a literal status would report it
        // as a disagreement forever, and the standing pressure would be to
        // "fix" it by overwriting the wildcard — which regenerating the
        // manifest has already destroyed twice.
        var (contractsDir, repoRoot) = MakeContractAndManifest(
            contractStatus: "PASS_ONE",
            manifestStatus: "*");
        try
        {
            RenderProgram.CompareContractsWithExpectationManifests(contractsDir, repoRoot)
                .Disagreements.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void CompareContractsWithExpectationManifests_ContractsWithNoManifestRowAreCountedNotFailed()
    {
        // Most contracts (all-pages contracts, and whole corpora like federal
        // and ghent that no corpus scan grades) have nothing to compare
        // against. That is a coverage number, not a failure.
        var (contractsDir, repoRoot) = MakeContractAndManifest(
            contractStatus: "PASS",
            manifestStatus: "PASS",
            contractPage: 7);
        try
        {
            var comparison = RenderProgram.CompareContractsWithExpectationManifests(contractsDir, repoRoot);

            comparison.ComparedPages.Should().Be(0);
            comparison.PagesWithoutManifestRow.Should().Be(1);
            comparison.Disagreements.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>
    /// A throwaway repo root holding one pdfjs contract and one pdfjs
    /// expectation manifest row for page 1.
    /// </summary>
    private static (string ContractsDir, string RepoRoot) MakeContractAndManifest(
        string contractStatus,
        string manifestStatus,
        int contractPage = 1)
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "excise-contract-agreement-" + Guid.NewGuid().ToString("N"));
        var contractsDir = Path.Combine(repoRoot, "test-pdfs", "rendering-contracts", "pdfjs");
        Directory.CreateDirectory(contractsDir);
        Directory.CreateDirectory(Path.Combine(repoRoot, "tests"));

        File.WriteAllText(
            Path.Combine(contractsDir, "sample.json"),
            $$"""
            {
              "Path": "pdfjs/sample.pdf",
              "Pages": {
                "{{contractPage}}": { "ExpectedRawStatus": "{{contractStatus}}" }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(repoRoot, "tests", "corpus-expectations.tsv"),
            "# comment line\nsample.pdf\t1\t" + manifestStatus + "\n");

        return (Path.Combine(repoRoot, "test-pdfs", "rendering-contracts"), repoRoot);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "test-pdfs")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
