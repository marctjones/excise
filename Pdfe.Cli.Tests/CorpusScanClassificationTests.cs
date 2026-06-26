using AwesomeAssertions;
using Pdfe.Core.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

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

            var set = Program.RenderingQualityContractSet.Load(dir);

            set.Contracts.Should().HaveCount(1);
            set.CreatePageManifest()["pdfjs/issue.pdf"].Should().BeEquivalentTo(new[] { 1, 2 });
            set.CreatePasswordManifest()!["pdfjs/issue.pdf"].Should().Be("secret");
            set.CreateExpectationManifest()
                .Should().ContainKey(new Program.CorpusPageKey("pdfjs/issue.pdf", 1));
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
            var set = Program.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new Program.CorpusScanEntry
                {
                    path = "pdfjs/issue19326.pdf",
                    pageNumber = 1,
                    status = "DIFF",
                    bestOracle = "mutool",
                    comparedOracles = 4,
                    oracleDisagreeingPairs = 2,
                },
            };

            Program.ApplyRenderingQualityContracts(entries, set, strictContracts: true);

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
            var set = Program.RenderingQualityContractSet.Load(dir);
            var entries = new[]
            {
                new Program.CorpusScanEntry
                {
                    path = "pdfjs/uncontracted.pdf",
                    pageNumber = 1,
                    status = "PASS",
                },
            };

            Program.ApplyRenderingQualityContracts(entries, set, strictContracts: true);

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
    public void RenderingQualityContractSet_LoadsRepositoryContracts()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, "test-pdfs", "rendering-contracts");
        Directory.Exists(dir).Should().BeTrue("rendering quality contracts are versioned test metadata");

        var set = Program.RenderingQualityContractSet.Load(dir);

        set.Contracts.Should().NotBeEmpty();
        set.CreateExpectationManifest()
            .Should().ContainKey(new Program.CorpusPageKey("pdfjs/issue19326.pdf", 1));
        var issue19326 = set.FindPage("pdfjs/issue19326.pdf", 1);
        issue19326.Should().NotBeNull();
        issue19326!.Page.QualityStatus.Should().Be("ACCEPTED_LIMITATION");
        issue19326.Page.Target!.Mode.Should().Be("PDF_SPEC");
    }

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
    public void TryApplyRecoveredMalformedContentShortCircuit_ClassifiesWithoutOracleWork()
    {
        var entry = new Program.CorpusScanEntry
        {
            path = "pdfjs/bomb_giant.pdf",
            pageNumber = 1,
            diagnostic = "pdfe=ContentStreamReadWarning { Code = IMAGE_ONLY_FILTER_IN_CONTENT_STREAM }",
            renderMs = 1,
        };

        Program.TryApplyRecoveredMalformedContentShortCircuit(entry, Stopwatch.StartNew())
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
        var entry = new Program.CorpusScanEntry
        {
            path = "pdfjs/normal.pdf",
            pageNumber = 1,
            diagnostic = "pdfe=ordinary render warning",
        };

        Program.TryApplyRecoveredMalformedContentShortCircuit(entry, Stopwatch.StartNew())
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
    public void TryGetCorpusPassword_MatchesPdfjsPrefixedManifestAgainstBarePdfjsCorpusPath()
    {
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pdfjs/bug1782186.pdf"] = "Hello",
        };

        Program.TryGetCorpusPassword(passwords, "bug1782186.pdf", out var password)
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

        Program.TryGetCorpusPassword(passwords, "pdfjs/issue3371.pdf", out var password)
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

            var manifest = Program.LoadCorpusExpectationManifest(new FileInfo(path))!;

            var semantic = manifest[new Program.CorpusPageKey("pdfjs/semantic.pdf", 1)];
            semantic.ExpectedStatus.Should().Be("PASS_ONE");
            semantic.ExpectedResultStatus.Should().Be("PASS");
            semantic.ExpectedResultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
            semantic.ExpectedResultReason.Should().Be("pdfe matches semantic majority");

            var legacy = manifest[new Program.CorpusPageKey("pdfjs/legacy.pdf", 0)];
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

    [Fact]
    public void CorpusScan_ExpectedRefusals_RefusesForDocumentedReasons()
    {
        var root = FindRepoRoot();
        var corpus = Path.Combine(root, "test-pdfs");
        var manifestPath = Path.Combine(
            corpus,
            "manifests",
            "rendering-expected-refusals-2026-06-24.tsv");

        if (!Directory.Exists(corpus) || !File.Exists(manifestPath))
            return;

        var expectations = LoadExpectedRefusals(manifestPath);
        expectations.Should().NotBeEmpty();

        var pageManifest = expectations.ToDictionary(
            expectation => expectation.Path,
            expectation => (IReadOnlySet<int>)new HashSet<int> { expectation.PageNumber },
            StringComparer.Ordinal);
        var expectationManifest = Program.LoadCorpusExpectationManifest(new FileInfo(manifestPath));
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "pdfe-expected-refusals-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            Program.RunCorpusScan(
                    corpus,
                    outputPath,
                    chunkIndex: 0,
                    chunkTotal: 1,
                    dpi: 72,
                    maxDiffFraction: 0.05,
                    maxMae: 5,
                    parallel: 1,
                    pdfTimeoutMs: 30_000,
                    pageMode: Program.CorpusPageMode.First,
                    extraOracles: Program.CorpusExtraOracles.None,
                    pageManifest: pageManifest,
                    expectationManifest: expectationManifest)
                .Should().BeTrue();

            using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
            var entries = document.RootElement.GetProperty("entries")
                .EnumerateArray()
                .Select(entry => new
                {
                    Path = entry.GetProperty("path").GetString()!,
                    PageNumber = entry.GetProperty("pageNumber").GetInt32(),
                    Status = entry.GetProperty("status").GetString()!,
                    ResultStatus = entry.GetProperty("resultStatus").GetString()!,
                    ExpectedStatus = entry.TryGetProperty("expectedStatus", out var expectedStatus)
                        ? expectedStatus.GetString()
                        : null,
                    ExpectationResult = entry.TryGetProperty("expectationResult", out var expectationResult)
                        ? expectationResult.GetString()
                        : null,
                    ErrorMessage = entry.TryGetProperty("errorMessage", out var error)
                        ? error.GetString()
                        : null,
                })
                .ToDictionary(entry => (entry.Path, entry.PageNumber));

            foreach (var expectation in expectations)
            {
                entries.Should().ContainKey((expectation.Path, expectation.PageNumber));
                var entry = entries[(expectation.Path, expectation.PageNumber)];
                entry.Status.Should().Be(expectation.ExpectedStatus, expectation.Note);
                entry.ResultStatus.Should().Be("PASS", expectation.Note);
                entry.ExpectedStatus.Should().Be(expectation.ExpectedStatus, expectation.Note);
                entry.ExpectationResult.Should().Be("PASS", expectation.Note);
                if (!string.IsNullOrWhiteSpace(expectation.ExpectedErrorContains))
                {
                    entry.ErrorMessage.Should().Contain(
                        expectation.ExpectedErrorContains,
                        expectation.Note);
                }
            }
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public void ApplyCorpusExpectations_MatchingExpectedFailureKeepsRawStatusAndPassesResult()
    {
        var entries = new[]
        {
            new Program.CorpusScanEntry
            {
                path = "pdfjs/bad.pdf",
                pageNumber = 0,
                status = "MALFORMED_PDF",
                errorMessage = "Document has no Pages dictionary",
            },
            new Program.CorpusScanEntry
            {
                path = "pdfjs/renderable.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<Program.CorpusPageKey, Program.CorpusExpectedOutcome>
        {
            [new Program.CorpusPageKey("pdfjs/bad.pdf", 0)] =
                new("MALFORMED_PDF", "no Pages dictionary", "accepted malformed fixture"),
        };

        Program.ApplyCorpusExpectations(entries, expectations);
        var summary = Program.BuildCorpusScanSummary(entries);

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
            new Program.CorpusScanEntry
            {
                path = "pdfjs/reference-refusal.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<Program.CorpusPageKey, Program.CorpusExpectedOutcome>
        {
            [new Program.CorpusPageKey("pdfjs/reference-refusal.pdf", 1)] =
                new(
                    "PASS_ONE",
                    "",
                    "one reference refused",
                    "PASS",
                    "PASS_ONE_REFERENCE_REFUSAL",
                    "pdfe agrees with the renderable references"),
        };

        Program.ApplyCorpusExpectations(entries, expectations);
        var summary = Program.BuildCorpusScanSummary(entries);

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
            new Program.CorpusScanEntry
            {
                path = "pdfjs/font-policy.pdf",
                pageNumber = 1,
                status = "DIFF",
            },
        };
        var expectations = new Dictionary<Program.CorpusPageKey, Program.CorpusExpectedOutcome>
        {
            [new Program.CorpusPageKey("pdfjs/font-policy.pdf", 1)] =
                new(
                    "*",
                    "",
                    "accepted by semantic review",
                    "PASS",
                    "PASS_ONE_SEMANTIC_OK",
                    "raw oracle class may vary by oracle set"),
        };

        Program.ApplyCorpusExpectations(entries, expectations);

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
            new Program.CorpusScanEntry
            {
                path = "bug920426.pdf",
                pageNumber = 1,
                status = "PASS_ONE",
            },
        };
        var expectations = new Dictionary<Program.CorpusPageKey, Program.CorpusExpectedOutcome>
        {
            [new Program.CorpusPageKey("pdfjs/bug920426.pdf", 1)] =
                new(
                    "PASS_ONE",
                    "",
                    "semantic pass",
                    "PASS",
                    "PASS_ONE_SEMANTIC_OK",
                    "bare default pdf.js corpus path should still match"),
        };

        Program.ApplyCorpusExpectations(entries, expectations);

        entries[0].expectationResult.Should().Be("PASS");
        entries[0].resultStatus.Should().Be("PASS");
        entries[0].resultCategory.Should().Be("PASS_ONE_SEMANTIC_OK");
    }

    private static IReadOnlyList<ExpectedRefusal> LoadExpectedRefusals(string manifestPath)
    {
        return File.ReadLines(manifestPath)
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var parts = line.Split('\t');
                parts.Length.Should().BeGreaterThanOrEqualTo(5);
                return new ExpectedRefusal(
                    parts[0],
                    int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    parts[2],
                    parts[3],
                    parts[4]);
            })
            .ToArray();
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

    private sealed record ExpectedRefusal(
        string Path,
        int PageNumber,
        string ExpectedStatus,
        string ExpectedErrorContains,
        string Note);
}
