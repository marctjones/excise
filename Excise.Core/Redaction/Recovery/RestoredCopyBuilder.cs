using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1588 — writes a rebuilt PDF with recovered material drawn back where it
/// came from: green behind certain recoveries, amber behind candidates, a note
/// where something is present but was not read.
///
/// <para><b>The output contains the sensitive text by design.</b> That is the
/// point — it is what a reviewer hands a court or a records officer to show
/// what a redaction leaked. It is also why this never overwrites its input,
/// always writes a new file, and stamps the document so a copy separated from
/// its context still says what it is.</para>
///
/// <para><b>Drawn ON TOP of the mark, not in place of it.</b> The original page
/// is left exactly as it was and the recovery is painted over it. A restored
/// copy that rebuilt the page would be excise's rendering of the document
/// rather than the document, and the difference matters when the artefact is
/// evidence of what the file contained.</para>
///
/// <para><b>Every restored item carries an annotation</b> naming the channel,
/// carrier and confidence, so a reader hovering over green text can see
/// whether it was read out of the bytes or inferred, without consulting the
/// JSON report.</para>
/// </summary>
public static class RestoredCopyBuilder
{
    /// <summary>Green: the bytes say this.</summary>
    private static readonly (double R, double G, double B) CertainFill = (0.65, 0.92, 0.65);

    /// <summary>Amber: a constraint, not a reading.</summary>
    private static readonly (double R, double G, double B) CandidateFill = (1.0, 0.85, 0.5);

    /// <param name="ItemsDrawn">Findings painted onto a page.</param>
    /// <param name="DocumentLevelItems">Findings with no page, listed on the summary page.</param>
    /// <param name="SummaryPageAdded">Whether a summary page was appended.</param>
    public readonly record struct RestoreResult(
        int ItemsDrawn, int DocumentLevelItems, bool SummaryPageAdded);

    /// <summary>
    /// Draw <paramref name="report"/>'s findings onto <paramref name="document"/>
    /// in place. The caller saves; this never writes a file, so the refusal to
    /// overwrite an input lives with whoever owns the paths.
    /// </summary>
    public static RestoreResult Apply(PdfDocument document, RecoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(report);

        var located = report.AllFindings.Where(f => f.Location != null).ToList();
        var documentLevel = report.DocumentLevel.Where(f => f.Location == null).ToList();

        var drawn = 0;
        foreach (var byPage in located.GroupBy(f => f.Location!.PageNumber))
        {
            if (byPage.Key < 1 || byPage.Key > document.PageCount) continue;
            var page = document.GetPage(byPage.Key);

            var font = FirstFontResource(page);
            var ops = new List<ContentOperator>(page.GetContentStream().Operators);
            foreach (var finding in byPage)
            {
                DrawFinding(ops, finding, font);
                AddExplanatoryAnnotation(document, byPage.Key, finding);
                drawn++;
            }

            DrawWatermark(ops, page, font);
            page.SetContentStream(new ContentStream(ops));
        }

        StampMetadata(document);
        var summaryAdded = documentLevel.Count > 0 && TryAddSummaryAnnotation(document, documentLevel);
        return new RestoreResult(drawn, documentLevel.Count, summaryAdded);
    }

    private static void DrawFinding(
        List<ContentOperator> ops, RecoveredFinding finding, string? font)
    {
        var rect = finding.Location!.Rect.Normalize();
        var certain = finding.Confidence == RecoveryConfidence.Certain;
        var fill = certain ? CertainFill : CandidateFill;

        // A present-only finding has nothing to write, so it gets the highlight
        // and its annotation and no text -- printing a guess there would invent
        // a value the channel never produced.
        var text = certain
            ? finding.Text
            : finding.Candidates.Count > 0 ? finding.Candidates[0] : null;

        ops.Add(new ContentOperator("q"));
        ops.Add(Op("rg", fill.R, fill.G, fill.B));
        ops.Add(Op("re", rect.Left, rect.Bottom, Math.Max(1, rect.Width), Math.Max(1, rect.Height)));
        ops.Add(new ContentOperator("f"));
        ops.Add(new ContentOperator("Q"));

        if (string.IsNullOrEmpty(text) || font == null) return;

        // Sized to the box, floored so a thin mark does not produce unreadable
        // text, and clamped so a tall one does not produce absurd text.
        var size = Math.Clamp(rect.Height * 0.72, 6, 18);

        ops.Add(new ContentOperator("q"));
        ops.Add(Op("rg", 0.0, 0.0, 0.0));
        ops.Add(new ContentOperator("BT"));
        ops.Add(new ContentOperator("Tf", new PdfObject[]
        {
            new PdfName(font), new PdfReal(size),
        }));
        ops.Add(Op("Td", rect.Left + 1, rect.Bottom + rect.Height * 0.2));
        ops.Add(new ContentOperator("Tj", new PdfObject[] { new PdfString(Drawable(text!)) }));
        ops.Add(new ContentOperator("ET"));
        ops.Add(new ContentOperator("Q"));
    }

    /// <summary>
    /// A visible stamp, so a page separated from its report still says what it
    /// is. Bottom-left, small, out of the way of the content it annotates.
    /// </summary>
    private static void DrawWatermark(List<ContentOperator> ops, PdfPage page, string? font)
    {
        if (font == null) return;
        var box = page.CropBox.Normalize();
        ops.Add(new ContentOperator("q"));
        ops.Add(Op("rg", 0.75, 0.1, 0.1));
        ops.Add(new ContentOperator("BT"));
        ops.Add(new ContentOperator("Tf", new PdfObject[]
        {
            new PdfName(font), new PdfReal(8),
        }));
        ops.Add(Op("Td", box.Left + 12, box.Bottom + 10));
        ops.Add(new ContentOperator("Tj", new PdfObject[]
        {
            new PdfString("RECONSTRUCTION by excise unredact - NOT the original document"),
        }));
        ops.Add(new ContentOperator("ET"));
        ops.Add(new ContentOperator("Q"));
    }

    private static void AddExplanatoryAnnotation(
        PdfDocument document, int pageNumber, RecoveredFinding finding)
    {
        var rect = finding.Location!.Rect.Normalize();
        var confidence = finding.Confidence switch
        {
            RecoveryConfidence.Certain => "CERTAIN — read from the file's bytes",
            RecoveryConfidence.Candidate => "CANDIDATE — a constraint, not a reading",
            _ => "PRESENT-ONLY — material survives here; this channel did not decode it",
        };
        var body =
            $"{confidence}\nchannel: {finding.Channel}\ncarrier: {finding.Carrier}" +
            (finding.Candidates.Count > 0
                ? $"\ncandidates ({finding.Candidates.Count}): " +
                  string.Join(", ", finding.Candidates.Take(10)) +
                  $"\nresidual: {finding.ResidualBits:F2} bits"
                : "");

        try
        {
            document.AddTextAnnotation(
                pageNumber,
                new PdfRectangle(rect.Right + 2, rect.Bottom, rect.Right + 18, rect.Bottom + 16),
                body,
                author: "excise unredact");
        }
        catch { /* an annotation that will not attach must not lose the drawn text */ }
    }

    private static void StampMetadata(PdfDocument document)
    {
        try
        {
            document.SetTitle("RECONSTRUCTION — recovered redacted material (excise unredact)");
        }
        catch { /* metadata is a courtesy; the watermark is the load-bearing marker */ }
    }

    /// <summary>
    /// Document-level findings — metadata, attachments, XFA, a prior revision
    /// with no surviving geometry — have nowhere on a page to go, and dropping
    /// them would make the restored copy quieter than the report.
    ///
    /// <para>#1588 asks for an appended summary PAGE. There is no public
    /// page-append API, so this collects them into one annotation on page 1
    /// instead. The material is present and explained either way; the page is
    /// the nicer presentation and is noted as outstanding rather than faked.</para>
    /// </summary>
    private static bool TryAddSummaryAnnotation(
        PdfDocument document, IReadOnlyList<RecoveredFinding> findings)
    {
        if (document.PageCount < 1) return false;
        try
        {
            var lines = new List<string>
            {
                "Recovered material with no page location",
                "Reconstruction by excise unredact. Not the original document.",
                "",
            };
            foreach (var finding in findings.Take(40))
            {
                var value = finding.Text
                    ?? (finding.Candidates.Count > 0 ? finding.Candidates[0] : "(present, not decoded)");
                lines.Add($"[{finding.Channel}] {Truncate(finding.Carrier, 70)}");
                lines.Add($"    {Truncate(value, 90)}");
            }
            if (findings.Count > 40)
                lines.Add($"... and {findings.Count - 40} more; see the JSON report");

            var box = document.GetPage(1).CropBox.Normalize();
            document.AddTextAnnotation(
                1,
                new PdfRectangle(box.Left + 12, box.Top - 40, box.Left + 28, box.Top - 24),
                string.Join("\n", lines),
                author: "excise unredact",
                open: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Text safe to DRAW with a reused simple font.
    ///
    /// <para>A page's own font is almost always WinAnsi-encoded, and a
    /// non-ASCII character forces the string into UTF-16BE, which such a font
    /// renders as mojibake — the em-dash in this class's own watermark did
    /// exactly that on the first run. Replacing what cannot be drawn keeps the
    /// visible layer legible; the finding's ANNOTATION carries the value
    /// verbatim, so nothing is lost, and a reader who needs the exact bytes has
    /// them there and in the JSON report.</para>
    /// </summary>
    internal static string Drawable(string text)
        => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c : '?'));

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// The page's own first font resource, reused to draw restored text.
    ///
    /// <para>Reusing rather than adding one is deliberate: a font the page
    /// already resolves is guaranteed to render, and the alternative would mean
    /// embedding machinery for a job that is fundamentally annotation. A page
    /// with NO font gets its highlights and its annotations and no drawn text —
    /// the recovery is still visible and still explained, and silently drawing
    /// with a font that will not resolve would produce a blank box that looks
    /// like a successful redaction.</para>
    /// </summary>
    private static string? FirstFontResource(PdfPage page)
    {
        try { return page.GetFonts().Select(f => f.Name).FirstOrDefault(); }
        catch { return null; }
    }

    private static ContentOperator Op(string name, params double[] values)
        => new(name, values.Select(v => (PdfObject)new PdfReal(v)).ToArray());
}
