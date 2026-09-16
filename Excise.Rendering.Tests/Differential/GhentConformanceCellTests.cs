using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Per-CELL conformance gate for the Ghent patch pages that print their own
/// failure indicator (#1515), driven by
/// <c>tests/ghent-conformance-cells.json</c> and judged against mutool,
/// Ghostscript and pdftocairo.
///
/// <para><b>The mechanism #1515 is about, stated precisely.</b> Every number
/// these pages have ever been gated on is an AGGREGATE OVER AREA:</para>
/// <list type="bullet">
///   <item>the corpus scan's page-wide <c>diffFraction</c>/<c>MAE</c> — GWG161's
///     contract pins 1.27% differing pixels, and a 22.7 x 22.7 pt cell is 0.7%
///     of a 255 x 142 pt page, so a fully painted cell fits inside the budget
///     whole;</item>
///   <item><c>SkiaRendererTests</c>'s cell MEAN RGB;</item>
///   <item><see cref="GhentSoftMaskVectorCellTests"/>'s per-block luminance and
///     chroma.</item>
/// </list>
///
/// <para>The failure these pages announce is STRUCTURAL, not average. GWG161
/// says so in 4 pt type: <i>"If an 'X' appears, rendering of Knockout
/// Transparency Groups (a transparency effect) is not performed
/// correctly."</i> A mean absorbs an X inside a cell exactly as it absorbs a
/// cell inside a page, so NO threshold on a mean can separate them — widening
/// or tightening one is the wrong move, and #1515's "the tolerance is too wide"
/// framing understates the problem.</para>
///
/// <para><b>Measured, not argued.</b> With #1514's defect present the
/// Opacity (0%) cell read mean RGB (0, 160.3, 133.1) where mutool reads
/// (0, 142, 191) and Ghostscript (0, 152, 196). Reduced to the metric
/// <see cref="GhentSoftMaskVectorCellTests"/> uses, that is luminance 109.3 /
/// chroma 160.3 against an oracle consensus of 108.4 / 193.5 — INSIDE that
/// gate's own +/-40 luminance and +/-60 chroma tolerances. So porting the
/// existing per-cell convention verbatim would have been blind too. The fix
/// has to add a DIMENSION, not move a threshold.</para>
///
/// <para><b>Two measures per cell.</b></para>
/// <list type="number">
///   <item><b>Colour</b> — per-block luminance and chroma, the measure #1395
///     proved on GWG168/169. Catches FLAT-BUT-WRONG: a black square where a
///     cyan drop shadow belongs, a white one for a yellow one, grey for a green
///     bevel.</item>
///   <item><b>Structure</b> — the fraction of the cell's interior that departs
///     from the cell's OWN median colour. Catches AN X APPEARS. Each engine is
///     read against its own median, so the DeviceCMYK-preview differences these
///     PDF/X-4 pages provoke (the contracts record
///     <c>ReferenceSituation: REFS_DISAGREE</c> for exactly this reason) cancel
///     on both sides of the comparison instead of having to be tolerated. Same
///     discipline #1514's own synthetic test used: read each renderer against
///     its own backdrop pixel, because a 48-level gap there is colour
///     management, not compositing.</item>
/// </list>
///
/// <para><b>No hand-picked floors.</b> Both measures are judged against the
/// oracle consensus, and a block or cell the oracles do not agree on is not
/// judged at all. That is deliberate: the sibling assertions in
/// <c>SkiaRendererTests.RenderPage_GhentDeviceCmyk{Knockout,Isolated}Group_*</c>
/// are uncorroborated magic numbers (<c>Blue &gt; 185</c>,
/// <c>CountRedPixels &gt; 120</c>) — the #1512 failure class, a floor no
/// reference renderer vouches for.</para>
///
/// <para><b>Passing on nothing is a failure.</b> A cell must have at least half
/// its colour blocks judgeable AND a judged structure measure, or it fails
/// rather than reporting green — <see cref="GhentSoftMaskVectorCellTests"/>'s
/// rule, kept.</para>
/// </summary>
public class GhentConformanceCellTests
{
    private const string ManifestRelativePath = "tests/ghent-conformance-cells.json";
    private const string ContractDirRelativePath = "test-pdfs/rendering-contracts/ghent";

    /// <summary>Blocks per axis inside a cell, as GWG168/169's gate uses.</summary>
    private const int Grid = 4;

    /// <summary>
    /// Colour tolerances carried over unchanged from
    /// <see cref="GhentSoftMaskVectorCellTests"/>, where #1395 measured them
    /// against mutool and Ghostscript on the same suite at the same DPI. They
    /// are wide because the three engines preview DeviceCMYK through different
    /// conversions; covering what that wideness lets through is the structure
    /// measure's job, not a tighter number's.
    /// </summary>
    private const double LuminanceTolerance = 40;
    private const double ChromaTolerance = 60;

    /// <summary>
    /// A pixel "departs" from its cell's median when any channel differs by
    /// more than this. Above per-engine antialiasing fringe and far below the
    /// signal: an X painted in a Ghent swatch departs by 100+ levels in at
    /// least one channel (green ink <c>1 0 1 0 k</c> has blue 0 against a
    /// cyan-ish <c>1 .16 .16 0 k</c> backdrop).
    /// </summary>
    private const int StructureDepartureDelta = 24;

    /// <summary>
    /// How far two oracles may sit apart on the structure measure and still
    /// form a consensus.
    ///
    /// ⚠️ PINNED FROM THE ORACLE SPREAD, NEVER FROM excise — re-derive it by
    /// running <see cref="MeasureEveryCell_PrintsTheOracleSpread"/>
    /// (<c>EXCISE_CONFORMANCE_CELL_DUMP=1</c>) over every cell x every engine
    /// and taking a margin above the widest gap between two AGREEING oracles.
    /// Never raise it to make an excise render pass.
    ///
    /// Measured over all 48 cells at 144 dpi: mutool and Ghostscript agree
    /// exactly (0.0000 both) on every GWG160 and GWG161 cell. GWG162, the
    /// isolated-group page, is where they genuinely differ — its widest
    /// agreeing-pair gaps are 0.0848 (Exclusion, mutool 0.0848 vs Ghostscript
    /// 0.0000) and 0.0592 (Opacity (0%), Ghostscript 0.0072 vs pdftocairo
    /// 0.0664) — so anything below ~0.09 leaves those cells with no 2-of-3
    /// consensus and the gate would hard-fail on pages excise renders fine.
    ///
    /// ⚠️ 0.10 is PROVISIONAL: those figures come from a phase-1 prototype
    /// that invoked the tools directly, and the repo's Ghostscript oracle adds
    /// <c>-dTextAlphaBits=4 -dGraphicsAlphaBits=4 -dUseCropBox</c>, which the
    /// prototype did not. Re-derive from the C# dump before treating the
    /// number as measured (#1515).
    /// </summary>
    private const double StructureConsensusTolerance = 0.10;

    /// <summary>
    /// How far excise's departing-area fraction may sit from the oracle
    /// consensus. Separate from <see cref="StructureConsensusTolerance"/> on
    /// purpose: "the references agree with each other" and "excise agrees with
    /// them" are different questions, and conflating them is how one gets
    /// widened for the other's reason.
    ///
    /// The signal it has to separate, measured: with #1514's defect present
    /// excise painted an X across roughly a quarter to a half of the cell,
    /// while mutool and Ghostscript read 0.0000 in every GWG161 cell. So this
    /// is a 2.5x-to-5x margin, not a knife edge.
    /// </summary>
    private const double StructureTolerance = 0.10;

    private const int OracleTimeoutMs = 30_000;

    private static readonly object RenderLock = new();
    private static readonly Dictionary<string, SKBitmap?> Renders = new();

    private static readonly (string Name, Func<string, int, int, SKBitmap?> Render)[] Engines =
    {
        ("excise", (path, page, dpi) => RenderWithExcise(path, page, dpi)),
        ("mutool", (path, page, dpi) => MutoolReferenceRenderer.RenderPage(path, page, dpi, OracleTimeoutMs)),
        ("ghostscript", (path, page, dpi) => GhostscriptReferenceRenderer.RenderPage(path, page, dpi, OracleTimeoutMs)),
        ("pdftocairo", (path, page, dpi) => PdftocairoReferenceRenderer.RenderPage(path, page, dpi, OracleTimeoutMs)),
    };

    private readonly ITestOutputHelper _output;

    public GhentConformanceCellTests(ITestOutputHelper output) => _output = output;

    // ── 1. every cell must look the way the independent renderers render it ──

    [Theory]
    [MemberData(nameof(Cells))]
    public void Cell_MatchesIndependentRenderers(string fixture, string cell)
    {
        using var manifest = LoadManifest();
        var (fixtureNode, cellNode) = FindCell(manifest, fixture, cell);

        if (cellNode.TryGetProperty("KnownDefect", out var known))
        {
            // An accepted red states its issue, in code, where the skip
            // happens (#1172) — never by widening StructureTolerance until the
            // cell passes.
            Assert.Skip($"{fixture} '{cell}': known defect {known.GetString()}");
        }

        var dpi = manifest.RootElement.GetProperty("Dpi").GetInt32();
        var inset = manifest.RootElement.GetProperty("CellInsetPt").GetDouble();
        var pageHeight = fixtureNode.GetProperty("PageHeightPt").GetDouble();
        var pageNumber = fixtureNode.GetProperty("Page").GetInt32();
        var relativePath = fixtureNode.GetProperty("Path").GetString()!;
        var indicator = fixtureNode.GetProperty("FailureIndicator").GetString();
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.SkipUnless(File.Exists(path),
            $"Ghent {fixture} is not present locally at {relativePath} " +
            "(run scripts/download-standards-image-corpora.sh).");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var rect = ReadRect(cellNode);
        var measured = new List<(string Name, CellMeasure Measure)>();
        foreach (var engine in Engines)
        {
            var bitmap = Render(engine.Name, path, pageNumber, dpi);
            bitmap.Should().NotBeNull($"{engine.Name} rendered {fixture} page {pageNumber}");
            measured.Add((engine.Name, MeasureCell(bitmap!, rect, pageHeight, dpi, inset)));
        }

        var mine = measured[0].Measure;
        var oracles = measured.Skip(1).ToArray();
        var failures = new List<string>();

        // ── colour, per block ────────────────────────────────────────────────
        var judgedBlocks = 0;
        for (var i = 0; i < Grid * Grid; i++)
        {
            var luminances = oracles.Select(o => o.Measure.Blocks[i].Luminance).ToArray();
            var chromas = oracles.Select(o => o.Measure.Blocks[i].Chroma).ToArray();
            if (!TryConsensus(luminances, LuminanceTolerance, out var targetLuminance) ||
                !TryConsensus(chromas, ChromaTolerance, out var targetChroma))
            {
                continue;
            }

            judgedBlocks++;
            var block = mine.Blocks[i];
            if (Math.Abs(block.Luminance - targetLuminance) > LuminanceTolerance ||
                Math.Abs(block.Chroma - targetChroma) > ChromaTolerance)
            {
                failures.Add(
                    $"colour block ({i % Grid},{i / Grid}): excise L{block.Luminance:F0}/C{block.Chroma:F0} " +
                    $"vs consensus L{targetLuminance:F0}/C{targetChroma:F0}");
            }
        }

        judgedBlocks.Should().BeGreaterThanOrEqualTo(Grid * Grid / 2,
            "{0} '{1}': the independent renderers must agree on at least half the blocks for their "
            + "answer to be a target — a cell judged on nothing is not a pass.\n{2}",
            fixture, cell, Dump(measured));

        // ── structure: does a mark appear where the references show none? ────
        var spread = string.Join(", ",
            oracles.Select(o => $"{o.Name} {o.Measure.DepartingFraction:F4}"));
        TryConsensus(oracles.Select(o => o.Measure.DepartingFraction).ToArray(),
                StructureConsensusTolerance, out var targetStructure)
            .Should().BeTrue(
                "{0} '{1}': the independent renderers must agree on how much of this cell departs "
                + "from its own background before their answer can gate excise's. Measured: {2}",
                fixture, cell, spread);

        if (Math.Abs(mine.DepartingFraction - targetStructure) > StructureTolerance)
        {
            failures.Add(
                $"structure: {mine.DepartingFraction:P1} of the cell departs from its own background "
                + $"where the references agree on {targetStructure:P1} ({spread}). "
                + (mine.DepartingFraction > targetStructure
                    ? "excise is DRAWING something the references do not — on a page whose own caption "
                      + "says a visible mark here IS the failure"
                    : "excise is MISSING something the references draw"));
        }

        failures.Should().BeEmpty(
            "{0} '{1}' must render the way mutool, Ghostscript and pdftocairo render it (#1515). "
            + "The fixture's own verdict line: \"{2}\"\n{3}",
            fixture, cell, indicator, Dump(measured));
    }

    public static TheoryData<string, string> Cells()
    {
        var data = new TheoryData<string, string>();
        using var manifest = LoadManifest();
        foreach (var fixture in manifest.RootElement.GetProperty("Fixtures").EnumerateArray())
        {
            var name = fixture.GetProperty("Name").GetString()!;
            foreach (var cell in fixture.GetProperty("Cells").EnumerateArray())
                data.Add(name, cell.GetProperty("Name").GetString()!);
        }

        return data;
    }

    // ── 2. the manifest is well formed and inside the page ──────────────────

    [Fact]
    public void Manifest_IsWellFormed()
    {
        using var manifest = LoadManifest();
        var failures = new List<string>();
        var inset = manifest.RootElement.GetProperty("CellInsetPt").GetDouble();
        var dpi = manifest.RootElement.GetProperty("Dpi").GetInt32();

        dpi.Should().BeGreaterThan(72, "a cell must be more than a handful of pixels across");
        inset.Should().BePositive("the swatch's own stroke frame has to be excluded");

        foreach (var fixture in manifest.RootElement.GetProperty("Fixtures").EnumerateArray())
        {
            var name = fixture.GetProperty("Name").GetString()!;
            var pageHeight = fixture.GetProperty("PageHeightPt").GetDouble();
            var indicator = fixture.GetProperty("FailureIndicator").GetString();

            if (string.IsNullOrWhiteSpace(indicator))
                failures.Add($"{name}: FailureIndicator must quote the page's own verdict line");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rects = new List<(string Name, double[] Rect)>();
            foreach (var cell in fixture.GetProperty("Cells").EnumerateArray())
            {
                var cellName = cell.GetProperty("Name").GetString()!;
                if (!seen.Add(cellName))
                    failures.Add($"{name}: duplicate cell name '{cellName}'");

                var rect = ReadRect(cell);
                if (rect.Length != 4)
                    failures.Add($"{name}/{cellName}: Rect must be [llx, lly, urx, ury]");
                else if (rect[2] <= rect[0] || rect[3] <= rect[1])
                    failures.Add($"{name}/{cellName}: Rect is empty or inverted");
                else if (rect[1] < 0 || rect[3] > pageHeight)
                    failures.Add($"{name}/{cellName}: Rect falls outside the {pageHeight} pt page");
                else if (rect[2] - rect[0] <= 2 * inset || rect[3] - rect[1] <= 2 * inset)
                    failures.Add($"{name}/{cellName}: Rect is smaller than twice CellInsetPt");
                else
                    rects.Add((cellName, rect));
            }

            // Two cells that overlap would sample each other's artwork, which
            // is how a per-cell gate quietly stops being per-cell.
            for (var i = 0; i < rects.Count; i++)
            {
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var (a, b) = (rects[i], rects[j]);
                    if (a.Rect[0] < b.Rect[2] && b.Rect[0] < a.Rect[2] &&
                        a.Rect[1] < b.Rect[3] && b.Rect[1] < a.Rect[3])
                    {
                        failures.Add($"{name}: cells '{a.Name}' and '{b.Name}' overlap");
                    }
                }
            }
        }

        failures.Should().BeEmpty($"{ManifestRelativePath} must describe cells that exist on the page");
    }

    // ── 3. no contracted Ghent page may quietly stay ungated ────────────────

    /// <summary>
    /// The coverage guard. #1515's "a fix that only covers GWG161 leaves the
    /// class open", made mechanical: every Ghent rendering contract must either
    /// name cells in the manifest or say there why it has none. Measured when
    /// this was written: 8 of the 10 contracted Ghent pages print an explicit
    /// X-failure indicator, and 24 of the suite's 60 patch PDFs do — so the
    /// remainder is not a rounding error and has to stay visible instead of
    /// being forgotten.
    /// </summary>
    [Fact]
    public void EveryContractedGhentPage_IsGatedOrDeclaredUngated()
    {
        using var manifest = LoadManifest();
        var covered = manifest.RootElement.GetProperty("Fixtures").EnumerateArray()
            .Select(f => Path.GetFileName(f.GetProperty("Path").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in manifest.RootElement.GetProperty("NotCellGated").EnumerateArray())
        {
            declared[row.GetProperty("Name").GetString()!] =
                row.TryGetProperty("Reason", out var reason) ? reason.GetString() : null;
        }

        var contractDir = Path.Combine(
            FindRepoRoot(), ContractDirRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(contractDir).Should().BeTrue($"{ContractDirRelativePath} should exist");

        var contracted = new HashSet<string>(StringComparer.Ordinal);
        var contracts = Directory.EnumerateFiles(contractDir, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        contracts.Should().NotBeEmpty("zero contracts read is a broken guard, not a pass");

        var failures = new List<string>();
        foreach (var contract in contracts)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(contract));
            var pdf = Path.GetFileName(doc.RootElement.GetProperty("Path").GetString()!);
            contracted.Add(pdf);
            if (covered.Contains(pdf))
                continue;

            if (!declared.TryGetValue(pdf, out var reason))
            {
                failures.Add(
                    $"{pdf}: has a rendering contract but no per-cell expectations and no " +
                    $"'NotCellGated' entry in {ManifestRelativePath}. A Ghent patch page's contract " +
                    "is a whole-page tolerance, which cannot see a single wrong cell (#1515).");
            }
            else if (string.IsNullOrWhiteSpace(reason))
            {
                failures.Add($"{pdf}: 'NotCellGated' entry carries no Reason");
            }
        }

        // A stale exemption rots as quietly as a missing one.
        foreach (var name in declared.Keys)
        {
            if (covered.Contains(name))
                failures.Add($"{name}: listed in both Fixtures and NotCellGated");
            else if (!contracted.Contains(name))
                failures.Add($"{name}: exempted in NotCellGated but no Ghent contract covers it");
        }

        failures.Should().BeEmpty();
    }

    // ── 4. the measurement table, for pinning StructureTolerance ────────────

    /// <summary>
    /// Prints every cell's measures for all four engines. This is how
    /// <see cref="StructureTolerance"/> is derived from the ORACLE SPREAD
    /// instead of from excise's own output; it asserts nothing and is skipped
    /// unless asked for, with the reason stated in code (#1172).
    /// </summary>
    [Fact]
    public void MeasureEveryCell_PrintsTheOracleSpread()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EXCISE_CONFORMANCE_CELL_DUMP") == "1",
            "measurement reporter, not a gate; set EXCISE_CONFORMANCE_CELL_DUMP=1 to print the "
            + "per-cell oracle spread that StructureTolerance is pinned from");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        using var manifest = LoadManifest();
        var dpi = manifest.RootElement.GetProperty("Dpi").GetInt32();
        var inset = manifest.RootElement.GetProperty("CellInsetPt").GetDouble();
        _output.WriteLine("fixture  cell                 engine       departing   meanL  meanC");

        foreach (var fixture in manifest.RootElement.GetProperty("Fixtures").EnumerateArray())
        {
            var name = fixture.GetProperty("Name").GetString()!;
            var pageHeight = fixture.GetProperty("PageHeightPt").GetDouble();
            var pageNumber = fixture.GetProperty("Page").GetInt32();
            var path = Path.Combine(
                FindRepoRoot(),
                fixture.GetProperty("Path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                _output.WriteLine($"{name,-8} (fixture absent)");
                continue;
            }

            foreach (var cell in fixture.GetProperty("Cells").EnumerateArray())
            {
                var cellName = cell.GetProperty("Name").GetString()!;
                var rect = ReadRect(cell);
                foreach (var engine in Engines)
                {
                    var bitmap = Render(engine.Name, path, pageNumber, dpi);
                    if (bitmap == null)
                    {
                        _output.WriteLine($"{name,-8} {cellName,-20} {engine.Name,-12} (no render)");
                        continue;
                    }

                    var m = MeasureCell(bitmap, rect, pageHeight, dpi, inset);
                    _output.WriteLine(
                        $"{name,-8} {cellName,-20} {engine.Name,-12} {m.DepartingFraction,9:F4} "
                        + $"{m.Blocks.Average(b => b.Luminance),6:F1} {m.Blocks.Average(b => b.Chroma),6:F1}");
                }
            }
        }
    }

    // ── measurement ─────────────────────────────────────────────────────────

    private readonly record struct Block(double Luminance, double Chroma);

    private readonly record struct CellMeasure(Block[] Blocks, double DepartingFraction);

    /// <summary>
    /// A cell reduced to the numbers this gate judges: per-block
    /// luminance/chroma, and the fraction of the interior that departs from the
    /// cell's OWN median colour.
    ///
    /// The MEDIAN (not the mean) stands in for the background: an X can cover a
    /// quarter of the cell without moving the median off the backdrop, whereas
    /// it drags a mean with it — which is exactly why a mean cannot see one.
    /// </summary>
    private static CellMeasure MeasureCell(
        SKBitmap bitmap, double[] rect, double pageHeightPt, int dpi, double insetPt)
    {
        var scale = dpi / 72.0;
        var x0 = Math.Clamp((int)Math.Round((rect[0] + insetPt) * scale), 0, bitmap.Width);
        var x1 = Math.Clamp((int)Math.Round((rect[2] - insetPt) * scale), 0, bitmap.Width);
        // PDF points are bottom-left origin; raster row 0 is the page top.
        var y0 = Math.Clamp((int)Math.Round((pageHeightPt - rect[3] + insetPt) * scale), 0, bitmap.Height);
        var y1 = Math.Clamp((int)Math.Round((pageHeightPt - rect[1] - insetPt) * scale), 0, bitmap.Height);

        var blocks = new Block[Grid * Grid];
        for (var by = 0; by < Grid; by++)
        {
            for (var bx = 0; bx < Grid; bx++)
            {
                var bx0 = x0 + ((x1 - x0) * bx / Grid);
                var bx1 = x0 + ((x1 - x0) * (bx + 1) / Grid);
                var by0 = y0 + ((y1 - y0) * by / Grid);
                var by1 = y0 + ((y1 - y0) * (by + 1) / Grid);

                double r = 0, g = 0, b = 0;
                var n = 0;
                for (var y = by0; y < by1; y++)
                {
                    for (var x = bx0; x < bx1; x++)
                    {
                        var c = bitmap.GetPixel(x, y);
                        r += c.Red;
                        g += c.Green;
                        b += c.Blue;
                        n++;
                    }
                }

                if (n > 0)
                {
                    r /= n;
                    g /= n;
                    b /= n;
                }

                blocks[(by * Grid) + bx] = new Block(
                    (0.299 * r) + (0.587 * g) + (0.114 * b),
                    Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
            }
        }

        var count = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        if (count == 0)
            return new CellMeasure(blocks, 0);

        var reds = new byte[count];
        var greens = new byte[count];
        var blues = new byte[count];
        var i = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var c = bitmap.GetPixel(x, y);
                reds[i] = c.Red;
                greens[i] = c.Green;
                blues[i] = c.Blue;
                i++;
            }
        }

        var mr = Median(reds);
        var mg = Median(greens);
        var mb = Median(blues);
        var departing = 0;
        for (var p = 0; p < count; p++)
        {
            var delta = Math.Max(
                Math.Abs(reds[p] - mr),
                Math.Max(Math.Abs(greens[p] - mg), Math.Abs(blues[p] - mb)));
            if (delta > StructureDepartureDelta)
                departing++;
        }

        return new CellMeasure(blocks, departing / (double)count);
    }

    /// <summary>
    /// The target is the mean of the largest group of oracles that agree with
    /// each other inside <paramref name="tolerance"/>, and at least two must
    /// agree. A single reference is a quorum of one — the #932/#1512 failure,
    /// where a floor was calibrated on the one renderer that shares excise's
    /// defect.
    ///
    /// <para>This is what keeps the gate honest on GWG161 specifically:
    /// measured at 144 dpi, <b>pdftocairo paints an X in 15 of the 16
    /// cells</b> (departing fraction 0.47–0.97) where mutool and Ghostscript
    /// read 0.0000 — poppler fails the Ghent knockout patch outright, the same
    /// transparency outlier #1505/#1512 documented. Anchoring on one renderer
    /// would have made that the target.</para>
    ///
    /// <para>Ties are broken deterministically — tightest group first, then
    /// lowest mean — because two groups of equal size are otherwise resolved
    /// by the order the engines happen to be listed in, and a gate whose
    /// verdict depends on that is not reproducible.</para>
    ///
    /// <para>⚠️ Grouping is by distance from an ANCHOR, not transitive, so a
    /// winning group can span up to twice <paramref name="tolerance"/>.
    /// Measured instance: GWG162's Opacity (0%) groups
    /// {0.1203, 0.0072, 0.0664} around the pdftocairo anchor at a tolerance of
    /// 0.10, a span of 0.113. That is intended — a middle engine flanked by two
    /// others is a consensus of three — but it is why the group's SPREAD is
    /// reported in the failure message rather than assumed small.</para>
    /// </summary>
    internal static bool TryConsensus(double[] values, double tolerance, out double target)
    {
        target = 0;
        var best = (Count: 0, Spread: 0.0, Mean: 0.0);
        foreach (var anchor in values)
        {
            var count = 0;
            var sum = 0.0;
            var low = double.MaxValue;
            var high = double.MinValue;
            foreach (var other in values)
            {
                if (Math.Abs(other - anchor) > tolerance)
                    continue;
                count++;
                sum += other;
                low = Math.Min(low, other);
                high = Math.Max(high, other);
            }

            var candidate = (Count: count, Spread: high - low, Mean: sum / count);
            if (candidate.Count > best.Count ||
                (candidate.Count == best.Count && candidate.Spread < best.Spread) ||
                (candidate.Count == best.Count && candidate.Spread == best.Spread &&
                 candidate.Mean < best.Mean))
            {
                best = candidate;
            }
        }

        if (best.Count < 2)
            return false;

        target = best.Mean;
        return true;
    }

    private static int Median(byte[] values)
    {
        var sorted = (byte[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static string Dump(IReadOnlyList<(string Name, CellMeasure Measure)> measured)
    {
        var sb = new StringBuilder();
        foreach (var (name, m) in measured)
        {
            sb.AppendLine(
                $"  {name,-11} departing {m.DepartingFraction:F4}  blocks L/C: "
                + string.Join(" ", m.Blocks.Select(b => $"{b.Luminance:F0}/{b.Chroma:F0}")));
        }

        return sb.ToString();
    }

    private static SKBitmap? Render(string engine, string path, int page, int dpi)
    {
        var key = $"{engine}|{path}|{page}|{dpi}";
        lock (RenderLock)
        {
            if (Renders.TryGetValue(key, out var cached))
                return cached;

            var bitmap = Engines.First(e => e.Name == engine).Render(path, page, dpi);
            Renders[key] = bitmap;
            return bitmap;
        }
    }

    private static SKBitmap RenderWithExcise(string path, int page, int dpi)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(page),
            new RenderOptions { Dpi = dpi, BackgroundColor = SKColors.White });
    }

    private static double[] ReadRect(JsonElement cell)
        => cell.GetProperty("Rect").EnumerateArray().Select(v => v.GetDouble()).ToArray();

    private static (JsonElement Fixture, JsonElement Cell) FindCell(
        JsonDocument manifest, string fixture, string cell)
    {
        foreach (var f in manifest.RootElement.GetProperty("Fixtures").EnumerateArray())
        {
            if (f.GetProperty("Name").GetString() != fixture)
                continue;
            foreach (var c in f.GetProperty("Cells").EnumerateArray())
            {
                if (c.GetProperty("Name").GetString() == cell)
                    return (f, c);
            }
        }

        throw new InvalidOperationException(
            $"{ManifestRelativePath} has no cell '{cell}' in '{fixture}'");
    }

    private static JsonDocument LoadManifest()
    {
        var path = Path.Combine(
            FindRepoRoot(), ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{ManifestRelativePath} should exist");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find repository root (no excise.sln above the test base directory).");
    }
}
