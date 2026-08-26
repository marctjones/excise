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

        // Three failure classes. "occluded" = genuinely hidden (black text under
        // a box; faint low-contrast) — the core job. "inverted-box" = white text
        // on a black box (readable, but a redaction that did not take) — closed
        // by #1180. "highlight" = readable text under a translucent highlight —
        // the residual gap (#1180): flagging it in the wild would false-positive
        // on ordinary highlighting, so it stays out until it can be gated safely.
        static string Class(string colour) => colour switch
        {
            "black-on-white" or "low-contrast" => "occluded",
            "white-on-black" => "inverted-box",
            _ => "highlight",
        };

        var xrayAvailable = XRayBadRedactionDetector.IsAvailable;
        var total = new Dictionary<string, int>();
        var excise = new Dictionary<string, int>();
        int comparable = 0, exciseComparable = 0, xrayComparable = 0;

        foreach (var c in cases)
        {
            var path = Path.Combine(corpus, c.Id + ".pdf");
            var cls = Class(c.Colour);
            total[cls] = total.GetValueOrDefault(cls) + 1;

            bool exciseGot;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                exciseGot = HiddenTextDetector.Scan(doc)
                    .Any(h => h.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase));
            if (exciseGot) excise[cls] = excise.GetValueOrDefault(cls) + 1;

            if (!xrayAvailable) continue;
            var xr = XRayBadRedactionDetector.Inspect(path);
            if (xr == null) continue;                 // x-ray could not read this one — not comparable
            comparable++;
            if (exciseGot) exciseComparable++;
            if (xr.Any(b => b.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase))) xrayComparable++;
        }

        int E(string k) => excise.GetValueOrDefault(k);
        int T(string k) => total.GetValueOrDefault(k);
        _out.WriteLine($"under-box CERTAIN recall — occluded {E("occluded")}/{T("occluded")}, " +
            $"inverted-box {E("inverted-box")}/{T("inverted-box")}, highlight {E("highlight")}/{T("highlight")} (#1180 residual)" +
            (xrayAvailable
                ? $"  |  overall vs x-ray reference: excise {exciseComparable}/{comparable}, x-ray {xrayComparable}/{comparable}"
                : "  |  x-ray reference: NOT INSTALLED (run scripts/download-xray.sh)"));

        // (1) verifiable with no external tool: occluded text MUST be recovered
        // (core guarantee), and #1180 closed the inverted-box (white-on-black)
        // class, so it must stay recovered too.
        E("occluded").Should().Be(T("occluded"), "the certain channel must recover every occluded secret");
        E("inverted-box").Should().Be(T("inverted-box"), "#1180: readable text on a black box must be surfaced");

        // (2) held to a REAL independent unredactor when one is installed — the
        // claim 'our recovery is complete' is worthless without a second tool.
        if (xrayAvailable && comparable > 0)
            exciseComparable.Should().BeGreaterThanOrEqualTo(xrayComparable,
                "excise's certain channel must recover at least what the x-ray reference recovers");
    }
}
