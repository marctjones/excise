using System.CommandLine;

namespace Excise.RenderTools;

/// <summary>
/// Two checked-in files describe what a corpus page should do, and until #977
/// nothing compared them to each other.
///
/// <list type="bullet">
///   <item><c>test-pdfs/rendering-contracts/**</c> pins <c>ExpectedRawStatus</c>
///   per page, and <c>render-quality-scan</c> grades against it.</item>
///   <item><c>tests/corpus-expectations*.tsv</c> pins the same status for page
///   1, and the corpus scan in <c>run-full-suite.sh</c> grades against
///   that.</item>
/// </list>
///
/// A page could therefore be green in one and years stale in the other
/// indefinitely: three of the annotation pages #932 re-pinned had contracts
/// stuck at PASS_ONE while the manifest said MISSING_CONTENT, and had been that
/// way since the contracts were generated. Most contracts are auto-inferred
/// baselines whose own QualityReason says "promote to a reviewed contract when
/// triaged" — so the drift is unsurprising, and it is also undetectable by
/// reading, because a stale auto-baseline looks exactly like a reviewed one.
///
/// This is a file comparison, no rendering: it costs milliseconds and needs no
/// corpus on disk (both inputs are checked in). It reuses the two production
/// loaders rather than re-parsing either format — a second parser of the same
/// files, drifting quietly from the first, is the disease being treated here,
/// not the cure.
/// </summary>
partial class Program
{
    /// <summary>
    /// Corpus directory name (the first path segment of a contract's Path) to
    /// the expectation manifest that grades that corpus.
    ///
    /// Mirrors <c>_CORPUS_SPECS</c> in <c>scripts/run-full-suite.sh</c>. A
    /// corpus with no manifest — the contract tree also covers federal, ghent,
    /// poppler, altona, smoke and others that no corpus scan grades — simply
    /// has nothing to be compared against, which is reported as a count rather
    /// than treated as an error.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CorpusExpectationManifests =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pdfjs"] = "tests/corpus-expectations.tsv",
            ["verapdf-corpus"] = "tests/corpus-expectations-verapdf.tsv",
            ["isartor"] = "tests/corpus-expectations-isartor.tsv",
            ["pdfium"] = "tests/corpus-expectations-pdfium.tsv",
        };

    internal sealed record ContractManifestDisagreement(
        string Path,
        int PageNumber,
        string ContractStatus,
        string ManifestStatus,
        string ContractFile)
    {
        public override string ToString()
            => $"{Path}\tpage {PageNumber}\tcontract={ContractStatus}\tmanifest={ManifestStatus}\t{ContractFile}";
    }

    internal sealed record ContractManifestComparison(
        int ComparedPages,
        int PagesWithoutManifestRow,
        IReadOnlyList<ContractManifestDisagreement> Disagreements);

    /// <summary>
    /// Compare every contract page that has a corresponding manifest row.
    ///
    /// A wildcard on either side is compatible with anything: the manifest's
    /// <c>*</c> exists for load-dependent pages (reference-renderer timeouts),
    /// and a contract with no ExpectedRawStatus is not making a claim about the
    /// raw status at all.
    /// </summary>
    internal static ContractManifestComparison CompareContractsWithExpectationManifests(
        string contractsDir,
        string repoRoot)
    {
        var contracts = RenderingQualityContractSet.Load(contractsDir);
        var manifests = new Dictionary<string, IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome>?>(StringComparer.Ordinal);

        var compared = 0;
        var withoutRow = 0;
        var disagreements = new List<ContractManifestDisagreement>();

        foreach (var (key, match) in contracts.EnumeratePages())
        {
            var slash = key.Path.IndexOf('/', StringComparison.Ordinal);
            var corpus = slash < 0 ? key.Path : key.Path[..slash];
            var corpusRelative = slash < 0 ? key.Path : key.Path[(slash + 1)..];

            if (!manifests.TryGetValue(corpus, out var manifest))
            {
                manifest = CorpusExpectationManifests.TryGetValue(corpus, out var manifestPath)
                    ? LoadCorpusExpectationManifest(new FileInfo(System.IO.Path.Combine(repoRoot, manifestPath)))
                    : null;
                manifests[corpus] = manifest;
            }

            if (manifest is null ||
                !TryGetCorpusExpectation(manifest, corpusRelative, key.PageNumber, out var expectation))
            {
                withoutRow++;
                continue;
            }

            compared++;
            var contractStatus = string.IsNullOrWhiteSpace(match.Page.ExpectedRawStatus)
                ? "*"
                : match.Page.ExpectedRawStatus!;
            var manifestStatus = expectation.ExpectedStatus;

            if (contractStatus == "*" || manifestStatus == "*" ||
                string.Equals(contractStatus, manifestStatus, StringComparison.Ordinal))
            {
                continue;
            }

            disagreements.Add(new ContractManifestDisagreement(
                key.Path,
                key.PageNumber,
                contractStatus,
                manifestStatus,
                match.Contract.ContractFile ?? key.Path));
        }

        return new ContractManifestComparison(
            compared,
            withoutRow,
            disagreements
                .OrderBy(d => d.Path, StringComparer.Ordinal)
                .ThenBy(d => d.PageNumber)
                .ToArray());
    }

    static Command CreateContractManifestAgreementCommand()
    {
        var contractsOption = new Option<DirectoryInfo>("--contracts")
        {
            Description = "Directory containing per-PDF rendering quality contract JSON files",
            DefaultValueFactory = _ => new DirectoryInfo("test-pdfs/rendering-contracts"),
        };
        var repoRootOption = new Option<DirectoryInfo>("--repo-root")
        {
            Description = "Repository root holding tests/corpus-expectations*.tsv",
            DefaultValueFactory = _ => new DirectoryInfo("."),
        };

        var command = new Command(
            "contract-manifest-agreement",
            "Check that rendering quality contracts and corpus expectation manifests pin the same status")
        {
            contractsOption,
            repoRootOption,
        };

        command.SetAction(parseResult =>
        {
            var contracts = parseResult.GetValue(contractsOption)!;
            var repoRoot = parseResult.GetValue(repoRootOption)!;

            if (!contracts.Exists)
            {
                Console.Error.WriteLine($"Rendering quality contracts not found: {contracts.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var comparison = CompareContractsWithExpectationManifests(contracts.FullName, repoRoot.FullName);

                // Every disagreement, not the first one: the point is to work
                // through a list, not to fix one row per run.
                foreach (var disagreement in comparison.Disagreements)
                    Console.Out.WriteLine(disagreement.ToString());

                Console.Out.WriteLine(
                    $"contract pages comparable to a manifest row: {comparison.ComparedPages}");
                Console.Out.WriteLine(
                    $"  disagree with the manifest:                {comparison.Disagreements.Count}");
                Console.Out.WriteLine(
                    $"contract pages with no manifest row:         {comparison.PagesWithoutManifestRow}");

                Environment.ExitCode = comparison.Disagreements.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }
}
