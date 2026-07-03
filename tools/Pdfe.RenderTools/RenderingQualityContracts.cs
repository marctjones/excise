using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdfe.RenderTools;

partial class Program
{
    private const string RenderingQualityUnknown = "UNKNOWN";
    private const string PassOneReviewNotApplicable = "NOT_PASS_ONE";
    private const string PassOneReviewAccepted = "ACCEPTED_PASS_ONE";
    private const string PassOneReviewUnreviewed = "UNREVIEWED_PASS_ONE";
    private const string PassOneReviewRejected = "REJECTED_PASS_ONE";

    static Command CreateRenderQualityScanCommand()
    {
        var corpusArg = new Argument<DirectoryInfo>("corpus") { Description = "Directory of PDFs to scan" };
        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Output JSON path",
            Required = true,
        };
        var contractsOption = new Option<DirectoryInfo>("--contracts")
        {
            Description = "Directory containing per-PDF rendering quality contract JSON files",
            DefaultValueFactory = _ => new DirectoryInfo("test-pdfs/rendering-contracts"),
        };
        var contractPathContainsOption = new Option<string?>("--contract-path-contains")
        {
            Description = "Only scan contracts whose normalized PDF path contains this text.",
        };
        var contractRootCauseOption = new Option<string?>("--contract-root-cause")
        {
            Description = "Only scan contracts/pages whose root-cause value contains this text.",
        };
        var contractOwnerOption = new Option<string?>("--contract-owner")
        {
            Description = "Only scan contracts whose Owner contains this text.",
        };
        var contractIssueOption = new Option<int?>("--contract-issue")
        {
            Description = "Only scan contracts/pages linked to this GitHub issue number.",
        };
        var pageModeOption = new Option<string>("--page-mode")
        {
            Description = "Pages to compare: first, sample, or all.",
            DefaultValueFactory = _ => "all",
        };
        var extraOraclesOption = new Option<string>("--oracles")
        {
            Description = "Reference oracles: none, ghostscript, pdfbox, pdfium, or all (comma-separated).",
            DefaultValueFactory = _ => "all",
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Render DPI",
            DefaultValueFactory = _ => 150,
        };
        var diffPctOption = new Option<double>("--max-diff-fraction")
        {
            Description = "Pass-fail threshold for differing-pixel fraction",
            DefaultValueFactory = _ => 0.10,
        };
        var maxMaeOption = new Option<double>("--max-mae")
        {
            Description = "Pass-fail threshold for mean-absolute-error per channel",
            DefaultValueFactory = _ => 32.0,
        };
        var parallelOption = new Option<int>("--parallel")
        {
            Description = "Concurrent PDFs. 0 = auto (ProcessorCount/2).",
            DefaultValueFactory = _ => 0,
        };
        var perPdfTimeoutOption = new Option<int>("--pdf-timeout-ms")
        {
            Description = "Timeout budget per PDF/oracle render.",
            DefaultValueFactory = _ => 120_000,
        };
        var strictContractsOption = new Option<bool>("--strict-contracts")
        {
            Description = "Fail the quality report when scanned pages have no contract.",
            DefaultValueFactory = _ => false,
        };
        var rawOutputOption = new Option<FileInfo?>("--raw-output")
        {
            Description = "Optional path for the underlying raw corpus-scan report.",
        };
        var oracleCacheDirOption = new Option<DirectoryInfo?>("--oracle-cache-dir")
        {
            Description = "Directory for cached third-party oracle PNGs.",
        };
        var noOracleCacheOption = new Option<bool>("--no-oracle-cache")
        {
            Description = "Disable the third-party oracle render cache.",
            DefaultValueFactory = _ => false,
        };
        var pdfeRenderCacheDirOption = new Option<DirectoryInfo?>("--pdfe-render-cache-dir")
        {
            Description = "Directory for cached pdfe-rendered PNGs.",
        };
        var progressIntervalOption = new Option<int>("--progress-interval-seconds")
        {
            Description = "Emit raw corpus-scan heartbeat progress every N seconds. 0 disables heartbeat output.",
            DefaultValueFactory = _ => 30,
        };
        var progressOutputOption = new Option<FileInfo?>("--progress-output")
        {
            Description = "Optional JSON sidecar for heartbeat progress. Defaults to <raw-output>.progress.json when heartbeat is enabled.",
        };
        var incrementalRawOutputOption = new Option<bool>("--incremental-raw-output")
        {
            Description = "Rewrite --raw-output with completed page results on each heartbeat so interrupted long runs leave usable data.",
            DefaultValueFactory = _ => false,
        };
        var largePdfShardPagesOption = new Option<int>("--large-pdf-shard-pages")
        {
            Description = "Split page-manifest PDFs with more than N selected pages into independent work items. 0 disables sharding.",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command(
            "render-quality-scan",
            "Render corpus PDFs with contract-driven quality/release classification")
        {
            corpusArg,
            outputOption,
            contractsOption,
            contractPathContainsOption,
            contractRootCauseOption,
            contractOwnerOption,
            contractIssueOption,
            pageModeOption,
            extraOraclesOption,
            dpiOption,
            diffPctOption,
            maxMaeOption,
            parallelOption,
            perPdfTimeoutOption,
            strictContractsOption,
            rawOutputOption,
            oracleCacheDirOption,
            noOracleCacheOption,
            pdfeRenderCacheDirOption,
            progressIntervalOption,
            progressOutputOption,
            incrementalRawOutputOption,
            largePdfShardPagesOption,
        };

        command.SetAction(parseResult =>
        {
            var corpus = parseResult.GetValue(corpusArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var contracts = parseResult.GetValue(contractsOption)!;
            var contractPathContains = parseResult.GetValue(contractPathContainsOption);
            var contractRootCause = parseResult.GetValue(contractRootCauseOption);
            var contractOwner = parseResult.GetValue(contractOwnerOption);
            var contractIssue = parseResult.GetValue(contractIssueOption);
            var pageModeRaw = parseResult.GetValue(pageModeOption) ?? "all";
            var extraOraclesRaw = parseResult.GetValue(extraOraclesOption) ?? "all";
            var dpi = parseResult.GetValue(dpiOption);
            var maxDiffFraction = parseResult.GetValue(diffPctOption);
            var maxMae = parseResult.GetValue(maxMaeOption);
            var parallel = parseResult.GetValue(parallelOption);
            var pdfTimeoutMs = parseResult.GetValue(perPdfTimeoutOption);
            var strictContracts = parseResult.GetValue(strictContractsOption);
            var rawOutput = parseResult.GetValue(rawOutputOption);
            var oracleCacheDir = parseResult.GetValue(oracleCacheDirOption);
            var noOracleCache = parseResult.GetValue(noOracleCacheOption);
            var pdfeRenderCacheDir = parseResult.GetValue(pdfeRenderCacheDirOption);
            var progressIntervalSeconds = parseResult.GetValue(progressIntervalOption);
            var progressOutput = parseResult.GetValue(progressOutputOption);
            var incrementalRawOutput = parseResult.GetValue(incrementalRawOutputOption);
            var largePdfShardPages = parseResult.GetValue(largePdfShardPagesOption);

            if (parallel <= 0) parallel = Math.Max(1, Environment.ProcessorCount / 2);
            if (!TryParseCorpusPageMode(pageModeRaw, out var pageMode))
            {
                Console.Error.WriteLine($"Bad --page-mode '{pageModeRaw}'. Use first, sample, or all.");
                Environment.ExitCode = 1;
                return;
            }
            if (!TryParseCorpusExtraOracles(extraOraclesRaw, out var extraOracles, out var extraOracleError))
            {
                Console.Error.WriteLine(extraOracleError);
                Environment.ExitCode = 1;
                return;
            }
            if (!corpus.Exists)
            {
                Console.Error.WriteLine($"Corpus not found: {corpus.FullName}");
                Environment.ExitCode = 1;
                return;
            }
            if (!contracts.Exists)
            {
                Console.Error.WriteLine($"Rendering quality contracts not found: {contracts.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var ok = RunRenderQualityScan(
                    corpus.FullName,
                    contracts.FullName,
                    output.FullName,
                    rawOutput?.FullName,
                    dpi,
                    maxDiffFraction,
                    maxMae,
                    parallel,
                    pdfTimeoutMs,
                    pageMode,
                    extraOracles,
                    strictContracts,
                    noOracleCache ? null : ResolveCorpusOracleCacheDir(oracleCacheDir),
                    pdfeRenderCacheDir,
                    progressIntervalSeconds,
                    progressOutput?.FullName,
                    incrementalRawOutput,
                    largePdfShardPages,
                    new RenderingQualityContractFilter(
                        contractPathContains,
                        contractRootCause,
                        contractOwner,
                        contractIssue));
                Environment.ExitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    static Command CreateRenderQualityClassifyCommand()
    {
        var rawReportArg = new Argument<FileInfo>("raw-report") { Description = "Existing corpus-scan JSON report" };
        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Output quality JSON path",
            Required = true,
        };
        var contractsOption = new Option<DirectoryInfo>("--contracts")
        {
            Description = "Directory containing per-PDF rendering quality contract JSON files",
            DefaultValueFactory = _ => new DirectoryInfo("test-pdfs/rendering-contracts"),
        };
        var strictContractsOption = new Option<bool>("--strict-contracts")
        {
            Description = "Mark pages without contracts as NEEDS_REVIEW.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "render-quality-classify",
            "Apply rendering quality contracts to an existing raw corpus-scan JSON report")
        {
            rawReportArg,
            outputOption,
            contractsOption,
            strictContractsOption,
        };

        command.SetAction(parseResult =>
        {
            var rawReport = parseResult.GetValue(rawReportArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var contracts = parseResult.GetValue(contractsOption)!;
            var strictContracts = parseResult.GetValue(strictContractsOption);

            if (!rawReport.Exists)
            {
                Console.Error.WriteLine($"Raw corpus report not found: {rawReport.FullName}");
                Environment.ExitCode = 1;
                return;
            }
            if (!contracts.Exists)
            {
                Console.Error.WriteLine($"Rendering quality contracts not found: {contracts.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var ok = RunRenderQualityClassify(
                    rawReport.FullName,
                    contracts.FullName,
                    output.FullName,
                    strictContracts);
                Environment.ExitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    internal static bool RunRenderQualityScan(
        string corpusDir,
        string contractsDir,
        string outputPath,
        string? rawOutputPath,
        int dpi,
        double maxDiffFraction,
        double maxMae,
        int parallel,
        int pdfTimeoutMs,
        CorpusPageMode pageMode,
        CorpusExtraOracles extraOracles,
        bool strictContracts,
        DirectoryInfo? oracleCacheDir,
        DirectoryInfo? pdfeRenderCacheDir,
        int progressIntervalSeconds = 30,
        string? progressOutputPath = null,
        bool incrementalRawOutput = false,
        int largePdfShardPages = 0,
        RenderingQualityContractFilter? contractFilter = null)
    {
        var contractSet = RenderingQualityContractSet.Load(contractsDir);
        if (contractFilter is { IsEmpty: false })
        {
            contractSet = contractSet.Filter(contractFilter);
            if (contractSet.Contracts.Count == 0)
            {
                Console.Error.WriteLine("No rendering quality contracts matched the requested filter.");
                return false;
            }
        }
        var pageManifest = contractSet.CreatePageManifest();
        var passwordManifest = contractSet.CreatePasswordManifest();
        var expectations = contractSet.CreateExpectationManifest();
        var rawPath = string.IsNullOrWhiteSpace(rawOutputPath)
            ? Path.Combine(
                Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
                Path.GetFileNameWithoutExtension(outputPath) + ".raw-corpus-scan.json")
            : rawOutputPath;

        var scanOk = RunCorpusScan(
            corpusDir,
            rawPath,
            chunkIndex: 0,
            chunkTotal: 1,
            dpi,
            maxDiffFraction,
            maxMae,
            parallel,
            pdfTimeoutMs,
            pageMode,
            extraOracles,
            pageManifest,
            passwordManifest,
            expectations,
            oracleCacheDir,
            pdfeRenderCacheDir,
            progressIntervalSeconds,
            progressOutputPath,
            incrementalRawOutput,
            largePdfShardPages);
        if (!scanOk)
            return false;

        var rawReport = LoadCorpusScanReport(rawPath);
        ApplyRenderingQualityContracts(rawReport.entries, contractSet, strictContracts);
        var report = BuildRenderingQualityReport(rawReport, contractSet, contractsDir, strictContracts);
        var json = JsonSerializer.Serialize(report, RenderingQualityJsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.Out.WriteLine($"  wrote quality report {outputPath}");
        PrintRenderingQualitySummary(report.summary);
        return !strictContracts || report.summary.missingContractPages == 0;
    }

    internal static bool RunRenderQualityClassify(
        string rawReportPath,
        string contractsDir,
        string outputPath,
        bool strictContracts)
    {
        var contractSet = RenderingQualityContractSet.Load(contractsDir);
        var rawReport = LoadCorpusScanReport(rawReportPath);
        ApplyRenderingQualityContracts(rawReport.entries, contractSet, strictContracts);
        var report = BuildRenderingQualityReport(rawReport, contractSet, contractsDir, strictContracts);
        var json = JsonSerializer.Serialize(report, RenderingQualityJsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.Out.WriteLine($"  wrote quality report {outputPath}");
        PrintRenderingQualitySummary(report.summary);
        return !strictContracts || report.summary.missingContractPages == 0;
    }

    internal static void ApplyRenderingQualityContracts(
        IReadOnlyList<CorpusScanEntry> entries,
        RenderingQualityContractSet contracts,
        bool strictContracts)
    {
        foreach (var entry in entries)
        {
            var contract = contracts.FindPage(entry.path, entry.pageNumber);
            entry.releaseStatus = "PASS";
            entry.qualityStatus = InferQualityStatus(entry, contract, strictContracts);
            entry.pixelAgreement = InferPixelAgreement(entry, contract);
            entry.referenceSituation = InferReferenceSituation(entry);
            entry.targetBasis = contract?.Target?.Mode ?? InferTargetBasis(entry);
            entry.targetRenderer = contract?.Target?.Primary;
            entry.rootCause = contract?.RootCause ?? entry.resultCategory ?? RenderingQualityUnknown;
            entry.improvementPriority = contract?.ImprovementPriority ?? InferImprovementPriority(entry);
            entry.confidence = contract?.Confidence ?? InferConfidence(entry);
            entry.trackedBy = contract?.Issue is null ? null : $"#{contract.Issue.Value}";
            entry.qualityReason = contract?.QualityReason
                ?? contract?.Notes
                ?? entry.resultReason
                ?? entry.expectedNote;

            var page = contract?.Page;
            if (page is not null)
            {
                entry.releaseStatus = string.IsNullOrWhiteSpace(page.ReleaseStatus) ? "PASS" : page.ReleaseStatus;
                entry.qualityStatus = string.IsNullOrWhiteSpace(page.QualityStatus) ? entry.qualityStatus : page.QualityStatus;
                entry.pixelAgreement = NormalizeContractPixelAgreement(
                    entry,
                    string.IsNullOrWhiteSpace(page.PixelAgreement) ? entry.pixelAgreement : page.PixelAgreement,
                    contract);
                entry.referenceSituation = string.IsNullOrWhiteSpace(page.ReferenceSituation) ? entry.referenceSituation : page.ReferenceSituation;
                entry.targetBasis = page.Target?.Mode ?? entry.targetBasis;
                entry.targetRenderer = page.Target?.Primary ?? entry.targetRenderer;
                entry.rootCause = string.IsNullOrWhiteSpace(page.RootCause) ? entry.rootCause : page.RootCause;
                entry.improvementPriority = string.IsNullOrWhiteSpace(page.ImprovementPriority) ? entry.improvementPriority : page.ImprovementPriority;
                entry.confidence = string.IsNullOrWhiteSpace(page.Confidence) ? entry.confidence : page.Confidence;
                entry.qualityReason = page.QualityReason ?? page.Notes ?? entry.qualityReason;
                if (page.Issue is not null)
                    entry.trackedBy = $"#{page.Issue.Value}";
            }

            if (contract is null)
            {
                entry.contractStatus = "MISSING";
                if (strictContracts)
                {
                    entry.releaseStatus = "NEEDS_REVIEW";
                    entry.qualityStatus = "NEEDS_REVIEW";
                    entry.qualityReason = "No rendering quality contract covers this scanned page.";
                }
            }
            else
            {
                entry.contractStatus = "APPLIED";
            }

            entry.passOneReviewStatus = InferPassOneReviewStatus(entry, contract);
            if (entry.status == "PASS_ONE" && entry.passOneReviewStatus == PassOneReviewUnreviewed)
            {
                entry.qualityReason = string.IsNullOrWhiteSpace(entry.qualityReason)
                    ? "Raw PASS_ONE has not been assigned a reviewed target decision."
                    : entry.qualityReason;
            }
        }
    }

    private static string InferQualityStatus(CorpusScanEntry entry, RenderingQualityPageMatch? contract, bool strictContracts)
    {
        if (contract is null && strictContracts)
            return "NEEDS_REVIEW";

        return entry.status switch
        {
            "PASS" => "PIXEL_EXACT",
            "PASS_ONE" => "MATCHES_ACCEPTED_REFERENCE",
            "ALL_ORACLES_REFUSED" => "REFERENCE_REFUSAL_ACCEPTED",
            "RECOVERED_MALFORMED_CONTENT" => "NON_RENDERABLE_ACCEPTED",
            "MALFORMED_PDF" or "EMPTY_DOC" or "PASSWORD_REQUIRED" or "RESOURCE_LIMIT" or "INVALID_PAGE_GEOMETRY" =>
                "NON_RENDERABLE_ACCEPTED",
            "DIFF" => "FAIL",
            _ => string.Equals(entry.resultStatus, "PASS", StringComparison.Ordinal)
                ? "GOOD_ENOUGH"
                : "FAIL",
        };
    }

    private static string InferPixelAgreement(CorpusScanEntry entry, RenderingQualityPageMatch? contract)
    {
        if (entry.status == "PASS")
            return "MATCHES_ALL_REQUIRED";

        if (entry.status == "PASS_ONE")
        {
            if (!string.IsNullOrWhiteSpace(contract?.Target?.Primary)
                && string.Equals(entry.bestOracle, contract.Target.Primary, StringComparison.Ordinal))
                return "MATCHES_TARGET";

            if (entry.agreeingOracles == 1)
                return "MATCHES_ONE_REFERENCE";

            return "MATCHES_SOME";
        }

        if (entry.status == "DIFF")
            return "MATCHES_NONE";

        return "NOT_COMPARABLE";
    }

    private static string NormalizeContractPixelAgreement(
        CorpusScanEntry entry,
        string proposed,
        RenderingQualityPageMatch? contract)
    {
        if (entry.status == "PASS_ONE" && proposed == "MATCHES_NONE")
            return InferPixelAgreement(entry, contract);

        if (entry.status == "PASS" && proposed != "MATCHES_ALL_REQUIRED")
            return "MATCHES_ALL_REQUIRED";

        if (entry.status == "DIFF" && proposed is not "MATCHES_NONE" and not "NOT_COMPARABLE")
            return "MATCHES_NONE";

        return proposed;
    }

    private static string InferReferenceSituation(CorpusScanEntry entry)
    {
        if (entry.status == "ALL_ORACLES_REFUSED")
            return "REFS_REFUSE";

        if (entry.comparedOracles is null or 0)
            return "REFS_INCOMPLETE";

        if (entry.oracleDisagreeingPairs is > 0)
            return "REFS_DISAGREE";

        return "REFS_AGREE";
    }

    private static string InferTargetBasis(CorpusScanEntry entry)
        => entry.status switch
        {
            "MALFORMED_PDF" or "EMPTY_DOC" or "PASSWORD_REQUIRED" or "INVALID_PAGE_GEOMETRY" => "MALFORMED_INPUT_POLICY",
            "RESOURCE_LIMIT" or "TIMEOUT" => "RESOURCE_POLICY",
            "ALL_ORACLES_REFUSED" => "QUALITY_STANDARD",
            "PASS_ONE" => "REFERENCE_DISAGREEMENT",
            _ => "REFERENCE_CONSENSUS",
        };

    private static string InferImprovementPriority(CorpusScanEntry entry)
        => entry.status switch
        {
            "DIFF" => "P1",
            "PASS_ONE" => "P2",
            _ => "NONE",
        };

    private static string InferConfidence(CorpusScanEntry entry)
        => entry.status switch
        {
            "PASS" => "HIGH",
            "PASS_ONE" => entry.comparedOracles >= 3 ? "MEDIUM" : "LOW",
            _ => "MEDIUM",
        };

    private static string InferPassOneReviewStatus(CorpusScanEntry entry, RenderingQualityPageMatch? contract)
    {
        if (entry.status != "PASS_ONE")
            return PassOneReviewNotApplicable;

        if (entry.releaseStatus is "BLOCKED" or "FAIL" || entry.qualityStatus == "FAIL")
            return PassOneReviewRejected;

        if (contract is null)
            return PassOneReviewUnreviewed;

        var explicitReviewStatus = contract.ReviewStatus;
        if (string.Equals(explicitReviewStatus, "REVIEWED", StringComparison.Ordinal))
            return PassOneReviewAccepted;
        if (string.Equals(explicitReviewStatus, "GENERATED", StringComparison.Ordinal) ||
            string.Equals(explicitReviewStatus, "NEEDS_REVIEW", StringComparison.Ordinal))
            return PassOneReviewUnreviewed;

        if (LooksLikeGeneratedBaseline(contract))
            return PassOneReviewUnreviewed;

        if (contract.Target is null)
            return PassOneReviewUnreviewed;

        if (string.IsNullOrWhiteSpace(contract.QualityReason) && string.IsNullOrWhiteSpace(contract.Notes))
            return PassOneReviewUnreviewed;

        return PassOneReviewAccepted;
    }

    private static bool LooksLikeGeneratedBaseline(RenderingQualityPageMatch contract)
    {
        static bool ContainsGeneratedMarker(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && (value.Contains("Baseline", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("Auto-classified", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("promote to a reviewed contract", StringComparison.OrdinalIgnoreCase));

        return string.Equals(contract.RootCause, "FULL_CORPUS_BASELINE", StringComparison.Ordinal) ||
               ContainsGeneratedMarker(contract.QualityReason) ||
               ContainsGeneratedMarker(contract.Notes) ||
               ContainsGeneratedMarker(contract.Target?.Reason);
    }

    private static RenderingQualityReport BuildRenderingQualityReport(
        CorpusScanReport rawReport,
        RenderingQualityContractSet contracts,
        string contractsDir,
        bool strictContracts)
    {
        var entries = rawReport.entries
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ThenBy(entry => entry.pageNumber)
            .ToArray();

        var reportEntries = entries
            .Select(RenderingQualityReportEntry.FromCorpusEntry)
            .ToArray();

        return new RenderingQualityReport
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            corpus = rawReport.corpus,
            contracts = contractsDir,
            strictContracts = strictContracts,
            summary = new RenderingQualitySummary
            {
                pagesScanned = entries.Length,
                pdfsScanned = entries.Select(entry => entry.path).Distinct(StringComparer.Ordinal).Count(),
                contractFiles = contracts.Contracts.Count,
                missingContractPages = entries.Count(entry => string.Equals(entry.contractStatus, "MISSING", StringComparison.Ordinal)),
                rawStatusCounts = CountBy(entries.Select(entry => entry.status)),
                releaseStatusCounts = CountBy(entries.Select(entry => entry.releaseStatus ?? RenderingQualityUnknown)),
                qualityStatusCounts = CountBy(entries.Select(entry => entry.qualityStatus ?? RenderingQualityUnknown)),
                pixelAgreementCounts = CountBy(entries.Select(entry => entry.pixelAgreement ?? RenderingQualityUnknown)),
                referenceSituationCounts = CountBy(entries.Select(entry => entry.referenceSituation ?? RenderingQualityUnknown)),
                targetBasisCounts = CountBy(entries.Select(entry => entry.targetBasis ?? RenderingQualityUnknown)),
                targetRendererCounts = CountBy(entries.Select(entry => entry.targetRenderer ?? "none")),
                rootCauseCounts = CountBy(entries.Select(entry => entry.rootCause ?? RenderingQualityUnknown)),
                improvementPriorityCounts = CountBy(entries.Select(entry => entry.improvementPriority ?? RenderingQualityUnknown)),
                confidenceCounts = CountBy(entries.Select(entry => entry.confidence ?? RenderingQualityUnknown)),
                passOneReviewStatusCounts = CountBy(entries.Select(entry => entry.passOneReviewStatus ?? PassOneReviewNotApplicable)),
                expectationResultCounts = CountBy(entries.Select(entry => entry.expectationResult ?? RenderingQualityUnknown)),
            },
            failures = reportEntries
                .Where(entry => entry.releaseStatus is "BLOCKED" or "FAIL" || entry.qualityStatus == "FAIL")
                .ToArray(),
            needsReview = reportEntries
                .Where(entry => entry.releaseStatus == "NEEDS_REVIEW" || entry.qualityStatus == "NEEDS_REVIEW")
                .ToArray(),
            unreviewedPassOne = reportEntries
                .Where(entry => entry.passOneReviewStatus == PassOneReviewUnreviewed)
                .ToArray(),
            rejectedPassOne = reportEntries
                .Where(entry => entry.passOneReviewStatus == PassOneReviewRejected)
                .ToArray(),
            acceptedLimitations = reportEntries
                .Where(entry => entry.qualityStatus == "ACCEPTED_LIMITATION")
                .ToArray(),
            referenceDisagreements = reportEntries
                .Where(entry => entry.referenceSituation == "REFS_DISAGREE")
                .ToArray(),
            passOneTriage = BuildPassOneTriage(reportEntries),
            entries = reportEntries,
        };
    }

    private static IReadOnlyList<RenderingQualityPassOneTriageCluster> BuildPassOneTriage(
        IReadOnlyList<RenderingQualityReportEntry> entries)
    {
        return entries
            .Where(entry => entry.rawStatus == "PASS_ONE")
            .GroupBy(entry => new
            {
                entry.passOneReviewStatus,
                entry.rootCause,
                entry.targetBasis,
                targetRenderer = entry.targetRenderer ?? "none",
                entry.referenceSituation,
                entry.visualHumanImpact,
                entry.visualCategory,
                bestOracle = entry.bestOracle ?? "none",
                entry.contractStatus,
            })
            .Select(group => new RenderingQualityPassOneTriageCluster
            {
                passOneReviewStatus = group.Key.passOneReviewStatus,
                rootCause = group.Key.rootCause,
                targetBasis = group.Key.targetBasis,
                targetRenderer = group.Key.targetRenderer,
                referenceSituation = group.Key.referenceSituation,
                visualHumanImpact = group.Key.visualHumanImpact,
                visualCategory = group.Key.visualCategory,
                bestOracle = group.Key.bestOracle,
                contractStatus = group.Key.contractStatus,
                count = group.Count(),
                pdfCount = group.Select(entry => entry.path).Distinct(StringComparer.Ordinal).Count(),
                representativePages = group
                    .OrderBy(entry => entry.path, StringComparer.Ordinal)
                    .ThenBy(entry => entry.pageNumber)
                    .Take(12)
                    .Select(entry => new RenderingQualityPageRef
                    {
                        path = entry.path,
                        pageNumber = entry.pageNumber,
                        qualityStatus = entry.qualityStatus,
                        diffFraction = entry.diffFraction,
                        mae = entry.mae,
                    })
                    .ToArray(),
            })
            .OrderBy(cluster => PassOneReviewRank(cluster.passOneReviewStatus))
            .ThenByDescending(cluster => cluster.count)
            .ThenBy(cluster => cluster.rootCause, StringComparer.Ordinal)
            .ThenBy(cluster => cluster.targetBasis, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PassOneReviewRank(string? status)
        => status switch
        {
            PassOneReviewRejected => 0,
            PassOneReviewUnreviewed => 1,
            PassOneReviewAccepted => 2,
            _ => 3,
        };

    private static void PrintRenderingQualitySummary(RenderingQualitySummary summary)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("Rendering quality summary:");
        Console.Out.WriteLine($"  pages scanned: {summary.pagesScanned}");
        Console.Out.WriteLine($"  missing contract pages: {summary.missingContractPages}");
        PrintCountGroup("  quality", summary.qualityStatusCounts);
        PrintCountGroup("  pass-one review", summary.passOneReviewStatusCounts);
        PrintCountGroup("  pixel agreement", summary.pixelAgreementCounts);
        PrintCountGroup("  reference situation", summary.referenceSituationCounts);
        PrintCountGroup("  release", summary.releaseStatusCounts);
        PrintCountGroup("  expectations", summary.expectationResultCounts);
    }

    private static void PrintCountGroup(string label, IReadOnlyDictionary<string, int> counts)
    {
        Console.Out.WriteLine(label + ":");
        foreach (var kv in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            Console.Out.WriteLine($"    {kv.Value,5}  {kv.Key}");
    }

    private static CorpusScanReport LoadCorpusScanReport(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CorpusScanReport>(stream, RenderingQualityJsonOptions)
               ?? throw new InvalidDataException($"Could not read corpus scan report: {path}");
    }

    private static readonly JsonSerializerOptions RenderingQualityJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal sealed class RenderingQualityContractSet
    {
        public IReadOnlyList<RenderingQualityPdfContract> Contracts { get; init; } = Array.Empty<RenderingQualityPdfContract>();
        private Dictionary<CorpusPageKey, RenderingQualityPageMatch> PageContracts { get; init; } = new();

        public static RenderingQualityContractSet Load(string contractsDir)
        {
            var files = Directory
                .EnumerateFiles(contractsDir, "*.json", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).StartsWith('_'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var contracts = new List<RenderingQualityPdfContract>();
            var pageContracts = new Dictionary<CorpusPageKey, RenderingQualityPageMatch>();
            foreach (var file in files)
            {
                using var stream = File.OpenRead(file);
                var contract = JsonSerializer.Deserialize<RenderingQualityPdfContract>(stream, RenderingQualityJsonOptions)
                               ?? throw new InvalidDataException($"Empty rendering contract: {file}");
                contract.ContractFile = file;
                contract.Path = NormalizeManifestPath(contract.Path);
                contract.Validate();
                contracts.Add(contract);

                foreach (var page in contract.ExpandPages())
                {
                    var key = new CorpusPageKey(contract.Path, page.PageNumber);
                    if (pageContracts.ContainsKey(key))
                        throw new InvalidDataException($"Duplicate rendering contract for {contract.Path} page {page.PageNumber}");

                    pageContracts[key] = new RenderingQualityPageMatch(contract, page);
                }
            }

            return new RenderingQualityContractSet
            {
                Contracts = contracts,
                PageContracts = pageContracts,
            };
        }

        public RenderingQualityContractSet Filter(RenderingQualityContractFilter filter)
        {
            if (filter.IsEmpty)
                return this;

            var pageContracts = PageContracts
                .Where(kvp => filter.Matches(kvp.Value))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var contracts = pageContracts.Values
                .Select(match => match.Contract)
                .DistinctBy(contract => contract.ContractFile ?? contract.Path)
                .OrderBy(contract => contract.ContractFile ?? contract.Path, StringComparer.Ordinal)
                .ToArray();

            return new RenderingQualityContractSet
            {
                Contracts = contracts,
                PageContracts = pageContracts,
            };
        }

        public IReadOnlyDictionary<string, IReadOnlySet<int>> CreatePageManifest()
        {
            return PageContracts
                .GroupBy(kvp => kvp.Key.Path, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<int>)group.Select(kvp => kvp.Key.PageNumber).ToHashSet(),
                    StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, string>? CreatePasswordManifest()
        {
            var passwords = Contracts
                .Where(contract => !string.IsNullOrWhiteSpace(contract.Password))
                .ToDictionary(contract => contract.Path, contract => contract.Password!, StringComparer.Ordinal);
            return passwords.Count == 0 ? null : passwords;
        }

        public IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome> CreateExpectationManifest()
        {
            return PageContracts.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var page = kvp.Value.Page;
                    var contract = kvp.Value.Contract;
                    return new CorpusExpectedOutcome(
                        string.IsNullOrWhiteSpace(page.ExpectedRawStatus)
                            ? "*"
                            : page.ExpectedRawStatus,
                        page.ExpectedErrorContains ?? string.Empty,
                        page.Notes ?? contract.Notes ?? string.Empty,
                        page.ReleaseStatus ?? "PASS",
                        page.RootCause ?? contract.RootCause ?? string.Empty,
                        page.QualityReason ?? page.Notes ?? contract.QualityReason ?? contract.Notes ?? string.Empty);
                });
        }

        public RenderingQualityPageMatch? FindPage(string path, int pageNumber)
        {
            var normalized = NormalizeManifestPath(path);
            if (PageContracts.TryGetValue(new CorpusPageKey(normalized, pageNumber), out var match))
                return match;

            if (!normalized.Contains('/', StringComparison.Ordinal) &&
                PageContracts.TryGetValue(new CorpusPageKey("pdfjs/" + normalized, pageNumber), out match))
            {
                return match;
            }

            const string pdfjsPrefix = "pdfjs/";
            if (normalized.StartsWith(pdfjsPrefix, StringComparison.Ordinal) &&
                PageContracts.TryGetValue(new CorpusPageKey(normalized[pdfjsPrefix.Length..], pageNumber), out match))
            {
                return match;
            }

            return null;
        }
    }

    internal sealed record RenderingQualityPageMatch(
        RenderingQualityPdfContract Contract,
        RenderingQualityPageContract Page)
    {
        public RenderingQualityTarget? Target => Page.Target ?? Contract.Target;
        public string? RootCause => Page.RootCause ?? Contract.RootCause;
        public string? ImprovementPriority => Page.ImprovementPriority ?? Contract.ImprovementPriority;
        public string? Confidence => Page.Confidence ?? Contract.Confidence;
        public int? Issue => Page.Issue ?? Contract.Issue;
        public string? Notes => Page.Notes ?? Contract.Notes;
        public string? QualityReason => Page.QualityReason ?? Contract.QualityReason;
        public string? ReviewStatus => Page.ReviewStatus ?? Contract.ReviewStatus;
    }

    internal sealed record RenderingQualityContractFilter(
        string? PathContains,
        string? RootCauseContains,
        string? OwnerContains,
        int? Issue)
    {
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(PathContains) &&
            string.IsNullOrWhiteSpace(RootCauseContains) &&
            string.IsNullOrWhiteSpace(OwnerContains) &&
            Issue is null;

        public bool Matches(RenderingQualityPageMatch match)
        {
            if (!Contains(match.Contract.Path, PathContains))
                return false;
            if (!Contains(match.RootCause, RootCauseContains))
                return false;
            if (!Contains(match.Contract.Owner, OwnerContains))
                return false;
            if (Issue is { } issue && match.Issue != issue)
                return false;

            return true;
        }

        private static bool Contains(string? value, string? expected)
            => string.IsNullOrWhiteSpace(expected) ||
               (!string.IsNullOrWhiteSpace(value) &&
                value.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class RenderingQualityPdfContract
    {
        private static readonly HashSet<string> KnownRawStatuses = new(StringComparer.Ordinal)
        {
            "*",
            "PASS",
            "PASS_ONE",
            "DIFF",
            "COMPARE_ERROR",
            "ALL_ORACLES_REFUSED",
            "RECOVERED_MALFORMED_CONTENT",
            "DECODE_ERROR",
            "MALFORMED_PDF",
            "EMPTY_DOC",
            "PASSWORD_REQUIRED",
            "RESOURCE_LIMIT",
            "INVALID_PAGE_GEOMETRY",
            "TIMEOUT",
            "UNSUPPORTED_COMPRESSION",
        };

        private static readonly HashSet<string> KnownReleaseStatuses = new(StringComparer.Ordinal)
        {
            "PASS",
            "FAIL",
            "BLOCKED",
            "NEEDS_REVIEW",
        };

        private static readonly HashSet<string> KnownQualityStatuses = new(StringComparer.Ordinal)
        {
            "PIXEL_EXACT",
            "TARGET_MATCH",
            "MATCHES_ACCEPTED_REFERENCE",
            "REFERENCE_REFUSAL_ACCEPTED",
            "GOOD_ENOUGH",
            "PDFE_BETTER_THAN_REFS",
            "ACCEPTED_LIMITATION",
            "LOWER_QUALITY_ACCEPTABLE",
            "NON_RENDERABLE_ACCEPTED",
            "FAIL",
            "NEEDS_REVIEW",
        };

        private static readonly HashSet<string> KnownPixelAgreements = new(StringComparer.Ordinal)
        {
            "MATCHES_ALL_REQUIRED",
            "MATCHES_TARGET",
            "MATCHES_SOME",
            "MATCHES_ONE_REFERENCE",
            "MATCHES_NONE",
            "NOT_COMPARABLE",
        };

        private static readonly HashSet<string> KnownReferenceSituations = new(StringComparer.Ordinal)
        {
            "REFS_AGREE",
            "REFS_DISAGREE",
            "REFS_REFUSE",
            "REFS_INCOMPLETE",
            "REFS_WRONG_OR_LOSSY",
            "NOT_APPLICABLE",
        };

        private static readonly HashSet<string> KnownTargetModes = new(StringComparer.Ordinal)
        {
            "ACCEPTED_REFERENCE",
            "PDF_FONT_DICTIONARY_WITH_REFERENCE_EVIDENCE",
            "PREPRESS_REFERENCE_WITH_ALTONA_SEMANTICS",
            "REFERENCE_RENDERER",
            "REFERENCE_CONSENSUS",
            "REFERENCE_CONSENSUS_FIXED",
            "REFERENCE_DISAGREEMENT",
            "PDF_SPEC",
            "QUALITY_STANDARD",
            "RESOURCE_POLICY",
            "REFERENCE_REFUSAL",
            "MALFORMED_INPUT_POLICY",
        };

        private static readonly HashSet<string> KnownReviewStatuses = new(StringComparer.Ordinal)
        {
            "REVIEWED",
            "GENERATED",
            "NEEDS_REVIEW",
        };

        private static readonly HashSet<string> KnownImprovementPriorities = new(StringComparer.Ordinal)
        {
            "P0",
            "P1",
            "P2",
            "NONE",
            "DONE",
        };

        private static readonly HashSet<string> KnownConfidenceValues = new(StringComparer.Ordinal)
        {
            "HIGH",
            "MEDIUM",
            "LOW",
        };

        [JsonIgnore]
        public string? ContractFile { get; set; }
        public string Path { get; set; } = "";
        public string? Source { get; set; }
        public string? Password { get; set; }
        public string? Owner { get; set; }
        public int? Issue { get; set; }
        public string? RootCause { get; set; }
        public RenderingQualityTarget? Target { get; set; }
        public string? ReviewStatus { get; set; }
        public string? ImprovementPriority { get; set; }
        public string? Confidence { get; set; }
        public string? QualityReason { get; set; }
        public string? Notes { get; set; }
        public Dictionary<string, RenderingQualityPageContract> Pages { get; set; } = new(StringComparer.Ordinal);

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Path))
                throw new InvalidDataException($"{ContractFile}: path is required");
            if (Pages.Count == 0)
                throw new InvalidDataException($"{ContractFile}: at least one page contract is required");
            ValidateKnownValue(nameof(ReviewStatus), ReviewStatus, KnownReviewStatuses);
            ValidateKnownValue(nameof(ImprovementPriority), ImprovementPriority, KnownImprovementPriorities);
            ValidateKnownValue(nameof(Confidence), Confidence, KnownConfidenceValues);
            ValidateTarget("Target", Target);
            foreach (var page in ExpandPages())
            {
                if (page.PageNumber < 0)
                    throw new InvalidDataException($"{ContractFile}: page numbers must be non-negative");
                ValidatePage(page);
            }
        }

        private void ValidatePage(RenderingQualityPageContract page)
        {
            var prefix = $"page {page.PageNumber}";
            ValidateKnownValue($"{prefix} ExpectedRawStatus", page.ExpectedRawStatus, KnownRawStatuses);
            ValidateKnownValue($"{prefix} ReleaseStatus", page.ReleaseStatus, KnownReleaseStatuses);
            ValidateKnownValue($"{prefix} QualityStatus", page.QualityStatus, KnownQualityStatuses);
            ValidateKnownValue($"{prefix} PixelAgreement", page.PixelAgreement, KnownPixelAgreements);
            ValidateKnownValue($"{prefix} ReferenceSituation", page.ReferenceSituation, KnownReferenceSituations);
            ValidateKnownValue($"{prefix} ReviewStatus", page.ReviewStatus, KnownReviewStatuses);
            ValidateKnownValue($"{prefix} ImprovementPriority", page.ImprovementPriority, KnownImprovementPriorities);
            ValidateKnownValue($"{prefix} Confidence", page.Confidence, KnownConfidenceValues);
            ValidateTarget($"{prefix} Target", page.Target);

            if (page.ExpectedRawStatus is "PASS" or "PASS_ONE" &&
                page.PixelAgreement is "MATCHES_NONE" or "NOT_COMPARABLE")
            {
                throw new InvalidDataException(
                    $"{ContractFile}: {prefix} PixelAgreement {page.PixelAgreement} is incompatible with ExpectedRawStatus {page.ExpectedRawStatus}.");
            }

            if (page.QualityStatus == "FAIL" && page.ReleaseStatus == "PASS")
            {
                throw new InvalidDataException(
                    $"{ContractFile}: {prefix} QualityStatus FAIL must not use ReleaseStatus PASS.");
            }

            if (page.QualityStatus is "PIXEL_EXACT" or "TARGET_MATCH" or "MATCHES_ACCEPTED_REFERENCE" &&
                page.PixelAgreement is "MATCHES_NONE" or "NOT_COMPARABLE")
            {
                throw new InvalidDataException(
                    $"{ContractFile}: {prefix} PixelAgreement {page.PixelAgreement} is incompatible with QualityStatus {page.QualityStatus}.");
            }

            if (page.PixelAgreement == "NOT_COMPARABLE" &&
                page.QualityStatus is "PIXEL_EXACT" or "TARGET_MATCH" or "MATCHES_ACCEPTED_REFERENCE" or "GOOD_ENOUGH")
            {
                throw new InvalidDataException(
                    $"{ContractFile}: {prefix} PixelAgreement NOT_COMPARABLE is incompatible with QualityStatus {page.QualityStatus}.");
            }
        }

        private void ValidateKnownValue(string name, string? value, IReadOnlySet<string> knownValues)
        {
            if (string.IsNullOrWhiteSpace(value) || knownValues.Contains(value))
                return;

            throw new InvalidDataException(
                $"{ContractFile}: {name} '{value}' is not in the rendering quality vocabulary.");
        }

        private void ValidateTarget(string name, RenderingQualityTarget? target)
        {
            if (target is null)
                return;

            ValidateKnownValue($"{name}.Mode", target.Mode, KnownTargetModes);
        }

        public IEnumerable<RenderingQualityPageContract> ExpandPages()
        {
            foreach (var kvp in Pages)
            {
                foreach (var pageNumber in ExpandPageKey(kvp.Key))
                {
                    var page = kvp.Value.Clone();
                    page.PageNumber = pageNumber;
                    yield return page;
                }
            }
        }

        private static IEnumerable<int> ExpandPageKey(string raw)
        {
            var key = raw.Trim();
            if (int.TryParse(key, out var single))
            {
                yield return single;
                yield break;
            }

            var parts = key.Split('-', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var start)
                && int.TryParse(parts[1], out var end)
                && start <= end)
            {
                for (var page = start; page <= end; page++)
                    yield return page;
                yield break;
            }

            throw new InvalidDataException($"Invalid page key '{raw}'. Use an integer or range like '2-5'.");
        }
    }

    internal sealed class RenderingQualityPageContract
    {
        [JsonIgnore]
        public int PageNumber { get; set; }
        public string? ExpectedRawStatus { get; set; }
        public string? ExpectedErrorContains { get; set; }
        public string? ReleaseStatus { get; set; }
        public string? QualityStatus { get; set; }
        public string? PixelAgreement { get; set; }
        public string? ReferenceSituation { get; set; }
        public string? RootCause { get; set; }
        public RenderingQualityTarget? Target { get; set; }
        public RenderingQualityGoal? QualityGoal { get; set; }
        public string? ReviewStatus { get; set; }
        public string? ImprovementPriority { get; set; }
        public string? Confidence { get; set; }
        public int? Issue { get; set; }
        public string? QualityReason { get; set; }
        public string? Notes { get; set; }

        public RenderingQualityPageContract Clone()
            => new()
            {
                PageNumber = PageNumber,
                ExpectedRawStatus = ExpectedRawStatus,
                ExpectedErrorContains = ExpectedErrorContains,
                ReleaseStatus = ReleaseStatus,
                QualityStatus = QualityStatus,
                PixelAgreement = PixelAgreement,
                ReferenceSituation = ReferenceSituation,
                RootCause = RootCause,
                Target = Target,
                QualityGoal = QualityGoal,
                ReviewStatus = ReviewStatus,
                ImprovementPriority = ImprovementPriority,
                Confidence = Confidence,
                Issue = Issue,
                QualityReason = QualityReason,
                Notes = Notes,
            };
    }

    internal sealed class RenderingQualityTarget
    {
        public string Mode { get; set; } = "REFERENCE_CONSENSUS";
        public string? Primary { get; set; }
        public IReadOnlyList<string> AcceptableAlternates { get; set; } = Array.Empty<string>();
        public string? Reason { get; set; }
    }

    internal sealed class RenderingQualityGoal
    {
        public string Type { get; set; } = "semantic";
        public IReadOnlyList<string> Must { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Allowed { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Forbidden { get; set; } = Array.Empty<string>();
    }

    internal sealed class CorpusScanReport
    {
        public string generatedUtc { get; set; } = "";
        public string corpus { get; set; } = "";
        public Dictionary<string, int> counts { get; set; } = new(StringComparer.Ordinal);
        public CorpusScanEntry[] entries { get; set; } = Array.Empty<CorpusScanEntry>();
    }

    internal sealed class RenderingQualityReport
    {
        public string generatedUtc { get; set; } = "";
        public string corpus { get; set; } = "";
        public string contracts { get; set; } = "";
        public bool strictContracts { get; set; }
        public RenderingQualitySummary summary { get; set; } = new();
        public IReadOnlyList<RenderingQualityReportEntry> failures { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityReportEntry> needsReview { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityReportEntry> unreviewedPassOne { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityReportEntry> rejectedPassOne { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityReportEntry> acceptedLimitations { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityReportEntry> referenceDisagreements { get; set; } = Array.Empty<RenderingQualityReportEntry>();
        public IReadOnlyList<RenderingQualityPassOneTriageCluster> passOneTriage { get; set; } = Array.Empty<RenderingQualityPassOneTriageCluster>();
        public IReadOnlyList<RenderingQualityReportEntry> entries { get; set; } = Array.Empty<RenderingQualityReportEntry>();
    }

    internal sealed class RenderingQualitySummary
    {
        public int pagesScanned { get; set; }
        public int pdfsScanned { get; set; }
        public int contractFiles { get; set; }
        public int missingContractPages { get; set; }
        public Dictionary<string, int> rawStatusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> releaseStatusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> qualityStatusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> pixelAgreementCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> referenceSituationCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> targetBasisCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> targetRendererCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> rootCauseCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> improvementPriorityCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> confidenceCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> passOneReviewStatusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> expectationResultCounts { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class RenderingQualityPassOneTriageCluster
    {
        public string passOneReviewStatus { get; set; } = "";
        public string rootCause { get; set; } = "";
        public string targetBasis { get; set; } = "";
        public string targetRenderer { get; set; } = "";
        public string referenceSituation { get; set; } = "";
        public string? visualHumanImpact { get; set; }
        public string? visualCategory { get; set; }
        public string bestOracle { get; set; } = "";
        public string? contractStatus { get; set; }
        public int count { get; set; }
        public int pdfCount { get; set; }
        public IReadOnlyList<RenderingQualityPageRef> representativePages { get; set; } = Array.Empty<RenderingQualityPageRef>();
    }

    internal sealed class RenderingQualityPageRef
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; }
        public string qualityStatus { get; set; } = "";
        public double diffFraction { get; set; }
        public double mae { get; set; }
    }

    internal sealed class RenderingQualityReportEntry
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; }
        public string rawStatus { get; set; } = "";
        public string releaseStatus { get; set; } = "";
        public string qualityStatus { get; set; } = "";
        public string pixelAgreement { get; set; } = "";
        public string referenceSituation { get; set; } = "";
        public string targetBasis { get; set; } = "";
        public string? targetRenderer { get; set; }
        public string rootCause { get; set; } = "";
        public string improvementPriority { get; set; } = "";
        public string confidence { get; set; } = "";
        public string passOneReviewStatus { get; set; } = "";
        public string? trackedBy { get; set; }
        public string? bestOracle { get; set; }
        public string? visualHumanImpact { get; set; }
        public string? visualCategory { get; set; }
        public double diffFraction { get; set; }
        public double mae { get; set; }
        public int? comparedOracles { get; set; }
        public int? agreeingOracles { get; set; }
        public string? qualityReason { get; set; }
        public string? contractStatus { get; set; }
        public string? expectedRawStatus { get; set; }
        public string? expectationResult { get; set; }
        public string? expectationFailure { get; set; }

        public static RenderingQualityReportEntry FromCorpusEntry(CorpusScanEntry entry)
            => new()
            {
                path = entry.path,
                pageNumber = entry.pageNumber,
                rawStatus = entry.status,
                releaseStatus = entry.releaseStatus ?? RenderingQualityUnknown,
                qualityStatus = entry.qualityStatus ?? RenderingQualityUnknown,
                pixelAgreement = entry.pixelAgreement ?? RenderingQualityUnknown,
                referenceSituation = entry.referenceSituation ?? RenderingQualityUnknown,
                targetBasis = entry.targetBasis ?? RenderingQualityUnknown,
                targetRenderer = entry.targetRenderer,
                rootCause = entry.rootCause ?? RenderingQualityUnknown,
                improvementPriority = entry.improvementPriority ?? RenderingQualityUnknown,
                confidence = entry.confidence ?? RenderingQualityUnknown,
                passOneReviewStatus = entry.passOneReviewStatus ?? PassOneReviewNotApplicable,
                trackedBy = entry.trackedBy,
                bestOracle = entry.bestOracle,
                visualHumanImpact = entry.visualHumanImpact,
                visualCategory = entry.visualCategory,
                diffFraction = entry.diffFraction,
                mae = entry.mae,
                comparedOracles = entry.comparedOracles,
                agreeingOracles = entry.agreeingOracles,
                qualityReason = entry.qualityReason,
                contractStatus = entry.contractStatus,
                expectedRawStatus = entry.expectedStatus,
                expectationResult = entry.expectationResult,
                expectationFailure = entry.expectationFailure,
            };
    }
}
