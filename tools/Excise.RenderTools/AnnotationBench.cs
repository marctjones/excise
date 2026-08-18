using System.CommandLine;
using System.Globalization;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;

namespace Excise.RenderTools;

/// <summary>
/// ANNOTATION BENCH — scores excise's annotation rendering against every
/// independent renderer that draws annotations, per annotation, across real and
/// synthetic documents.
///
/// <para>This is a BENCH, not a gate. It reports a score; it does not pass or
/// fail. The gates (AppearanceStreamDifferentialTests,
/// CorpusAppearanceStreamTests) pin a handful of cases so a regression is
/// caught; this answers the different question of <b>how well are we doing
/// overall, and where are we worst</b>.</para>
///
/// <para><b>Group A and Group B are scored separately and never pooled</b>
/// (#1053). With an <c>/AP</c>, §12.5.5 says the appearance stream shall be
/// used, so disagreement is a defect. Without one, a viewer may synthesize and
/// the spec declines to say what it looks like — measured unanimity across five
/// engines on the /AP-absent cases is only 57.8%, so a low Group B score is
/// mostly a statement about the question, not about excise. Averaging the two
/// would produce a single number that means nothing.</para>
/// </summary>
partial class Program
{
    private const int BenchTile = 10;

    private sealed record BenchRow(
        string File, int Page, int Index, string Subtype, bool HasAp,
        int Voters, int MajorityTiles, int OurTiles, int Missing, int Extra, string Verdict);

    static Command CreateAnnotationBenchCommand()
    {
        var corpusOption = new Option<string[]>("--corpus")
        {
            Description = "Directory of PDFs to bench (repeatable)",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => new[] { "test-pdfs/pdfjs" },
        };
        var outOption = new Option<FileInfo?>("--out")
        {
            Description = "TSV report path (per-annotation rows)",
        };
        var dpiOption = new Option<int>("--dpi")
        {
            // 150, NOT 72, and this is load-bearing rather than a preference.
            //
            // At 72 dpi a 10px tile is 10pt of page, body text is a few pixels
            // tall and heavily antialiased, and the 5%-of-tile ink threshold
            // sits right on the edge — so tiles flip between engines for
            // rasterisation reasons alone. Measured on 160F-2019.pdf, whose 60
            // widgets all carry an /AP:
            //
            //     72 dpi  -> 28.3% agreement, 43 divergent
            //    150 dpi  -> 100%,             0 divergent
            //    300 dpi  -> 100%,             0 divergent
            //
            // The 72 dpi run read as a large Widget defect. There was none. The
            // renders are visually identical and all four engines fill those
            // fields with the same colour, (219,229,239) against (220,230,240).
            Description = "Render DPI. Below ~150 the measurement is dominated by text antialiasing, "
                        + "not by annotation ink — see the note in the source.",
            DefaultValueFactory = _ => 150,
        };
        var maxFilesOption = new Option<int>("--max-files")
        {
            Description = "Stop after this many annotated documents (0 = all)",
            DefaultValueFactory = _ => 0,
        };
        var oraclesOption = new Option<string>("--oracles")
        {
            // Which engines legitimately VOTE depends on the subtype. A
            // page-raster API that renders no form fields is not disagreeing
            // about a Widget, it is abstaining, and counting an abstention as a
            // "no" is the defect #1007 fixed in the corpus gate.
            Description = "Comma-separated oracle names, or 'primaries' (mutool,pdftocairo,ghostscript) or 'all'",
            DefaultValueFactory = _ => "all",
        };
        var maxPagesOption = new Option<int>("--max-pages-per-pdf")
        {
            Description = "Only bench the first N annotated pages of a document",
            DefaultValueFactory = _ => 3,
        };

        var command = new Command("annotation-bench",
            "Score excise's annotation rendering against the reference renderers, per annotation")
        {
            corpusOption, outOption, dpiOption, maxFilesOption, maxPagesOption, oraclesOption,
        };

        command.SetAction(parseResult =>
        {
            var corpora = parseResult.GetValue(corpusOption)!;
            var outFile = parseResult.GetValue(outOption);
            var dpi = parseResult.GetValue(dpiOption);
            var maxFiles = parseResult.GetValue(maxFilesOption);
            var maxPages = parseResult.GetValue(maxPagesOption);
            var oracles = parseResult.GetValue(oraclesOption)!;
            Environment.ExitCode = RunAnnotationBench(corpora, outFile, dpi, maxFiles, maxPages, oracles);
        });

        return command;
    }

    private static int RunAnnotationBench(
        string[] corpora, FileInfo? outFile, int dpi, int maxFiles, int maxPagesPerPdf, string oracleSpec)
    {
        var available = AvailableAnnotationOracles();
        var wanted = oracleSpec.Trim().ToLowerInvariant() switch
        {
            "all" => null,
            "primaries" => new[] { "mutool", "pdftocairo", "ghostscript" },
            _ => oracleSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        if (wanted != null) available = available.Where(o => wanted.Contains(o)).ToList();
        Console.WriteLine($"oracles answering: {string.Join(", ", available)}");
        if (available.Count < 3)
        {
            // Below three a "majority" is unanimity, so a single dissenting
            // renderer erases the reference and every row reads NO_MAJORITY
            // (#976). A bench that cannot form an opinion should say so rather
            // than print zeros.
            Console.Error.WriteLine(
                $"FAIL: need at least 3 annotation-capable renderers, found {available.Count}. " +
                "Install mutool/pdftocairo/ghostscript, or set EXCISE_PDFIUM_TEST / EXCISE_PDFBOX_JAR.");
            return 2;
        }

        var rows = new List<BenchRow>();
        var files = 0;

        foreach (var corpus in corpora)
        {
            if (!Directory.Exists(corpus)) { Console.Error.WriteLine($"skip: {corpus} not found"); continue; }

            foreach (var path in Directory.EnumerateFiles(corpus, "*.pdf", SearchOption.AllDirectories)
                                          .OrderBy(x => x, StringComparer.Ordinal))
            {
                if (maxFiles > 0 && files >= maxFiles) break;

                List<(int Page, List<PdfAnnotation> Annots)> annotated;
                try { annotated = AnnotatedPages(path, maxPagesPerPdf); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Cannot open is a parser question, not an annotation one.
                    continue;
                }
                if (annotated.Count == 0) continue;

                files++;
                foreach (var (pageNo, annots) in annotated)
                    rows.AddRange(BenchPage(path, pageNo, annots, dpi, available));

                if (files % 25 == 0) Console.WriteLine($"  ... {files} documents, {rows.Count} annotations");
            }
        }

        Report(rows, files, outFile);
        return 0;
    }

    private static List<string> AvailableAnnotationOracles()
    {
        var list = new List<string>();
        if (MutoolReferenceRenderer.IsAvailable) list.Add("mutool");
        if (PdftocairoReferenceRenderer.IsAvailable) list.Add("pdftocairo");
        if (GhostscriptReferenceRenderer.IsAvailable) list.Add("ghostscript");
        if (PdfiumNativeReferenceRenderer.IsAvailable) list.Add("pdfium");
        if (PdfBoxReferenceRenderer.IsAvailable) list.Add("pdfbox");
        return list;
    }

    private static SKBitmap? RenderAnnotationOracle(string name, string path, int page, int dpi) => name switch
    {
        "mutool" => MutoolReferenceRenderer.RenderPage(path, page, dpi),
        "pdftocairo" => PdftocairoReferenceRenderer.RenderPage(path, page, dpi),
        "ghostscript" => GhostscriptReferenceRenderer.RenderPage(path, page, dpi),
        // Without FPDF_ANNOT pdfium inks nothing on an annotation, and that
        // zero is not a vote (#1007/#1020).
        "pdfium" => PdfiumNativeReferenceRenderer.RenderPage(path, page, dpi, null, renderAnnotations: true),
        "pdfbox" => PdfBoxReferenceRenderer.RenderPage(path, page, dpi),
        _ => throw new ArgumentException($"unknown oracle '{name}'"),
    };

    private static List<(int Page, List<PdfAnnotation> Annots)> AnnotatedPages(string path, int maxPages)
    {
        var result = new List<(int, List<PdfAnnotation>)>();
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        for (var p = 1; p <= doc.PageCount && result.Count < maxPages; p++)
        {
            var annots = doc.GetPage(p).GetAnnotations()
                            .Where(a => a.Subtype != PdfAnnotationSubtype.Popup)
                            .ToList();
            if (annots.Count > 0) result.Add((p, annots));
        }
        return result;
    }

    private static IEnumerable<BenchRow> BenchPage(
        string path, int pageNo, List<PdfAnnotation> annots, int dpi, List<string> oracleNames)
    {
        var rows = new List<BenchRow>();

        var oracleBitmaps = new List<SKBitmap>();
        foreach (var name in oracleNames)
        {
            SKBitmap? b = null;
            try { b = RenderAnnotationOracle(name, path, pageNo, dpi); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (b != null) oracleBitmaps.Add(b);
        }

        SKBitmap? ours = null;
        double pageHeight;
        try
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(path));
            var page = doc.GetPage(pageNo);
            pageHeight = page.CropBox.Height;
            ours = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = dpi, RenderAnnotations = true });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var b in oracleBitmaps) b.Dispose();
            return rows;
        }

        try
        {
            if (ours == null || oracleBitmaps.Count < 3) return rows;

            var scale = dpi / 72.0;
            for (var i = 0; i < annots.Count; i++)
            {
                var a = annots[i];
                var window = RectWindow(a.Rect, pageHeight, scale);
                if (window.Count == 0) continue;

                var oracleTiles = oracleBitmaps.Select(b => InkedIn(b, window)).ToList();
                var majority = MajorityOf(oracleTiles);
                var mine = InkedIn(ours, window);

                var missing = majority.Except(mine).Count();
                var extra = mine.Except(majority).Count();

                var verdict =
                    majority.Count == 0 && mine.Count == 0 ? "AGREE_BLANK"
                    : majority.Count == 0 ? "EXTRA_ONLY"
                    : mine.Count == 0 ? "MISSING_ALL"
                    : missing == 0 && extra == 0 ? "AGREE_EXACT"
                    : missing <= Math.Max(1, majority.Count / 5) && extra <= Math.Max(1, majority.Count / 5) ? "AGREE_CLOSE"
                    : "DIVERGENT";

                rows.Add(new BenchRow(
                    Path.GetFileName(path), pageNo, i,
                    a.Subtype.ToString(), HasAppearance(a),
                    oracleBitmaps.Count, majority.Count, mine.Count, missing, extra, verdict));
            }
        }
        finally
        {
            ours?.Dispose();
            foreach (var b in oracleBitmaps) b.Dispose();
        }

        return rows;
    }

    private static bool HasAppearance(PdfAnnotation a) => a.RawDictionary.GetOptional("AP") != null;

    private static HashSet<(int X, int Y)> RectWindow(PdfRectangle r, double pageHeight, double scale)
    {
        var x0 = Math.Min(r.Left, r.Right) * scale;
        var x1 = Math.Max(r.Left, r.Right) * scale;
        var yTop = (pageHeight - Math.Max(r.Bottom, r.Top)) * scale;
        var yBot = (pageHeight - Math.Min(r.Bottom, r.Top)) * scale;

        var w = new HashSet<(int, int)>();
        for (var ty = (int)(yTop / BenchTile) - 1; ty <= (int)(yBot / BenchTile) + 1; ty++)
            for (var tx = (int)(x0 / BenchTile) - 1; tx <= (int)(x1 / BenchTile) + 1; tx++)
                if (tx >= 0 && ty >= 0) w.Add((tx, ty));
        return w;
    }

    private static HashSet<(int X, int Y)> InkedIn(SKBitmap bmp, HashSet<(int X, int Y)> window)
    {
        var tiles = new HashSet<(int, int)>();
        foreach (var (tx, ty) in window)
        {
            var inked = 0;
            for (var y = ty * BenchTile; y < Math.Min((ty + 1) * BenchTile, bmp.Height); y++)
                for (var x = tx * BenchTile; x < Math.Min((tx + 1) * BenchTile, bmp.Width); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Alpha > 128 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) inked++;
                }
            if (inked > BenchTile * BenchTile / 20) tiles.Add((tx, ty));
        }
        return tiles;
    }

    private static HashSet<(int X, int Y)> MajorityOf(List<HashSet<(int X, int Y)>> sets)
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (var s in sets)
            foreach (var t in s)
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.Where(kv => kv.Value * 2 > sets.Count).Select(kv => kv.Key).ToHashSet();
    }

    private static void Report(List<BenchRow> rows, int files, FileInfo? outFile)
    {
        if (outFile != null)
        {
            var sb = new StringBuilder("file\tpage\tindex\tsubtype\thasAP\tgroup\tvoters\tmajorityTiles\tourTiles\tmissing\textra\tverdict\n");
            foreach (var r in rows)
                sb.Append(CultureInfo.InvariantCulture,
                    $"{r.File}\t{r.Page}\t{r.Index}\t{r.Subtype}\t{r.HasAp}\t{(r.HasAp ? "A" : "B")}\t{r.Voters}\t{r.MajorityTiles}\t{r.OurTiles}\t{r.Missing}\t{r.Extra}\t{r.Verdict}\n");
            Directory.CreateDirectory(outFile.DirectoryName ?? ".");
            File.WriteAllText(outFile.FullName, sb.ToString());
            Console.WriteLine($"\nwrote {rows.Count} rows to {outFile.FullName}");
        }

        Console.WriteLine($"\n=== annotation bench: {files} documents, {rows.Count} annotations ===");
        foreach (var group in new[] { true, false })
        {
            var g = rows.Where(r => r.HasAp == group).ToList();
            if (g.Count == 0) continue;

            Console.WriteLine(group
                ? $"\nGROUP A — has /AP ({g.Count} annotations). §12.5.5: the appearance stream SHALL be used,"
                  + "\n          so a disagreement here is a DEFECT."
                : $"\nGROUP B — no /AP ({g.Count} annotations). The spec says a viewer MAY synthesize and"
                  + "\n          declines to say what it looks like, so disagreement is mostly not excise's fault."
                  + "\n          Reported for visibility, NOT as a score to optimise.");

            // ⚠️ 'docs' and 'badDocs' are load-bearing, not decoration. A
            // single form with 74 fields produced a 41.9% Widget rate on the
            // first run of this bench and looked like a general defect; every
            // divergent row came from ONE document. A per-annotation rate alone
            // says more about which files are in the corpus than about excise.
            Console.WriteLine($"\n  {"subtype",-16}{"n",5}{"docs",6}{"agree",8}{"close",7}{"diverge",9}{"missing",9}{"extra",7}{"badDocs",9}   agreement");
            foreach (var bySub in g.GroupBy(r => r.Subtype).OrderByDescending(x => x.Count()))
            {
                var n = bySub.Count();
                int C(string v) => bySub.Count(r => r.Verdict == v);
                var agree = C("AGREE_EXACT") + C("AGREE_BLANK");
                var close = C("AGREE_CLOSE");
                var ok = agree + close;
                var docs = bySub.Select(r => r.File).Distinct().Count();
                var badDocs = bySub.Where(r => !r.Verdict.StartsWith("AGREE", StringComparison.Ordinal))
                                   .Select(r => r.File).Distinct().Count();
                var bar = new string('#', (int)Math.Round(10.0 * ok / n));
                Console.WriteLine($"  {bySub.Key,-16}{n,5}{docs,6}{agree,8}{close,7}{C("DIVERGENT"),9}{C("MISSING_ALL"),9}{C("EXTRA_ONLY"),7}{badDocs,9}   {bar,-10} {100.0 * ok / n,5:F1}%");
            }

            var total = g.Count;
            var totalOk = g.Count(r => r.Verdict.StartsWith("AGREE", StringComparison.Ordinal));
            var micro = 100.0 * totalOk / total;

            // MACRO too, because the micro average is whatever the corpus is
            // made of. Group B on pdf.js is 33,105 annotations of which 32,993
            // are Links, so a per-annotation total reads 99.9% while several
            // subtypes sit at zero. One number per subtype, averaged, is the
            // one that notices a subtype nobody renders.
            var macro = g.GroupBy(r => r.Subtype)
                         .Average(x => 100.0 * x.Count(r => r.Verdict.StartsWith("AGREE", StringComparison.Ordinal)) / x.Count());

            Console.WriteLine($"  {"TOTAL",-16}{total,5}{g.Select(r => r.File).Distinct().Count(),6}" +
                              $"{"",8}{"",7}{"",9}{"",9}{"",7}{"",9}   {micro,5:F1}%  per annotation");
            Console.WriteLine($"  {"",-16}{"",5}{"",6}{"",8}{"",7}{"",9}{"",9}{"",7}{"",9}   {macro,5:F1}%  averaged over subtypes");
        }
    }
}
