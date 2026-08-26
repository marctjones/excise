using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// RC18 — excise's CERTAIN de-redaction channel measured against a REAL external
/// reference unredactor, on constructed ground truth. Free Law Project's
/// <c>x-ray</c> (via <see cref="XRayBadRedactionDetector"/>) recovers the text
/// readable under an opaque box — exactly what the certain channel claims to do —
/// so it is a genuine independent unredactor, not excise judging excise.
///
/// <para>Scored on the #1134 manifest's 32 under-box cases (the answer is
/// physically present under the box, so it MUST be recoverable). Two claims:
/// (1) excise recovers essentially all of them — verifiable with no external
/// tool; (2) where x-ray is installed, excise recovers at least what x-ray does
/// — the claim 'our certain channel is complete' is worthless without an
/// independent tool to hold it against.</para>
/// </summary>
public class CertainChannelReferenceComparisonTests
{
    private readonly ITestOutputHelper _out;
    public CertainChannelReferenceComparisonTests(ITestOutputHelper o) => _out = o;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void ExciseCertainChannel_RecoversUnderBoxText_AtLeastAsWellAsXRay()
    {
        var corpus = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        var manifest = Path.Combine(corpus, "manifest.jsonl");
        Assert.SkipUnless(File.Exists(manifest),
            "constructed corpus absent — run scripts/gen-redaction-corpus.py [requires: corpus:redaction-synthetic]");

        var cases = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Where(m => m["method"].GetString() == "under-box")
            .Select(m => (Id: m["id"].GetString()!, Answer: m["answer"].GetString()!, Colour: m["colour"].GetString()!))
            .Where(c => File.Exists(Path.Combine(corpus, c.Id + ".pdf")))
            .ToList();
        cases.Should().NotBeEmpty("the under-box band is the certain channel's ground truth");

        // The band mixes two failure CLASSES. "Hidden" = the text is genuinely
        // occluded (black text under a box; faint low-contrast text) — the
        // certain channel's actual job. "Visible" = the text is still readable
        // (white-on-black on the box, a see-through highlight) — a failed
        // redaction, but not hidden; recovering it is a different capability
        // (#1180), tracked separately so it is not conflated with occlusion.
        static bool IsHidden(string colour) => colour is "black-on-white" or "low-contrast";

        var xrayAvailable = XRayBadRedactionDetector.IsAvailable;
        int hiddenTotal = 0, hiddenExcise = 0;
        int visibleTotal = 0, visibleExcise = 0;
        int comparable = 0, exciseComparable = 0, xrayComparable = 0;

        foreach (var c in cases)
        {
            var path = Path.Combine(corpus, c.Id + ".pdf");

            bool exciseGot;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                exciseGot = HiddenTextDetector.Scan(doc)
                    .Any(h => h.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase));

            if (IsHidden(c.Colour)) { hiddenTotal++; if (exciseGot) hiddenExcise++; }
            else { visibleTotal++; if (exciseGot) visibleExcise++; }

            if (!xrayAvailable) continue;
            var xr = XRayBadRedactionDetector.Inspect(path);
            if (xr == null) continue;                 // x-ray could not read this one — not comparable
            comparable++;
            if (exciseGot) exciseComparable++;
            if (xr.Any(b => b.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase))) xrayComparable++;
        }

        _out.WriteLine($"under-box CERTAIN recall — HIDDEN excise {hiddenExcise}/{hiddenTotal}, " +
            $"VISIBLE-readable excise {visibleExcise}/{visibleTotal} (#1180 gap)" +
            (xrayAvailable
                ? $"  |  overall vs x-ray reference: excise {exciseComparable}/{comparable}, x-ray {xrayComparable}/{comparable}"
                : "  |  x-ray reference: NOT INSTALLED (run scripts/download-xray.sh)"));

        // (1) verifiable with no external tool: genuinely-hidden text MUST be
        // recovered — that is the certain channel's core guarantee.
        hiddenExcise.Should().Be(hiddenTotal,
            "the certain channel must recover every genuinely-occluded secret");

        // (2) held to a REAL independent unredactor when one is installed — the
        // claim 'our recovery is complete' is worthless without a second tool.
        if (xrayAvailable && comparable > 0)
            exciseComparable.Should().BeGreaterThanOrEqualTo(xrayComparable,
                "excise's certain channel must recover at least what the x-ray reference recovers");
    }
}
