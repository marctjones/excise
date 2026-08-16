using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1011 — the gate over <c>tests/skia-rasterisation-register.json</c>.
///
/// <para><b>What the register is for.</b> A rendering difference that
/// originates in SkiaSharp's own rasterisation is an ACCEPTED LIMITATION: not
/// fixed, not compensated for, not re-triaged. Nothing recorded which
/// differences had already been traced to Skia, so they were re-investigated
/// each time. This gate keeps the record honest in both directions.</para>
///
/// <para><b>The three checks, and the direction each one fails in:</b></para>
/// <list type="number">
/// <item><see cref="TheRegisterIsWellFormed"/> — every row carries evidence
/// that Skia received correct geometry, and says so in a field that cannot be
/// left empty. Needs no renderer and no tool, so it runs everywhere. This is
/// the check that stops the register becoming a place to park defects.</item>
/// <item><see cref="EveryRowStillReproducesInExcise"/> — excise's own recorded
/// numbers are re-measured. Fails when the renderer changes and the register
/// does not. Needs no external tool.</item>
/// <item><see cref="EveryRowStillDiffersFromTheOracles"/> — the oracles are
/// re-run and the difference must STILL BE THERE. An accepted limitation that
/// quietly went away must be deleted, not left standing to excuse a future
/// defect. This is the check the #1011 anecdote itself failed: the 1 pt link
/// border it describes does not reproduce, which is why it is in the register's
/// <c>notASkiaDifference</c> list rather than its rows.</item>
/// </list>
///
/// <para><b>What this gate does NOT do.</b> It does not wire the register into
/// the corpus scan's non-PASS triage. Both current rows are synthetic
/// content-stream fixtures rather than corpus pages, so that consult would be
/// unreachable code with nothing to exercise it; see the report on #1011.</para>
/// </summary>
public class SkiaRasterisationRegisterTests
{
    private const string RegisterRelativePath = "tests/skia-rasterisation-register.json";

    // ── 1. well-formed, and no defect parked ────────────────────────────────

    [Fact]
    public void TheRegisterIsWellFormed()
    {
        using var register = LoadRegister();
        var root = register.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);

        var rows = Rows(register);
        rows.Count.Should().Be(root.GetProperty("rowCount").GetInt32(),
            $"rowCount in {RegisterRelativePath} is a hand-maintained tripwire — a row added or "
            + "removed without touching it is a row nobody re-read");

        rows.Select(r => r.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();

        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString()!;

            row.GetProperty("measured").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$",
                $"{id}: a number without a date is not a measurement");
            row.GetProperty("what").GetString().Should().NotBeNullOrWhiteSpace();
            row.GetProperty("byHowMuch").GetString().Should().NotBeNullOrWhiteSpace(
                $"{id}: 'differs' is not a row; by how much is");

            // The load-bearing field. Without it a row is an unexplained
            // difference, and an unexplained difference is an open bug.
            row.GetProperty("evidenceInputToSkiaCorrect").GetString()!.Trim().Length
                .Should().BeGreaterThan(80,
                    $"{id}: the evidence that SKIA RECEIVED CORRECT GEOMETRY is what separates an "
                    + "accepted limitation from an excise bug, and it has to be a measurement "
                    + "someone else can re-run, not a sentence of reassurance");

            row.GetProperty("noCompensation").GetBoolean().Should().BeTrue(
                $"{id}: the register records differences we live with. A row that needed a fudge "
                + "factor, a widened tolerance or a special-cased fixture is not one of them");

            ExciseMeasurements(row).Should().NotBeEmpty($"{id}: no excise measurement recorded");
            Oracles(row).Should().NotBeEmpty(
                $"{id}: a difference is a difference FROM something — record the engines");
            Oracles(row).Select(o => o.GetProperty("engine").GetString())
                .Distinct().Should().HaveCountGreaterThanOrEqualTo(2,
                    $"{id}: one engine's opinion is not evidence that excise is the odd one out");
        }

        // The NOT list is part of the record: a claim that was re-measured and
        // did not hold, so nobody re-derives it. It carries its own required
        // fields precisely so it cannot be used to wave something away.
        foreach (var entry in root.GetProperty("notASkiaDifference").EnumerateArray())
        {
            entry.GetProperty("claim").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("remeasured").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
            entry.GetProperty("finding").GetString()!.Trim().Length.Should().BeGreaterThan(80);
        }
    }

    // ── 2. excise still measures what the register says ─────────────────────

    [Fact]
    public void EveryRowStillReproducesInExcise()
    {
        using var register = LoadRegister();
        var failures = new List<string>();

        foreach (var row in Rows(register))
        {
            var id = row.GetProperty("id").GetString()!;
            CheckExcise(id, row.GetProperty("fixture"), ExciseMeasurements(row), failures);

            if (row.TryGetProperty("controlFixture", out var control))
            {
                CheckExcise($"{id}/control", control,
                    control.GetProperty("excise").EnumerateArray().ToList(), failures);
            }
        }

        failures.Should().BeEmpty(
            "excise's own recorded numbers must still be what excise draws. If the renderer moved, "
            + $"re-measure and update {RegisterRelativePath} — an accepted Skia limitation is a "
            + "statement about today's behaviour, not a permanent excuse");
    }

    private static void CheckExcise(
        string id, JsonElement fixture, List<JsonElement> measurements, List<string> failures)
    {
        var path = WriteFixture(fixture);
        try
        {
            foreach (var m in measurements)
            {
                var aa = m.GetProperty("antiAlias").GetBoolean();
                using var bitmap = RenderWithExcise(path, fixture.GetProperty("dpi").GetInt32(), aa);
                var ink = InkedPixels(bitmap);
                var recorded = m.GetProperty("inkedPixels").GetInt32();

                // Exact for zero (the whole point of that row), otherwise a few
                // percent for platform glyph/AA drift.
                var ok = recorded == 0 ? ink == 0 : Math.Abs(ink - recorded) <= Math.Max(4, recorded * 0.05);
                if (!ok)
                {
                    failures.Add($"{id} (antiAlias={aa}): recorded {recorded} inked px, measured {ink}");
                    continue;
                }

                var bbox = InkBounds(bitmap);
                var recordedBox = ReadBox(m.GetProperty("bbox"));
                if (!BoxesAgree(bbox, recordedBox))
                    failures.Add($"{id} (antiAlias={aa}): recorded bbox {Describe(recordedBox)}, measured {Describe(bbox)}");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }

    // ── 3. the difference is still a difference ─────────────────────────────

    [Fact]
    public void EveryRowStillDiffersFromTheOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        using var register = LoadRegister();
        var failures = new List<string>();

        foreach (var row in Rows(register))
        {
            var id = row.GetProperty("id").GetString()!;
            var fixture = row.GetProperty("fixture");
            var dpi = fixture.GetProperty("dpi").GetInt32();
            var path = WriteFixture(fixture);

            try
            {
                // excise as the register says it renders in an aliased ink
                // comparison — the mode the differential gates use, and the one
                // a re-triage would be run in.
                using var bitmap = RenderWithExcise(path, dpi, antiAlias: false);
                var exciseInk = InkedPixels(bitmap);

                var stillDiffers = false;
                foreach (var o in Oracles(row))
                {
                    var oracle = o.GetProperty("oracle").GetString()!;
                    using var reference = RenderWithOracle(oracle, path, dpi);
                    if (reference == null) continue;   // tool absent — no opinion

                    var ink = InkedPixels(reference);
                    var recorded = o.GetProperty("inkedPixels").GetInt32();
                    if (ink > 25 && recorded > 25 && (ink > recorded * 2 || ink * 2 < recorded))
                    {
                        failures.Add($"{id}/{oracle}: recorded {recorded} inked px, measured {ink} — "
                                     + "more than a factor of two apart, which is more than tool drift");
                    }

                    if (exciseInk == 0 != (ink == 0) || (ink > 0 && Ratio(exciseInk, ink) >= 1.5))
                        stillDiffers = true;
                }

                if (!stillDiffers)
                {
                    failures.Add($"{id}: excise ({exciseInk} px) no longer differs from any oracle. "
                                 + "DELETE the row — a limitation that went away must not stay on "
                                 + "the books excusing a future defect");
                }
            }
            finally
            {
                try { File.Delete(path); } catch { /* temp */ }
            }
        }

        failures.Should().BeEmpty(
            $"every row in {RegisterRelativePath} is a claim about the world that the world can "
            + "invalidate");
    }

    private static double Ratio(int a, int b) =>
        a == 0 || b == 0 ? double.PositiveInfinity : Math.Max((double)a / b, (double)b / a);

    // ── plumbing ────────────────────────────────────────────────────────────

    private static JsonDocument LoadRegister()
    {
        var path = FindRepoFile(RegisterRelativePath);
        File.Exists(path).Should().BeTrue($"{RegisterRelativePath} must be checked in");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static List<JsonElement> Rows(JsonDocument register) =>
        register.RootElement.GetProperty("rows").EnumerateArray().ToList();

    private static List<JsonElement> ExciseMeasurements(JsonElement row) =>
        row.GetProperty("excise").EnumerateArray().ToList();

    private static List<JsonElement> Oracles(JsonElement row) =>
        row.GetProperty("oracles").EnumerateArray().ToList();

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return relativePath;
    }

    private static SKBitmap RenderWithExcise(string path, int dpi, bool antiAlias)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = dpi,
            AntiAlias = antiAlias,
            BackgroundColor = SKColors.White,
        });
    }

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
    /// The fixture, written out as the register states it: one content stream
    /// on a square page with no resources. Kept deliberately minimal so that
    /// "the input to Skia was correct" is checkable by reading the row — the
    /// path really is the numbers in the file.
    /// </summary>
    private static string WriteFixture(JsonElement fixture)
    {
        var content = fixture.GetProperty("content").GetString()!;
        var size = fixture.GetProperty("pageSize").GetInt32();
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {size} {size}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-1011-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    private static bool IsInk(SKColor c) => c.Red < 240 || c.Green < 240 || c.Blue < 240;

    private static int InkedPixels(SKBitmap bitmap)
    {
        int n = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (IsInk(bitmap.GetPixel(x, y))) n++;
        return n;
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

    private static SKRectI? ReadBox(JsonElement box)
    {
        if (box.ValueKind == JsonValueKind.Null) return null;
        var v = box.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        return new SKRectI(v[0], v[1], v[2], v[3]);
    }

    /// <summary>One pixel of slack per edge: enough for AA drift, not enough to hide a shape change.</summary>
    private static bool BoxesAgree(SKRectI? measured, SKRectI? recorded)
    {
        if (measured == null || recorded == null) return measured == null && recorded == null;
        return Math.Abs(measured.Value.Left - recorded.Value.Left) <= 1
            && Math.Abs(measured.Value.Top - recorded.Value.Top) <= 1
            && Math.Abs(measured.Value.Right - recorded.Value.Right) <= 1
            && Math.Abs(measured.Value.Bottom - recorded.Value.Bottom) <= 1;
    }

    private static string Describe(SKRectI? box) => box == null
        ? "none"
        : $"({box.Value.Left},{box.Value.Top})-({box.Value.Right},{box.Value.Bottom})";
}
