using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1201: a small, reviewed release-quality visual-redaction set.  The broad
/// corpus finds surprises; this register states which hard shapes must remain
/// intentional and which visual mutations the independent renderer/OCR gates
/// are required to reject.  It is deliberately not an Excise-rendered oracle.
/// </summary>
public class RedactionVisualGoldSetTests
{
    private const string GoldSetRelativePath = "tests/redaction-visual-gold-set.json";
    private const string Secret = "VISUALCANARYQ7";

    [Fact]
    public void ReviewedVisualGoldSet_IsWellFormedAndCoversEveryRequiredShape()
    {
        using var goldSet = LoadGoldSet();
        var root = goldSet.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("reviewedOn").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        root.GetProperty("reviewer").GetString().Should().NotBeNullOrWhiteSpace();

        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.Should().Be(root.GetProperty("rowCount").GetInt32(),
            "rowCount makes a removed reviewed case visible in the diff");
        rows.Select(row => row.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();

        var carriers = rows.Select(row => row.GetProperty("carrier").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "page text", "Form XObject", "annotation contents and appearance",
                     "raster scan with invisible OCR overlay", "image XObjects and supported filters",
                     "masked or clipped rendered content", "rotated page geometry",
                     "transparent or opacity-controlled cover", "ordinary vector text"
                 })
            carriers.Should().Contain(required, $"#1201 requires a reviewed visual case for {required}");

        var mutations = rows.SelectMany(row => row.GetProperty("mutations").EnumerateArray())
            .Select(m => m.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "leave-target-visible", "delete-untargeted-glyph", "delete-untargeted-image",
                     "shift-redaction-box", "transparent-cover"
                 })
            mutations.Should().Contain(required,
                "the reviewed subset must name every visual failure mode the gate claims to catch");

        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString();
            row.GetProperty("independentRenderer").GetString().Should().NotContain("Excise",
                $"{id}: the renderer evidence cannot grade itself");
            row.GetProperty("adjudication").GetString()!.Trim().Length.Should().BeGreaterThan(80,
                $"{id}: reference disagreement needs a recorded content/geometry rationale, not a pixel threshold");

            var evidence = row.GetProperty("evidence").GetString()!;
            File.Exists(FindRepoFile(evidence)).Should().BeTrue(
                $"{id}: every reviewed claim must point to a live test, not a stale fixture name");
        }
    }

    [Fact]
    public void VisualReadableAxis_RejectsVisibleShiftedAndTransparentCoverMutations()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var input = WriteTemp(BuildTextPdf(""));
        var plainLeak = WriteTemp(BuildTextPdf(""));
        var shiftedCover = WriteTemp(BuildTextPdf("q 0 0 0 rg 72 600 250 36 re f Q"));
        var transparentCover = WriteTemp(BuildTextPdf("q /GS1 gs 0 0 0 rg 72 695 250 36 re f Q"));
        try
        {
            foreach (var mutant in new[] { plainLeak, shiftedCover, transparentCover })
            {
                var readable = RedactionBenchmarkRunner.MeasureVisualReadable(input, mutant, Secret);
                Assert.SkipUnless(readable >= 0, "tesseract not installed or could not read the fixture");
                readable.Should().Be(1,
                    "an independent renderer plus OCR must reject a target that remains visible, " +
                    "whether no box was drawn, the box was shifted, or its opacity was insufficient");
            }
        }
        finally
        {
            Delete(input, plainLeak, shiftedCover, transparentCover);
        }
    }

    [Fact]
    public void SurvivingRenderAxis_RejectsDeletionOfAnUntargetedImageSizedRegion()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        // The black rectangle is an untargeted image-sized survivor.  The output
        // mutation deletes it while leaving the target present; the target box is
        // masked by the metric, so this can only pass if it measures survivors.
        var input = WriteTemp(BuildTextPdf("q 0 0 0 rg 100 100 400 400 re f Q"));
        var deletedSurvivor = WriteTemp(BuildTextPdf(""));
        try
        {
            var delta = RedactionBenchmarkRunner.MeasureSurvivingRenderDelta(input, deletedSurvivor, Secret);
            delta.Should().BeGreaterThan(0.02,
                "the independent render-fidelity axis must fail when an untargeted image-sized " +
                "region disappears; a removal-only or target-only metric would miss this");
        }
        finally
        {
            Delete(input, deletedSurvivor);
        }
    }

    private static JsonDocument LoadGoldSet()
    {
        var path = FindRepoFile(GoldSetRelativePath);
        File.Exists(path).Should().BeTrue($"{GoldSetRelativePath} must be checked in");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return relativePath;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redaction-visual-gold-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Delete(params string[] paths)
    {
        foreach (var path in paths)
            try { File.Delete(path); } catch { /* best effort */ }
    }

    private static byte[] BuildTextPdf(string suffix)
    {
        var content = Encoding.Latin1.GetBytes(
            $"BT /F1 30 Tf 72 700 Td (HEADER {Secret} FOOTER) Tj ET\n{suffix}\n");
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.Latin1.GetBytes(value));
        Write("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
              "/Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 6 0 R >> >> >>\nendobj\n");
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); stream.Write(content); Write("endstream\nendobj\n");
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Write("6 0 obj\n<< /Type /ExtGState /ca 0.20 /CA 0.20 >>\nendobj\n");
        Write("trailer\n<< /Root 1 0 R /Size 7 >>\n%%EOF\n");
        return stream.ToArray();
    }
}
