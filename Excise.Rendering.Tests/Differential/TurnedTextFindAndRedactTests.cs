using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2008: a line whose baseline the text matrix turns (pdf.js issue14497's
/// header, turned 90 degrees on a <c>/Rotate 90</c> page) is ONE line of text,
/// to excise as to MuPDF and Poppler. Before the fix excise extracted it one
/// glyph per line, so Find could not match a word of it.
///
/// <para>Two questions, kept apart. Redaction: the term leaves the file, judged
/// by the inflated saved bytes (<see cref="SavedPdfLeakScanner"/>), MuPDF and
/// Poppler text, and MuPDF's pixels inside the term's own glyph quads as MuPDF
/// placed them in the input. Extraction: excise's page text reads the turned
/// line in order, as both independent readers do.</para>
/// </summary>
public class TurnedTextFindAndRedactTests
{
    private const string Term = "Parklands";
    private const string TurnedLine = "REFERENCE Parklands Phase 14B";
    private const string Marker = "KEEPME";
    private const int Dpi = 72;

    private static readonly RedactionOptions PixelOptions =
        RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.CollapsePreserveLayout };

    public static TheoryData<int, int> Turns()
    {
        var data = new TheoryData<int, int>();
        foreach (var degrees in new[] { 90, 180, 270, 45, 135, 30 })
            foreach (var pageRotate in new[] { 0, 90 })
                data.Add(degrees, pageRotate);
        return data;
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void ATermInTextTurnedByItsMatrix_LeavesTheFile_AndThePixels(int degrees, int pageRotate)
    {
        RequireOracles();
        var pdf = BuildTurned(degrees, pageRotate);
        var input = WriteTemp(pdf);
        var outputs = new List<string> { input };
        try
        {
            AssertReadersRead(input, TurnedLine, degrees);
            var glyphs = TermGlyphBoxes(pdf);
            using (var before = MutoolReferenceRenderer.RenderPage(input, 1, Dpi))
                InkIn(before!, glyphs).Should().BeGreaterThan(0.05, "fixture sanity: the term is drawn");

            using (var doc = PdfDocument.Open(pdf))
            {
                var report = doc.RedactText(Term, RedactionOptions.Default);
                var saved = doc.SaveToBytes();
                var output = WriteTemp(saved);
                outputs.Add(output);

                report.VerifiedRemovals.Should().Be(1, report.ToString());
                report.IsCleanSuccess.Should().BeTrue(report.ToString());
                SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty("the term must leave the file");
                foreach (var (tool, text) in Readings(output))
                {
                    text.Should().NotContain(Term, $"{tool} must not read the term");
                    if (tool == "mutool" || degrees % 90 == 0)
                        text.Should().Contain("REFERENCE", $"{tool} still reads the rest of the turned line");
                    text.Should().Contain(Marker, $"{tool} still reads the upright marker");
                }
            }

            using (var doc = PdfDocument.Open(pdf))
            {
                // No covering box and no width closing, so the pixels inside the
                // term's quads show what glyph removal alone left there.
                doc.RedactText(Term, PixelOptions);
                var output = WriteTemp(doc.SaveToBytes());
                outputs.Add(output);
                using var after = MutoolReferenceRenderer.RenderPage(output, 1, Dpi);
                InkIn(after!, glyphs).Should().BeLessThan(0.001, "no ink is left inside the term's glyph quads");
            }
        }
        finally
        {
            foreach (var path in outputs)
                File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void PageText_ReadsALineTurnedByItsMatrix_AsOneLine(int degrees, int pageRotate)
    {
        RequireOracles();
        var pdf = BuildTurned(degrees, pageRotate);
        var input = WriteTemp(pdf);
        try
        {
            AssertReadersRead(input, TurnedLine, degrees);
            using var doc = PdfDocument.Open(pdf);
            var text = doc.GetPage(1).Text;
            Regex.Replace(text, @"\s+", " ").Should().Contain(TurnedLine,
                $"MuPDF and Poppler read the line turned {degrees} degrees as one line; excise read:\n{text}");
            text.Should().Contain(Marker);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void Issue14497_TheTurnedHeader_IsOneLine()
    {
        RequireOracles();
        var pdf = LoadIssue14497();
        var input = WriteTemp(pdf);
        try
        {
            foreach (var (tool, text) in Readings(input))
                text.Should().Contain("REFERENCE Parklands Phase 14B", $"fixture sanity: {tool} reads the header as one line");
        }
        finally
        {
            File.Delete(input);
        }

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        Regex.Replace(page.Text, @"\s+", " ").Should().Contain("REFERENCE Parklands Phase 14B",
            "MuPDF and Poppler read the header as one line (#2008)");

        // Find reads the page's words; a drag copies a reading-order range.
        page.GetWords().Select(w => w.Text).Should().ContainInOrder("REFERENCE", "Parklands", "Phase", "14B");
        var ordered = TextSelectionEngine.SortReadingOrder(page.Letters);
        var start = ordered.FindIndex(l => l.Value == "P"
            && string.Concat(ordered.Skip(ordered.IndexOf(l)).Take(Term.Length).Select(x => x.Value)) == Term);
        start.Should().BeGreaterThanOrEqualTo(0, "the header's glyphs read 'Parklands' in selection order");
        TextSelectionEngine.JoinText(
                TextSelectionEngine.RangeBetween(ordered, ordered[start], ordered[start + Term.Length - 1]))
            .Should().Be(Term, "a drag from the word's first glyph to its last copies exactly the word");
    }

    [Fact]
    public void Issue14497_Parklands_LeavesTheFile_AndThePixels()
    {
        RequireOracles();
        var pdf = LoadIssue14497();
        var glyphs = TermGlyphBoxes(pdf);
        var outputs = new List<string>();
        try
        {
            using (var doc = PdfDocument.Open(pdf))
            {
                var report = doc.RedactText(Term, RedactionOptions.Default);
                var saved = doc.SaveToBytes();
                var output = WriteTemp(saved);
                outputs.Add(output);
                report.VerifiedRemovals.Should().Be(1, report.ToString());
                SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty();
                foreach (var (tool, page) in Readings(output))
                {
                    page.Should().NotContain(Term, $"{tool} must not read the term");
                    page.Should().Contain("REFERENCE", $"{tool} still reads the header");
                }
            }

            using (var doc = PdfDocument.Open(pdf))
            {
                doc.RedactText(Term, PixelOptions);
                var output = WriteTemp(doc.SaveToBytes());
                outputs.Add(output);
                using var after = MutoolReferenceRenderer.RenderPage(output, 1, Dpi);
                InkIn(after!, glyphs).Should().BeLessThan(0.001);
            }
        }
        finally
        {
            foreach (var path in outputs)
                File.Delete(path);
        }
    }

    private static byte[] LoadIssue14497()
    {
        var pdf = RotationFixtures.TryLoad(RotationFixtures.Get("pdfjs-issue14497"), out var absence);
        Assert.SkipWhen(pdf == null, absence);
        return pdf!;
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed [requires: tool:pdftotext]");
        Assert.SkipUnless(MutoolStextGeometry.IsAvailable, "mutool not installed [requires: tool:mutool]");
    }

    /// <summary>The term's glyph quads as MuPDF places them on the displayed input page.</summary>
    private static IReadOnlyList<VisualRegion> TermGlyphBoxes(byte[] pdf)
    {
        var hits = MutoolStextGeometry.Read(pdf, 1).FindChars(Term);
        hits.Should().HaveCount(1, "MuPDF reads the term once in the input");
        return hits[0];
    }

    /// <summary>
    /// Fraction of inked pixels inside the glyph boxes, each shrunk by a fifth
    /// of its size so a turned box's corners do not reach a neighbour's ink.
    /// </summary>
    internal static double InkIn(SKBitmap bmp, IReadOnlyList<VisualRegion> boxes)
    {
        const double scale = Dpi / 72.0;
        int ink = 0, total = 0;
        foreach (var b in boxes)
        {
            var dx = b.Width * 0.2;
            var dy = b.Height * 0.2;
            int x0 = Math.Max(0, (int)Math.Ceiling((b.Left + dx) * scale));
            int x1 = Math.Min(bmp.Width - 1, (int)Math.Floor((b.Right - dx) * scale));
            int y0 = Math.Max(0, (int)Math.Ceiling((b.Top + dy) * scale));
            int y1 = Math.Min(bmp.Height - 1, (int)Math.Floor((b.Bottom - dy) * scale));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    total++;
                    if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
                }
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static IEnumerable<(string Tool, string Text)> Readings(string path)
    {
        foreach (var (tool, raw) in new[]
                 {
                     ("mutool", MutoolTextExtractor.ExtractPage(path, 1)),
                     ("pdftotext", PdftotextTextExtractor.ExtractPage(path, 1)),
                 })
        {
            raw.Should().NotBeNull($"{tool} must read {path}");
            yield return (tool, Regex.Replace(raw!, @"\s+", " "));
        }
    }

    /// <summary>
    /// Fixture sanity: MuPDF reads the turned line at every angle. Poppler
    /// reads it only at a quarter turn; off one it reads fragments, so it is
    /// not asked to.
    /// </summary>
    private static void AssertReadersRead(string path, string line, int degrees)
    {
        foreach (var (tool, text) in Readings(path))
            if (tool == "mutool" || degrees % 90 == 0)
                text.Should().Contain(line, $"fixture sanity: {tool} reads the line turned {degrees} degrees");
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-2008-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// One page: <see cref="TurnedLine"/> in one <c>Tj</c>, turned
    /// <paramref name="degrees"/> counterclockwise by its text matrix over a
    /// 14 pt Helvetica from the page centre, and an upright marker line. The
    /// page carries <c>/Rotate</c> only when <paramref name="pageRotate"/> is
    /// not 0, so the 0 case pins the absent-key default.
    /// </summary>
    private static byte[] BuildTurned(int degrees, int pageRotate)
    {
        string Num(double v) => Math.Round(v, 6).ToString(CultureInfo.InvariantCulture);
        var r = degrees * Math.PI / 180;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        var content =
            $"BT /F1 14 Tf {Num(c)} {Num(s)} {Num(-s)} {Num(c)} 306 396 Tm ({TurnedLine}) Tj ET\n" +
            $"BT /F1 12 Tf 72 72 Td ({Marker}) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                + (pageRotate != 0 ? $" /Rotate {pageRotate}" : "")
                + " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
