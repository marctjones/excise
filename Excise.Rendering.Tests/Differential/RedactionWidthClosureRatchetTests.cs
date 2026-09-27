using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1751 — the width-closing RATCHET over real documents. The synthetic
/// fixtures of <see cref="RedactionWidthClosureTests"/> pin each mechanism; this
/// pins what those mechanisms do to documents real producers wrote, where #1751
/// measured the gap relocated or not closed at all in 16 of 84.
///
/// <para>Each row of <c>tests/width-closure/ratchet.tsv</c> is redacted with
/// <see cref="WidthPolicy.CloseGap"/> and read back with <c>mutool -F stext</c>
/// (an independent oracle), glyph by glyph against the original. A row may
/// only improve. A line that did not close and was not reported is the output
/// #1751 exists to prevent, one that looks closed and is not: a row may be
/// pinned there only with the open issue that tracks it.</para>
/// </summary>
public sealed class RedactionWidthClosureRatchetTests : IDisposable
{
    private const string RatchetPath = "tests/width-closure/ratchet.tsv";

    /// <summary>Glyphs within this many points of the term's baseline are on its line (a superscript still is).</summary>
    private const double SameLine = 3.0;

    /// <summary>A gap wider than this on one baseline is a column gutter: text past it does not move.</summary>
    private const double Gutter = 25.0;

    /// <summary>closed and rejustified are equally good and not interchangeable: a justified line
    /// that merely closes ends short by the removed width (#1752), a ragged one re-justified states it.</summary>
    private static readonly Dictionary<string, int> Rank = new()
    {
        ["silent"] = 0, ["reported"] = 1, ["closed"] = 2, ["rejustified"] = 2,
    };

    private readonly ITestOutputHelper _out;
    private readonly List<string> _temp = new();

    public RedactionWidthClosureRatchetTests(ITestOutputHelper output) { _out = output; }

    private sealed record Row(string Doc, int Page, string Term, string Pinned, string Issue);

    [Fact]
    public void RealDocuments_CloseTheGapOrReportIt_AndNoPinnedRowRegresses()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "needs mutool [requires: tool:mutool]");
        var ratchet = TestRepoLayout.FindFileInLocalCheckout(RatchetPath)
            ?? throw new InvalidOperationException(RatchetPath + " is tracked and must exist");
        var rows = File.ReadAllLines(ratchet)
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("doc\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t'))
            .Select(c => new Row(c[0], int.Parse(c[1]), c[2], c[3], c.Length > 4 ? c[4] : ""))
            .ToList();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => Rank.ContainsKey(r.Pinned) && (r.Pinned != "silent" || r.Issue.StartsWith('#')),
            "a row pinned silent names the open issue that tracks its unreported gap");

        var failures = new List<string>();
        var absent = new List<string>();
        foreach (var group in rows.GroupBy(r => (r.Doc, r.Term)))
        {
            if (TestRepoLayout.FindFile(group.Key.Doc) is not { } source)
            {
                absent.Add(group.Key.Doc);
                continue;
            }

            byte[] saved;
            IReadOnlyList<string> notes;
            var tops = new Dictionary<int, double>();
            using (var doc = PdfDocument.Open(File.ReadAllBytes(source)))
            {
                foreach (var row in group) tops[row.Page] = doc.GetPage(row.Page).CropBox.Normalize().Top;
                doc.RedactText(group.Key.Term, RedactionOptions.Default with { Width = WidthPolicy.CloseGap, DrawBox = false });
                notes = doc.RedactionLedger.WidthNotes.ToList();
                saved = doc.SaveToBytes();
            }
            var redacted = WriteTemp(saved);

            foreach (var row in group)
            {
                (MutoolTextExtractor.ExtractPage(redacted, row.Page) ?? "").Should().NotContain(row.Term,
                    $"{row.Doc} p{row.Page}: the term is removed before its width is measured");
                var (outcome, detail) = Measure(source, redacted, row, NotesOnPage(notes, row.Page, tops[row.Page]));
                _out.WriteLine($"{outcome,-11} (pinned {row.Pinned,-11}) {row.Doc} p{row.Page} '{row.Term}': {detail}");
                if (outcome == row.Pinned) continue;
                if (Rank.TryGetValue(outcome, out var got) && got > Rank[row.Pinned])
                    _out.WriteLine($"  IMPROVED: raise its pin in {RatchetPath} to {outcome}");
                else
                    failures.Add($"{row.Doc} p{row.Page} '{row.Term}': {outcome}, pinned {row.Pinned} — {detail}");
            }
        }

        failures.Should().BeEmpty(
            "a width-closing redaction regressed on a real document, or left a line that is neither " +
            "closed nor reported (#1751):\n" + string.Join("\n", failures));
        Assert.SkipWhen(absent.Count > 0,
            TestRepoLayout.AbsenceReason($"{absent.Count} of the ratchet's documents", absent.ToArray()));
    }

    /// <summary>
    /// Compare the page glyph by glyph (mutool emission order, spaces left out)
    /// and say what happened to the term's line.
    /// </summary>
    private static (string Outcome, string Detail) Measure(
        string source, string redacted, Row row, IReadOnlyList<double> notedLines)
    {
        var all = MutoolGlyphPositions.ExtractPage(source, row.Page)!.ToList();
        var after = MutoolGlyphPositions.ExtractPage(redacted, row.Page)!.Where(g => !IsSpace(g)).ToList();
        var before = all.Where(g => !IsSpace(g)).ToList();

        // The occurrence the output lost: with spaces left out, the term's
        // letters can also appear across a word boundary earlier on the page.
        var needle = row.Term.Replace(" ", "").Select(c => c.ToString()).ToList();
        var shown = after.Select(g => g.Char).ToList();
        var at = Enumerable.Range(0, Math.Max(0, before.Count - needle.Count + 1))
            .Where(k => before.Skip(k).Take(needle.Count).Select(g => g.Char).SequenceEqual(needle))
            .Where(k => before.Take(k).Concat(before.Skip(k + needle.Count)).Select(g => g.Char).SequenceEqual(shown))
            .DefaultIfEmpty(-1).First();
        if (at < 0) return ("unaligned", "the output shows other text than the original minus the term");

        // The removed advance: from the term's first glyph to whatever the
        // page shows next on that baseline, its trailing space included.
        var first = before[at];
        var reported = notedLines.Any(y => Math.Abs(y - first.Y) <= SameLine);
        var last = before[at + needle.Count - 1];
        var next = all.SkipWhile(g => g != last).Skip(1).FirstOrDefault(g => Math.Abs(g.Y - first.Y) <= SameLine);
        if (next == default) return ("vacuous", "nothing follows the term on its line: pick another term");
        var width = next.X - first.X;

        // The term's line: its baseline, cut at column gutters in the original.
        var line = before.Select((g, i) => (Glyph: g, Index: i))
            .Where(p => Math.Abs(p.Glyph.Y - first.Y) <= SameLine)
            .OrderBy(p => p.Glyph.X)
            .ToList();
        var from = line.FindIndex(p => p.Index == at);
        var to = from;
        while (from > 0 && line[from].Glyph.X - line[from - 1].Glyph.X <= Gutter) from--;
        while (to + 1 < line.Count && line[to + 1].Glyph.X - line[to].Glyph.X <= Gutter) to++;
        var onLine = line.GetRange(from, to - from + 1).Select(p => p.Index).ToHashSet();

        var dxBefore = new List<double>();
        var dxAfter = new List<double>();
        var edges = new List<(double X, double Dx)>();
        var moved = 0;
        for (int i = 0, k = 0; i < before.Count; i++)
        {
            if (i >= at && i < at + needle.Count) continue;
            var dx = after[k++].X - before[i].X;
            if (!onLine.Contains(i)) { if (Math.Abs(dx) > 0.1) moved++; continue; }
            (i < at ? dxBefore : dxAfter).Add(dx);
            edges.Add((before[i].X, dx));
        }
        if (moved > 0) return ("moved", $"{moved} glyphs off the term's line moved");
        if (dxAfter.Count == 0) return ("vacuous", "nothing follows the term on its line: pick another term");

        var b = dxBefore.Count == 0 ? 0 : dxBefore.Average();
        var a = dxAfter.Average();
        var detail = $"removed {width:F1} pt; before moved {b:F2} (spread {Spread(dxBefore):F2}), " +
                     $"after moved {a:F2} (spread {Spread(dxAfter):F2})";
        if (reported) return ("reported", detail);
        if (Spread(dxBefore) <= 1 && Spread(dxAfter) <= 1 && Math.Abs(b - a - width) <= Math.Max(1, 0.05 * width))
            return ("closed", detail);
        // Re-justified: the line keeps both edges and the text after the term
        // moves by a share per word space, never by a jump. A jump of the
        // removed width is #1751's relocated gap, at a boundary nothing moved.
        var edgesKept = Math.Abs(edges.MinBy(e => e.X).Dx) <= 0.3 && Math.Abs(edges.MaxBy(e => e.X).Dx) <= 0.3;
        var steps = edges.Where(e => e.X > first.X).OrderBy(e => e.X).Select(e => e.Dx).ToList();
        var smooth = steps.Zip(steps.Skip(1), (p, q) => Math.Abs(q - p)).All(d => d <= Math.Max(1, width / 2));
        if (edgesKept && smooth && dxAfter.Any(d => Math.Abs(d) > 0.3))
            return ("rejustified", detail);
        return ("silent", detail);
    }

    /// <summary>The mutool baselines (y down from the crop box top) of the page's WIDTH NOT CLOSED rows.</summary>
    private static List<double> NotesOnPage(IReadOnlyList<string> notes, int page, double top) =>
        notes.Where(n => n.StartsWith($"page {page}, line at y=", StringComparison.Ordinal))
             .Select(n => n[$"page {page}, line at y=".Length..].Split(':')[0])
             .Select(y => top - double.Parse(y, System.Globalization.CultureInfo.InvariantCulture))
             .ToList();

    private static bool IsSpace(MutoolGlyphPositions.Glyph g) => string.IsNullOrWhiteSpace(g.Char);

    private static double Spread(List<double> values) => values.Count == 0 ? 0 : values.Max() - values.Min();

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-ratchet-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
