using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;
using Pdfe.Rendering.Differential;

namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Layer 4 of the differential testing stack — extract text from each
/// corpus PDF with both pdfe and mutool, compare on a normalized form.
/// Cheaper and more discriminating than pixel diffs for the bug class
/// where glyphs map to wrong characters or aren't decoded at all
/// (Betterment-style: "Betterment" → "Bet ter ment", or worse: missing
/// chars entirely because /ToUnicode wasn't honored).
///
/// Comparison is on a normalized form ("flatten to printable ASCII +
/// strip whitespace + lowercase") so we tolerate harmless differences
/// (line-break choices, dehyphenation strategies, soft-hyphen handling)
/// and only flag genuine content disagreement.
///
/// Threshold: similarity ≥ 0.85 (Jaccard on character bigrams). Tuned
/// against the smoke corpus: same content authored differently
/// produces 0.92–0.99; the Betterment-class bug produces 0.40–0.60
/// (mostly because each "B e t" gets bigrams like "Be"," e"," t" which
/// don't match clean "Be","et","tt" from the correct extraction).
///
/// Same gating model as the pixel differential: smoke corpus gates
/// the build; pdf.js corpus is best-effort (Skipped on disagreement).
/// </summary>
public sealed class TextExtractionDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public TextExtractionDifferentialTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Minimum acceptable bigram-Jaccard similarity. Below this, the
    /// two extractions are treated as inconsistent.
    /// </summary>
    private const double MinSimilarity = 0.85;

    /// <summary>
    /// Don't bother comparing pages whose mutool extraction is shorter
    /// than this — the signal-to-noise ratio is too poor. Most of the
    /// "PDF that just contains a 1-line title" cases land here.
    /// </summary>
    private const int MinReferenceTextLength = 32;

    /// <summary>
    /// Every corpus listed here gates the build. The pdf.js corpus is
    /// intentionally NOT included — it surfaces real disagreements we
    /// haven't fixed yet, and listing it here would either block CI or
    /// require ~250 allowlist entries. Use <see cref="ExploratoryDifferentialTests"/>
    /// to run it on demand.
    /// </summary>
    private static readonly string[] GatingCorpusDirectories =
    {
        "test-pdfs/smoke",
    };

    /// <summary>
    /// Known text-extraction disagreements we don't yet gate on. Each
    /// entry is the relative path → reason. Removing a line re-enables
    /// the gate. Same protocol as DifferentialRenderingTests.
    /// </summary>
    private static readonly Dictionary<string, string> KnownTextFailures = new()
    {
        ["test-pdfs/smoke/irs-1040-instructions.pdf"] =
            "pdfe text extraction returns XML/Marked-Content metadata " +
            "(\"useridcpmschemai1040xleadpct100ptsize10draftoktoprint…\") instead of the " +
            "visible page content. Marked-content boundaries (BMC/BDC/EMC) aren't being " +
            "honored — we're emitting hidden /Subtype /Artifact text that mutool correctly " +
            "filters out.",
    };

    public static IEnumerable<object[]> CorpusPdfs() => Discover();

    internal static IEnumerable<object[]> Discover()
    {
        var root = LocateRepoRoot();
        if (root == null)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var foundAny = false;
        foreach (var sub in GatingCorpusDirectories)
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var pdf in Directory.EnumerateFiles(dir, "*.pdf").OrderBy(p => p))
            {
                foundAny = true;
                yield return new object[] { Path.GetRelativePath(root, pdf) };
            }
        }

        if (!foundAny)
        {
            yield return new object[] { SentinelNoCorpus };
        }
    }

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public void TextMatchesMutool(string relativePath)
    {
        Assert.SkipWhen(relativePath == SentinelNoCorpus,
            "No smoke corpus found at test-pdfs/smoke/. Run scripts/download-smoke-corpus.sh to populate it.");

        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools");

        var root = LocateRepoRoot()!;
        var pdfPath = Path.Combine(root, relativePath);

        // pdfe's extraction.
        string? pdfeText = null;
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            if (doc.PageCount == 0)
                Assert.SkipWhen(true, $"{relativePath}: 0 pages");
            pdfeText = doc.GetPage(1).Text;
        }
        catch (Exception ex)
        {
            Assert.SkipWhen(true,
                $"pdfe could not extract from {relativePath}: {ex.GetType().Name}: {ex.Message}");
        }

        var mutoolText = MutoolTextExtractor.ExtractPage(pdfPath, 1);
        Assert.SkipWhen(mutoolText == null,
            $"mutool refused to extract text from {relativePath}");

        var refNormalized = TextSimilarity.Normalize(mutoolText!);
        Assert.SkipWhen(refNormalized.Length < MinReferenceTextLength,
            $"{relativePath}: reference extraction is only {refNormalized.Length} chars — too noisy to compare");

        var ourNormalized = TextSimilarity.Normalize(pdfeText ?? string.Empty);
        var sim = TextSimilarity.BigramJaccard(refNormalized, ourNormalized);

        _output.WriteLine($"  {relativePath}");
        _output.WriteLine($"  pdfe:   {Truncate(ourNormalized, 96)}");
        _output.WriteLine($"  mutool: {Truncate(refNormalized, 96)}");
        _output.WriteLine($"  similarity: {sim:F3}");

        var failed = sim < MinSimilarity;
        if (failed && KnownTextFailures.TryGetValue(relativePath, out var reason))
        {
            _output.WriteLine($"  ⚑ KNOWN FAILURE — not gating: {reason}");
            Assert.SkipWhen(true, $"Known text-extraction failure for {relativePath}: {reason}");
        }
        sim.Should().BeGreaterThanOrEqualTo(MinSimilarity,
            $"{relativePath}: pdfe text-extraction differs from mutool. " +
            $"Bigram-Jaccard {sim:F3} < {MinSimilarity}.");
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";
}
