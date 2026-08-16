using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Drift gate for <c>tests/annotation-synthesis-policy.json</c> (#993).
///
/// SkiaRenderer.Annotations.cs decides what to draw for an annotation with no
/// <c>/AP /N</c> in scattered <c>if</c>s, and until now the oracle evidence
/// behind each decision lived in free-text comments that nothing re-measured.
/// Three shipped errors came out of that arrangement, all with one mechanism —
/// a decision made from a scalar that cannot represent the thing being decided:
///
///   * #885 drew a blue box on every widget, justified by "mutool 233 px,
///     pdftocairo 229, excise 0". Those pixels were the CHECK MARK. Neither
///     oracle drew any box. An ink COUNT cannot tell a tick from a border.
///   * #987's /Link border default was measured on a fixture that DID state
///     /BS /W 2, so the no-/Border-at-all case — the common one — was never
///     measured at all.
///   * #972's own first fix drew a CARET instead of a tick, with identical
///     pixel count and identical bbox. A BBOX cannot tell a tick from a caret.
///
/// So the policy is data now, and this gate re-measures it. Four checks:
///
///   1. <see cref="PolicyRows_AreWellFormed"/> — the table is internally
///      consistent: the majority arithmetic matches the evidence it was
///      computed from, a row that draws has a majority behind it, and a row
///      that contradicts its own verdict carries a deviation naming an issue.
///   2. <see cref="EveryRow_MatchesWhatExciseDraws"/> — renders each row's
///      fixture and asserts the row's SHAPE, not just its ink. This is the
///      direction that fails when the renderer changes and the table does not.
///   3. <see cref="EveryRowsEvidence_StillHoldsAgainstTheOracles"/> — re-runs
///      the oracles and fails when a recorded vote or bbox class no longer
///      holds. This is the direction that fails when the world changes under
///      a decision that was correct when it was taken.
///   4. <see cref="EverySynthesisSiteInTheRenderer_IsNamedByARow"/> — a new
///      synthesis method or a new subtype case cannot be added without a row.
///
/// The gate deliberately does NOT assert exact ink counts against the oracles:
/// tool versions drift, and a gate that goes red on a poppler upgrade is a gate
/// people stop reading. Votes and bbox classes are asserted exactly; counts are
/// held to a factor of two.
/// </summary>
public class AnnotationSynthesisPolicyGateTests
{
    private const string PolicyRelativePath = "tests/annotation-synthesis-policy.json";
    private const string RendererRelativePath = "Excise.Rendering/SkiaRenderer.Annotations.cs";

    // ── 1. the table is internally consistent ───────────────────────────────

    [Fact]
    public void PolicyRows_AreWellFormed()
    {
        var policy = LoadPolicy();
        var failures = new List<string>();
        var ids = new HashSet<string>();
        var shapes = policy.RootElement.GetProperty("shapeVocabulary")
            .EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var row in Rows(policy))
        {
            var id = row.GetProperty("id").GetString()!;
            if (!ids.Add(id)) failures.Add($"{id}: duplicate row id");

            var decision = row.GetProperty("decision").GetString();
            var shape = row.GetProperty("shape").GetString()!;

            if (decision is not ("draw" or "nothing"))
                failures.Add($"{id}: decision must be 'draw' or 'nothing', got '{decision}'");
            if (!shapes.Contains(shape))
                failures.Add($"{id}: shape '{shape}' is not in the shapeVocabulary");
            if ((decision == "nothing") != (shape == "none"))
                failures.Add($"{id}: decision '{decision}' and shape '{shape}' disagree — " +
                             "'nothing' means 'none' and nothing else does");

            // The majority is recomputed from the evidence rather than trusted.
            // Only engines marked counts:true vote — pdftocairo and pdftoppm are
            // one engine, and an oracle that structurally cannot draw
            // annotations (pdfium, flags=0) is an abstention, not a blank vote.
            int draws = 0, blank = 0, voters = 0;
            foreach (var e in row.GetProperty("evidence").EnumerateArray())
            {
                var vote = e.GetProperty("vote").GetString();
                var ink = e.GetProperty("inkedPixels").GetInt32();
                if ((ink > 0) != (vote == "draws"))
                    failures.Add($"{id}/{e.GetProperty("oracle")}: vote '{vote}' " +
                                 $"contradicts its own {ink} inked px");
                if (!e.GetProperty("counts").GetBoolean()) continue;
                voters++;
                if (vote == "draws") draws++; else blank++;
            }

            var majority = row.GetProperty("majority");
            if (majority.GetProperty("voters").GetInt32() != voters ||
                majority.GetProperty("draws").GetInt32() != draws ||
                majority.GetProperty("blank").GetInt32() != blank)
            {
                failures.Add($"{id}: recorded majority {Describe(majority)} does not match " +
                             $"the evidence (voters {voters}, draws {draws}, blank {blank})");
            }

            var verdict = draws * 2 > voters ? "draw" : (blank * 2 > voters ? "blank" : "split");
            if (majority.GetProperty("verdict").GetString() != verdict)
                failures.Add($"{id}: verdict should be '{verdict}' for {draws}/{voters} drawing");

            if (voters < 3)
                failures.Add($"{id}: only {voters} engines voted — a majority needs three (#976)");

            // The two rules that make this a policy and not a log.
            var agrees = (verdict == "draw") == (decision == "draw");
            var hasDeviation = row.TryGetProperty("deviation", out var deviation);
            if (!agrees && !hasDeviation)
            {
                failures.Add($"{id}: decision '{decision}' contradicts the majority verdict " +
                             $"'{verdict}' with no 'deviation' block. Where no majority draws, " +
                             "excise draws nothing; departing from that is allowed but must be " +
                             "stated, attributed to an issue and explained.");
            }
            if (agrees && hasDeviation)
                failures.Add($"{id}: has a 'deviation' block but agrees with its majority verdict");

            if (hasDeviation)
            {
                foreach (var key in new[] { "kind", "issue", "note" })
                    if (!deviation.TryGetProperty(key, out var v) || string.IsNullOrWhiteSpace(v.GetString()))
                        failures.Add($"{id}: deviation is missing '{key}'");
                if (deviation.TryGetProperty("issue", out var issue) &&
                    !Regex.IsMatch(issue.GetString() ?? "", @"^#\d+$"))
                    failures.Add($"{id}: deviation issue '{issue}' is not an issue number");
            }

            // A magnitude divergence is NOT a deviation: excise draws what the
            // majority draws, differently sized. It still needs an issue, so it
            // cannot be used to wave a real disagreement through.
            if (row.TryGetProperty("divergence", out var divergence))
            {
                if (decision != "draw")
                    failures.Add($"{id}: 'divergence' only makes sense on a drawing row");
                foreach (var key in new[] { "kind", "issue", "note" })
                    if (!divergence.TryGetProperty(key, out var v) || string.IsNullOrWhiteSpace(v.GetString()))
                        failures.Add($"{id}: divergence is missing '{key}'");
            }

            if (row.TryGetProperty("comparedWith", out var compared) &&
                !Rows(policy).Any(r => r.GetProperty("id").GetString() == compared.GetString()))
            {
                failures.Add($"{id}: comparedWith names '{compared}', which is not a row");
            }

            var fixture = row.GetProperty("fixture");
            if (!fixture.GetProperty("annotation").GetString()!.Contains("/F 4"))
            {
                failures.Add($"{id}: fixture is not printable (/F 4). Ghostscript renders only " +
                             "printable annotations, so a non-printable fixture records a " +
                             "structural abstention as a 'draws nothing' vote — the error that " +
                             "made #987 read 1-of-3.");
            }
        }

        failures.Should().BeEmpty(
            $"{PolicyRelativePath} must be internally consistent — see the 'rules' array in the file");
    }

    // ── 2. the renderer draws what the table says ───────────────────────────

    [Fact]
    public void EveryRow_MatchesWhatExciseDraws()
    {
        var policy = LoadPolicy();
        var failures = new List<string>();

        foreach (var row in Rows(policy))
        {
            var id = row.GetProperty("id").GetString()!;
            using var bitmap = RenderRow(row);
            var problem = DescribeShapeMismatch(policy, row, bitmap);
            if (problem != null) failures.Add($"{id}: {problem}");
        }

        failures.Should().BeEmpty(
            "every row's declared decision and SHAPE must be what excise actually draws for its " +
            $"fixture. Update the renderer or the row in {PolicyRelativePath} — a change to one " +
            "without the other is exactly what this gate exists to stop");
    }

    // ── 3. the evidence still holds ─────────────────────────────────────────

    [Fact]
    public void EveryRowsEvidence_StillHoldsAgainstTheOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var policy = LoadPolicy();
        var failures = new List<string>();

        foreach (var row in Rows(policy))
        {
            var id = row.GetProperty("id").GetString()!;
            var fixture = row.GetProperty("fixture");
            var dpi = fixture.GetProperty("dpi").GetInt32();
            var rect = DeviceRect(fixture);
            var path = WriteFixture(fixture);

            try
            {
                int draws = 0, blank = 0, voters = 0;
                foreach (var e in row.GetProperty("evidence").EnumerateArray())
                {
                    var oracle = e.GetProperty("oracle").GetString()!;
                    using var bitmap = RenderWithOracle(oracle, path, dpi);
                    if (bitmap == null) continue;   // tool not installed — no vote

                    var ink = InkedPixels(bitmap);
                    var bbox = InkBounds(bitmap);
                    var vote = ink > 0 ? "draws" : "blank";
                    var recordedVote = e.GetProperty("vote").GetString();
                    var recordedInk = e.GetProperty("inkedPixels").GetInt32();
                    var bboxClass = BBoxClass(bbox, rect);
                    var recordedClass = e.GetProperty("bboxClass").GetString();

                    if (vote != recordedVote)
                    {
                        failures.Add($"{id}/{oracle}: recorded '{recordedVote}' " +
                                     $"({recordedInk} px), measured '{vote}' ({ink} px, {Describe(bbox)})");
                    }
                    else if (bboxClass != recordedClass)
                    {
                        failures.Add($"{id}/{oracle}: recorded bbox class '{recordedClass}', " +
                                     $"measured '{bboxClass}' ({Describe(bbox)} vs rect {Describe(rect)})");
                    }
                    else if (ink > 25 && recordedInk > 25 &&
                             (ink > recordedInk * 2 || ink * 2 < recordedInk))
                    {
                        failures.Add($"{id}/{oracle}: recorded {recordedInk} inked px, measured " +
                                     $"{ink} — more than a factor of two apart, which is more " +
                                     "than tool-version drift");
                    }

                    if (e.GetProperty("counts").GetBoolean())
                    {
                        voters++;
                        if (vote == "draws") draws++; else blank++;
                    }
                }

                // Recompute the majority over the engines actually present. A
                // partial pool cannot overturn a recorded verdict, so it is only
                // checked when all the recorded voters ran.
                if (voters == row.GetProperty("majority").GetProperty("voters").GetInt32())
                {
                    var verdict = draws * 2 > voters ? "draw" : (blank * 2 > voters ? "blank" : "split");
                    if (verdict != row.GetProperty("majority").GetProperty("verdict").GetString())
                    {
                        failures.Add($"{id}: the live majority is now '{verdict}' " +
                                     $"({draws}/{voters} drawing), not " +
                                     $"'{row.GetProperty("majority").GetProperty("verdict")}'");
                    }
                }
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        failures.Should().BeEmpty(
            "the recorded oracle evidence must still be what the oracles do. A row whose evidence " +
            "no longer supports its decision is a decision that has quietly become uncorroborated");
    }

    // ── 4. no synthesis site without a row ──────────────────────────────────

    [Fact]
    public void EverySynthesisSiteInTheRenderer_IsNamedByARow()
    {
        var policy = LoadPolicy();
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), RendererRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        var sites = Regex.Matches(source,
                @"\bprivate\s+(?:static\s+)?void\s+(Render[A-Za-z]*Default|RenderTextFieldValue|DrawSynthesizedCheckMark)\s*\(")
            .Select(m => m.Groups[1].Value)
            .Where(n => n != "RenderDefaultAppearance")
            .ToHashSet();

        var claimed = Rows(policy)
            .Select(r => r.GetProperty("rendererSite").GetString()!)
            .Where(s => !s.StartsWith("(", StringComparison.Ordinal))
            .ToHashSet();

        sites.Except(claimed).Should().BeEmpty(
            "a synthesis method with no row in the policy table is a decision with no recorded " +
            "evidence — the arrangement #993 exists to end");
        claimed.Except(sites).Should().BeEmpty(
            $"every rendererSite named in {PolicyRelativePath} must exist in {RendererRelativePath}");

        var dispatched = Regex.Matches(source, @"case\s+Excise\.Core\.Document\.PdfAnnotationSubtype\.(\w+)\s*:")
            .Select(m => m.Groups[1].Value)
            .Where(s => s != "Unknown")
            .ToHashSet();
        var covered = Rows(policy).Select(r => r.GetProperty("subtype").GetString()!).ToHashSet();

        dispatched.Except(covered).Should().BeEmpty(
            "a subtype the renderer synthesizes an appearance for must have at least one row");
    }

    // ── shape predicates ────────────────────────────────────────────────────
    //
    // Each returns null when the shape holds and a description of what was seen
    // when it does not. They are written to be tolerant of rasterisation
    // differences (a stroke straddling a pixel boundary, antialiasing) and
    // intolerant of the things that actually distinguish these pictures: a
    // border from a glyph, a tick from a caret, a fill from an outline, an
    // upright value from a mirrored one.

    private string? DescribeShapeMismatch(JsonDocument policy, JsonElement row, SKBitmap bitmap)
    {
        var shape = row.GetProperty("shape").GetString()!;
        var rect = DeviceRect(row.GetProperty("fixture"));
        var bounds = InkBounds(bitmap);
        var ink = InkedPixels(bitmap);

        if (shape == "none")
            return ink == 0 ? null : $"expected nothing drawn, found {ink} inked px at {Describe(bounds)}";
        if (bounds is not { } b)
            return "expected a mark, found a blank page";

        switch (shape)
        {
            case "hollow-rect-on-annot-rect":
                return MatchesRect(b, rect) is { } r1 ? r1
                     : InteriorInkFraction(bitmap, rect) > 0.02
                       ? $"the interior is inked ({InteriorInkFraction(bitmap, rect):P0}) — that is a fill, not an outline"
                       : null;

            case "filled-rect-on-annot-rect":
                return MatchesRect(b, rect) is { } r2 ? r2
                     : InteriorInkFraction(bitmap, rect) < 0.9
                       ? $"the interior is only {InteriorInkFraction(bitmap, rect):P0} inked — that is an outline, not a fill"
                       : null;

            case "hollow-oval-on-annot-rect":
                if (MatchesRect(b, rect) is { } r3) return r3;
                if (InteriorInkFraction(bitmap, rect) > 0.02) return "the interior is inked";
                // An eighth of the rect: an inscribed ellipse clears that
                // corner by ~5 px on a 100 pt rect, a stroked rectangle runs
                // straight through it.
                var corner = new SKRectI(rect.Left, rect.Top,
                    rect.Left + rect.Width / 8, rect.Top + rect.Height / 8);
                return InkedPixels(bitmap, corner) > 0
                    ? "the rect's corner is inked — that is a rectangle, not an oval"
                    : null;

            case "inset-tick":
                if (b.Left <= rect.Left || b.Top <= rect.Top ||
                    b.Right >= rect.Right || b.Bottom >= rect.Bottom)
                    return $"the mark {Describe(b)} is not strictly inside the rect {Describe(rect)} — " +
                           "a glyph is inset on all four sides, a border is not";
                var centroid = RowInkCentroidX(bitmap, b.Bottom - 1);
                return centroid >= b.Left + (b.Right - b.Left) * 0.45
                    ? $"the lowest row's ink centres at x={centroid} in {Describe(b)} — a tick's " +
                      "low point is its corner, left of centre; a caret's lowest row is its two " +
                      "outer ends and centres instead"
                    : null;

            case "marker-at-anchor":
                if (Math.Abs(b.Left - rect.Left) > 3 || Math.Abs(b.Top - rect.Top) > 3)
                    return $"the marker {Describe(b)} is not anchored at the rect's corner {Describe(rect)}";
                if (b.Width is < 12 or > 26 || b.Height is < 12 or > 26)
                    return $"the marker is {b.Width}x{b.Height} px — §12.5.6.4 wants a fixed ~16 pt icon";
                return (double)ink / (b.Width * b.Height) < 0.5
                    ? "the marker is mostly empty — the oracles draw a solid mark"
                    : null;

            case "text-value-inside-rect":
                return TextValueProblem(b, rect, "the value");

            case "clipped-remnant-inside-rect":
                if (Contains(rect, b) is { } r4) return r4;
                var reference = InkedPixels(RenderRowById(policy, row.GetProperty("comparedWith").GetString()!));
                return ink >= reference * 0.25
                    ? $"{ink} inked px against the unclipped row's {reference} — the run was " +
                      "supposed to land mostly outside the field and be clipped away"
                    : null;

            case "mirrored-text-value":
                if (TextValueProblem(b, rect, "the mirrored value") is { } r5) return r5;
                using (var upright = RenderRowById(policy, row.GetProperty("comparedWith").GetString()!))
                {
                    var here = ColumnProfile(bitmap, rect);
                    var there = ColumnProfile(upright, rect);
                    var reversed = Correlation(here, there.Reverse().ToArray());
                    var forward = Correlation(here, there);
                    return reversed > 0.9 && reversed > forward
                        ? null
                        : $"the column profile correlates {forward:0.00} with the upright value and " +
                          $"{reversed:0.00} with its reverse — a mirrored run correlates with the " +
                          "REVERSE. Ink count and bbox cannot see this; that is why the row asserts it";
                }

            case "stroked-path-inside-rect":
                if (Contains(rect, b) is { } r6) return r6;
                var density = (double)ink / (b.Width * b.Height);
                return density > 0.3
                    ? $"the mark fills {density:P0} of its own bbox — that is a fill, not a stroke"
                    : null;

            case "filled-band-over-quad":
                return InkedPixels(bitmap, rect) < rect.Width * rect.Height * 0.8
                    ? $"the quad is only {(double)InkedPixels(bitmap, rect) / (rect.Width * rect.Height):P0} inked"
                    : null;

            case "horizontal-band":
                if (b.Width < rect.Width * 0.9)
                    return $"the band is {b.Width} px wide over a {rect.Width} px quad";
                return b.Height > rect.Height * 0.4
                    ? $"the band is {b.Height} px tall over a {rect.Height} px quad — that is a fill, not a line"
                    : null;

            default:
                return $"no predicate implements shape '{shape}'";
        }
    }

    private static string? TextValueProblem(SKRectI b, SKRectI rect, string what)
    {
        if (Contains(rect, b) is { } problem) return problem;
        if (b.Height > rect.Height * 0.6)
            return $"{what} is {b.Height} px tall in a {rect.Height} px field — that is not a line of text";
        return b.Width < b.Height * 2
            ? $"{what} is {b.Width}x{b.Height} px — a line of text is wider than it is tall"
            : null;
    }

    private static string? Contains(SKRectI outer, SKRectI inner) =>
        inner.Left < outer.Left - 2 || inner.Top < outer.Top - 2 ||
        inner.Right > outer.Right + 2 || inner.Bottom > outer.Bottom + 2
            ? $"ink at {Describe(inner)} escapes the annotation rect {Describe(outer)}"
            : null;

    private static string? MatchesRect(SKRectI b, SKRectI rect) =>
        Math.Abs(b.Left - rect.Left) > 2 || Math.Abs(b.Top - rect.Top) > 2 ||
        Math.Abs(b.Right - rect.Right) > 2 || Math.Abs(b.Bottom - rect.Bottom) > 2
            ? $"ink bounds {Describe(b)} do not match the annotation rect {Describe(rect)}"
            : null;

    private static double InteriorInkFraction(SKBitmap bitmap, SKRectI rect)
    {
        var inset = new SKRectI(
            rect.Left + rect.Width / 4, rect.Top + rect.Height / 4,
            rect.Right - rect.Width / 4, rect.Bottom - rect.Height / 4);
        if (inset.Width <= 0 || inset.Height <= 0) return 0;
        return (double)InkedPixels(bitmap, inset) / (inset.Width * inset.Height);
    }

    // ── rendering ───────────────────────────────────────────────────────────

    private static SKBitmap RenderRow(JsonElement row)
    {
        var fixture = row.GetProperty("fixture");
        var path = WriteFixture(fixture);
        try
        {
            using var doc = PdfDocument.Open(path);
            return new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
            {
                Dpi = fixture.GetProperty("dpi").GetInt32(),
                AntiAlias = false,
                BackgroundColor = SKColors.White,
            });
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private SKBitmap RenderRowById(JsonDocument policy, string id) =>
        RenderRow(Rows(policy).Single(r => r.GetProperty("id").GetString() == id));

    private static SKBitmap? RenderWithOracle(string oracle, string path, int dpi) => oracle switch
    {
        "mutool" => MutoolReferenceRenderer.IsAvailable
            ? MutoolReferenceRenderer.RenderPage(path, 1, dpi) : null,
        "pdftocairo" => PdftocairoReferenceRenderer.IsAvailable
            ? PdftocairoReferenceRenderer.RenderPage(path, 1, dpi) : null,
        "pdftoppm" => PdftoppmReferenceRenderer.IsAvailable
            ? PdftoppmReferenceRenderer.RenderPage(path, 1, dpi) : null,
        "ghostscript" => GhostscriptReferenceRenderer.IsAvailable
            ? GhostscriptReferenceRenderer.RenderPage(path, 1, dpi) : null,
        _ => null,
    };

    /// <summary>
    /// Assemble the row's fixture: a blank page carrying exactly one
    /// annotation, so every inked pixel on the page is the synthesized
    /// appearance and nothing else.
    /// </summary>
    private static string WriteFixture(JsonElement fixture)
    {
        var size = fixture.GetProperty("pageSize").GetInt32();
        var objects = new List<string>
        {
            $"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {fixture.GetProperty("catalogExtra").GetString()}>>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {size} {size}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{fixture.GetProperty("annotation").GetString()}\nendobj\n",
        };
        objects.AddRange(fixture.GetProperty("extraObjects").EnumerateArray().Select(o => o.GetString()!));

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-993-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    // ── pixels ──────────────────────────────────────────────────────────────

    private static bool IsInk(SKColor c) => c.Red < 240 || c.Green < 240 || c.Blue < 240;

    private static int InkedPixels(SKBitmap bitmap) =>
        InkedPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));

    private static int InkedPixels(SKBitmap bitmap, SKRectI box)
    {
        int ink = 0;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bitmap.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bitmap.Width, box.Right); x++)
                if (IsInk(bitmap.GetPixel(x, y))) ink++;
        return ink;
    }

    private static SKRectI? InkBounds(SKBitmap bitmap)
    {
        int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (IsInk(bitmap.GetPixel(x, y)))
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static int RowInkCentroidX(SKBitmap bitmap, int y)
    {
        long sum = 0; int n = 0;
        for (int x = 0; x < bitmap.Width; x++)
            if (IsInk(bitmap.GetPixel(x, y))) { sum += x; n++; }
        return n == 0 ? -1 : (int)(sum / n);
    }

    /// <summary>Inked pixels per column across the annotation rect.</summary>
    private static double[] ColumnProfile(SKBitmap bitmap, SKRectI rect)
    {
        var profile = new double[rect.Width];
        for (int x = 0; x < rect.Width; x++)
        {
            int n = 0;
            for (int y = rect.Top; y < rect.Bottom; y++)
                if (y >= 0 && y < bitmap.Height && rect.Left + x < bitmap.Width &&
                    IsInk(bitmap.GetPixel(rect.Left + x, y))) n++;
            profile[x] = n;
        }
        return profile;
    }

    private static double Correlation(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        double ma = a.Take(n).Average(), mb = b.Take(n).Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return da == 0 || db == 0 ? 0 : num / Math.Sqrt(da * db);
    }

    private static string BBoxClass(SKRectI? bbox, SKRectI rect)
    {
        if (bbox is not { } b) return "none";
        if (MatchesRect(b, rect) == null) return "matches-annot-rect";
        return Contains(rect, b) == null ? "inside-annot-rect" : "exceeds-annot-rect";
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static SKRectI DeviceRect(JsonElement fixture)
    {
        var r = fixture.GetProperty("annotDeviceRect");
        return new SKRectI(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32());
    }

    private static string Describe(SKRectI? r) =>
        r is { } v ? $"({v.Left},{v.Top})-({v.Right},{v.Bottom})" : "-";

    private static string Describe(JsonElement e) => e.ToString();

    private static IEnumerable<JsonElement> Rows(JsonDocument policy) =>
        policy.RootElement.GetProperty("rows").EnumerateArray();

    private static JsonDocument LoadPolicy()
    {
        var path = Path.Combine(FindRepoRoot(),
            PolicyRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{PolicyRelativePath} should exist");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root (no excise.sln above the test base directory).");
    }
}
