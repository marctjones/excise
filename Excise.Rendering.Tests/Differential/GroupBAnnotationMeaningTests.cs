using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// GROUP B (#1053): annotations with NO <c>/AP</c>. §12.5.5 says a viewer MAY
/// synthesize an appearance and the spec declines to say what it should look
/// like, so <b>pixel agreement is the wrong instrument</b> — measured unanimity
/// across five engines on these cases is 57.8%, and it FELL when the pool grew,
/// which is the honest direction for a question nobody specified.
///
/// <para>So these judge MEANING, and every property below has an unambiguous
/// answer that does not depend on taste:</para>
///
/// <list type="number">
///   <item><b>TEXT</b> — the text an annotation contributes is parsed
///     correctly. Checked against the file's own bytes, not against excise's
///     second opinion.</item>
///   <item><b>CONTAINMENT</b> — synthesized ink stays inside <c>/Rect</c>.
///     Structural, every engine agrees, and excise has violated it twice
///     (#991 drew mirrored, #1004 overshot a highlight).</item>
///   <item><b>PRESENCE</b> — <c>/F</c> Hidden and NoView mean draw nothing.
///     Binary (§12.5.3).</item>
///   <item><b>NO INVENTED INK</b> — excise must not draw where NO independent
///     renderer draws. The spec's latitude permits excise to differ from the
///     others; it does not permit conjuring content onto a page nobody else
///     shows. For a redaction tool that is the dangerous direction: invented
///     ink can cover content a reviewer needed to see.</item>
/// </list>
///
/// <para>⚠️ What is deliberately NOT asserted: that excise's synthesized
/// artwork resembles anyone else's. That is #1015's settled position — where a
/// majority draws and the drawers disagree with each other, copying any of them
/// elects a renderer.</para>
/// </summary>
public class GroupBAnnotationMeaningTests
{
    private const int Dpi = 150;
    private const int Tile = 10;

    /// <summary>
    /// pdf.js fixtures whose annotations carry NO <c>/AP</c> — verified by
    /// scanning for <c>/AP &lt;&lt;</c> through inflated object streams, not by
    /// trusting the file names.
    /// </summary>
    public static TheoryData<string> GroupBFixtures()
    {
        var d = new TheoryData<string>();
        foreach (var n in new[]
                 {
                     "annotation-border-styles.pdf",
                     "annotation-highlight-without-appearance.pdf",
                     "annotation-ink-without-appearance.pdf",
                     "annotation-line-without-appearance.pdf",
                     "annotation-polyline-polygon-without-appearance.pdf",
                     "annotation-square-circle-without-appearance.pdf",
                     "annotation-squiggly-without-appearance.pdf",
                     "annotation-strikeout-without-appearance.pdf",
                     "annotation-underline-without-appearance.pdf",
                     "annotation-tx.pdf",
                 })
            d.Add(n);
        return d;
    }

    // ---------------------------------------------------------------- TEXT

    [Theory]
    [MemberData(nameof(GroupBFixtures))]
    public void TheTextAnAnnotationCarries_IsParsedFromTheFileNotInvented(string fixture)
    {
        var path = Resolve(fixture);
        Assert.SkipWhen(path == null, "pdf.js corpus not present");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        var haystack = FileTextCarriers(path!);

        var checkedAny = 0;
        var failures = new List<string>();

        for (var p = 1; p <= doc.PageCount; p++)
            foreach (var a in doc.GetPage(p).GetAnnotations())
            {
                foreach (var (label, value) in new[] { ("/Contents", a.Contents), ("/T", a.Author) })
                {
                    if (string.IsNullOrWhiteSpace(value) || value!.Length < 3) continue;
                    checkedAny++;
                    // The value excise reports must actually be IN the file.
                    // This cannot be satisfied by excise agreeing with itself.
                    if (!haystack.Contains(value, StringComparison.Ordinal))
                        failures.Add($"page {p} {a.Subtype} {label}: excise reports \"{value}\" " +
                                     "but that string is not in the file's bytes");
                }
            }

        Assert.SkipWhen(checkedAny == 0, $"{fixture} has no annotation text to check");
        failures.Should().BeEmpty($"{fixture}: annotation text must be parsed, not invented.\n" +
                                  string.Join("\n", failures));
    }

    // --------------------------------------------------------- CONTAINMENT

    [Theory]
    [MemberData(nameof(GroupBFixtures))]
    public void SynthesizedInkStaysInsideTheAnnotationRect(string fixture)
    {
        var path = Resolve(fixture);
        Assert.SkipWhen(path == null, "pdf.js corpus not present");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        var page = doc.GetPage(1);
        var annots = page.GetAnnotations()
                         .Where(a => a.Subtype != PdfAnnotationSubtype.Popup)
                         .ToList();
        Assert.SkipWhen(annots.Count == 0, "no annotations on page 1");

        // Difference the annotation layer against the page WITHOUT it, so page
        // content is not mistaken for synthesized annotation ink.
        using var withAnnots = Render(page, annotations: true);
        using var without = Render(page, annotations: false);

        var added = AddedTiles(withAnnots, without);
        Assert.SkipWhen(added.Count == 0, "excise synthesizes nothing here; containment is vacuous");

        var allowed = new HashSet<(int, int)>();
        foreach (var a in annots)
            foreach (var t in RectTiles(a.Rect, page.CropBox.Height))
                allowed.Add(t);

        var outside = added.Except(allowed).ToList();

        outside.Should().BeEmpty(
            $"{fixture}: annotation ink must stay inside some /Rect — excise has broken this " +
            $"twice (#991 mirrored, #1004 overshot). {outside.Count} of {added.Count} added tiles " +
            $"are outside every /Rect: {string.Join(", ", outside.Take(8))}");
    }

    // ------------------------------------------------------------ PRESENCE

    [Fact]
    public void HiddenAndNoViewAnnotations_DrawNothing()
    {
        // Synthetic, because the corpus fixtures are all visible and this is a
        // binary property the spec states outright (§12.5.3).
        foreach (var (name, flags) in new[] { ("Hidden", 2), ("NoView", 32) })
        {
            var pdf = SquareWithFlags(flags);
            using var doc = PdfDocument.Open(pdf);
            var page = doc.GetPage(1);

            using var withAnnots = Render(page, annotations: true);
            using var without = Render(page, annotations: false);

            AddedTiles(withAnnots, without).Should().BeEmpty(
                $"/F {name} means the annotation shall not be displayed (§12.5.3), " +
                "so it must contribute no ink at all");
        }
    }

    [Fact]
    public void AVisibleAnnotation_DoesDrawSomething()
    {
        // The control for the test above. Without it, a renderer that drew NO
        // annotations ever would pass the Hidden/NoView assertions perfectly.
        var pdf = SquareWithFlags(4 /* Print */);
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        using var withAnnots = Render(page, annotations: true);
        using var without = Render(page, annotations: false);

        AddedTiles(withAnnots, without).Should().NotBeEmpty(
            "a visible Square with /C and /IC must synthesize something, or the " +
            "Hidden/NoView assertions above prove nothing");
    }

    // -------------------------------------------------- STATED COLOUR VALUES

    /// <summary>
    /// #1055. Group B gives excise latitude over what a synthesized appearance
    /// LOOKS like. It gives none over values the file states outright: <c>/C</c>
    /// is the border colour and <c>/IC</c> the interior (§12.5.6.8 Table 178),
    /// and ignoring one is dropping data, not choosing an aesthetic.
    ///
    /// <para>This needs no oracle and deliberately does not use one — the
    /// question is whether excise used the number in the file, which has one
    /// right answer. It exists because reverting the <c>/IC</c> fix left the
    /// entire Group B suite green: every other property here is either
    /// one-directional (excise must not draw where nobody does) or structural,
    /// and none of them notices excise drawing LESS than the file asked for.</para>
    /// </summary>
    [Fact]
    public void StatedBorderAndInteriorColours_AreBothUsed()
    {
        var pdf = SquareWithColours(interior: "[1 0 0]", border: "[0 0 1]");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = Render(doc.GetPage(1), annotations: true);

        var scale = Dpi / 72.0;
        // /Rect [40 40 160 160] on a 200pt page; sample the middle for the
        // fill and the middle of the top edge for the stroke.
        var centre = bmp.GetPixel((int)(100 * scale), (int)((200 - 100) * scale));
        var edge = bmp.GetPixel((int)(100 * scale), (int)((200 - 160) * scale));

        centre.Red.Should().BeGreaterThan(180, "/IC [1 0 0] must fill the interior red");
        centre.Blue.Should().BeLessThan(80, "the interior must not be the BORDER colour");

        edge.Blue.Should().BeGreaterThan(180, "/C [0 0 1] must stroke the border blue");
        edge.Red.Should().BeLessThan(80, "the border must not be the INTERIOR colour");
    }

    [Fact]
    public void AnInteriorColourAlone_StillFills()
    {
        // A shape may carry /IC and no /C. The border needs /C, so the fill is
        // the whole appearance — an early-return on a missing /C would drop it.
        var pdf = SquareWithColours(interior: "[0 1 0]", border: null);
        using var doc = PdfDocument.Open(pdf);
        using var bmp = Render(doc.GetPage(1), annotations: true);

        var scale = Dpi / 72.0;
        var centre = bmp.GetPixel((int)(100 * scale), (int)((200 - 100) * scale));

        // ⚠️ Green > 180 ALONE is satisfied by a white page, so this assertion
        // passed with the /IC fill reverted. The other two channels are what
        // make it mean "green" rather than "not black".
        centre.Green.Should().BeGreaterThan(180, "/IC alone must still paint the interior");
        centre.Red.Should().BeLessThan(80, "the interior must be GREEN, not merely bright");
        centre.Blue.Should().BeLessThan(80, "the interior must be GREEN, not merely bright");
    }

    // ---------------------------------------- UNSTATED COLOUR: SERVE THE READER

    /// <summary>
    /// A <c>/Highlight</c> with no <c>/C</c> is drawn YELLOW, deliberately
    /// diverging from mutool and pdftocairo, which both paint it black.
    ///
    /// <para>§12.5.6.10 defines Highlight as markup that "shall appear as
    /// highlights"; §12.5.5 leaves the appearance unspecified without an
    /// <c>/AP</c>. So neither colour is prescribed — but rendered over body
    /// text, an uncoloured Highlight in mutool is <b>indistinguishable from a
    /// redaction bar</b>. For a redaction tool, showing a reviewer a black bar
    /// over text that has NOT been redacted invites precisely the wrong
    /// conclusion about what the document still contains.</para>
    ///
    /// <para>This pins the divergence so it is a decision rather than an
    /// accident. It was briefly changed to black on the reasoning that an
    /// unstated colour means the initial graphics state (§8.6.8) — that was a
    /// description of mutool's behaviour dressed up as a rule, and the rendered
    /// comparison is what disproved it.</para>
    /// </summary>
    [Fact]
    public void AnUncolouredHighlight_IsYellowNotABlackBarOverTheText()
    {
        var pdf = BuildSquare("/Subtype /Highlight /F 4 /Rect [40 90 160 120] " +
                              "/QuadPoints [40 120 160 120 40 90 160 90]")
                  .ToArray();

        using var doc = PdfDocument.Open(pdf);
        using var bmp = Render(doc.GetPage(1), annotations: true);

        var scale = Dpi / 72.0;
        var inside = bmp.GetPixel((int)(100 * scale), (int)((200 - 105) * scale));

        inside.Red.Should().BeGreaterThan(180, "an uncoloured highlight must be YELLOW");
        inside.Green.Should().BeGreaterThan(180, "an uncoloured highlight must be YELLOW");
        inside.Blue.Should().BeLessThan(80,
            "yellow, not white — and emphatically not the black mutool draws, which " +
            "would read as a redaction bar over text that is still present");
    }

    // ------------------------------------------------------ NO INVENTED INK

    [Theory]
    [MemberData(nameof(GroupBFixtures))]
    public void ExciseDoesNotDrawWhereNoIndependentRendererDraws(string fixture)
    {
        var path = Resolve(fixture);
        Assert.SkipWhen(path == null, "pdf.js corpus not present");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var oracles = new[]
        {
            MutoolReferenceRenderer.RenderPage(path!, 1, Dpi),
            PdftocairoReferenceRenderer.IsAvailable ? PdftocairoReferenceRenderer.RenderPage(path!, 1, Dpi) : null,
            GhostscriptReferenceRenderer.IsAvailable ? GhostscriptReferenceRenderer.RenderPage(path!, 1, Dpi) : null,
            PdfiumNativeReferenceRenderer.IsAvailable
                ? PdfiumNativeReferenceRenderer.RenderPage(path!, 1, Dpi, null, renderAnnotations: true) : null,
        }.Where(b => b != null).Select(b => b!).ToList();

        Assert.SkipWhen(oracles.Count < 3, "a verdict needs three renderers (#976)");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        var page = doc.GetPage(1);
        var annots = page.GetAnnotations().Where(a => a.Subtype != PdfAnnotationSubtype.Popup).ToList();
        Assert.SkipWhen(annots.Count == 0, "no annotations on page 1");

        try
        {
            var anyOracleInks = new HashSet<(int, int)>();
            foreach (var o in oracles)
                foreach (var t in InkedTiles(o)) anyOracleInks.Add(t);

            using var withAnnots = Render(page, annotations: true);
            using var without = Render(page, annotations: false);
            var added = AddedTiles(withAnnots, without);

            // Only the tiles inside an annotation /Rect are this test's business.
            var inRect = new HashSet<(int, int)>();
            foreach (var a in annots)
                foreach (var t in RectTiles(a.Rect, page.CropBox.Height)) inRect.Add(t);

            // ADJACENCY, not a numeric tolerance. A stroke excise draws one
            // tile away from where another engine drew it is a position
            // difference, not invented content — and tuning a count until the
            // suite goes green would just hide the distinction. A tile counts
            // as invented only when NO oracle inked it OR any of its eight
            // neighbours: that is ink in a region nobody touched.
            bool NearAnyOracleInk((int X, int Y) t)
            {
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (anyOracleInks.Contains((t.X + dx, t.Y + dy))) return true;
                return false;
            }

            var invented = added.Intersect(inRect).Where(t => !NearAnyOracleInk(t)).ToList();

            // ANY oracle inking the tile is enough to acquit — this asks whether
            // excise is alone, not whether it matches a majority. Group B
            // explicitly permits differing from the majority.
            invented.Should().BeEmpty(
                $"{fixture}: excise inked {invented.Count} tiles inside an annotation /Rect that " +
                "NOT ONE of the independent renderers inked. The spec's latitude permits excise to " +
                "draw differently; it does not permit drawing content nobody else shows.");
        }
        finally
        {
            foreach (var o in oracles) o.Dispose();
        }
    }

    // ------------------------------------------------------------- helpers

    private static SKBitmap Render(PdfPage page, bool annotations)
    {
        var b = new SkiaRenderer().RenderPage(page, new RenderOptions
        {
            Dpi = Dpi,
            RenderAnnotations = annotations,
        });
        b.Should().NotBeNull("excise must render the page");
        return b!;
    }

    private static HashSet<(int X, int Y)> AddedTiles(SKBitmap with, SKBitmap without)
        => InkedTiles(with).Except(InkedTiles(without)).ToHashSet();

    private static HashSet<(int X, int Y)> InkedTiles(SKBitmap bmp)
    {
        var tiles = new HashSet<(int, int)>();
        for (var ty = 0; ty * Tile < bmp.Height; ty++)
            for (var tx = 0; tx * Tile < bmp.Width; tx++)
            {
                var inked = 0;
                for (var y = ty * Tile; y < Math.Min((ty + 1) * Tile, bmp.Height); y++)
                    for (var x = tx * Tile; x < Math.Min((tx + 1) * Tile, bmp.Width); x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.Alpha > 128 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) inked++;
                    }
                if (inked > Tile * Tile / 20) tiles.Add((tx, ty));
            }
        return tiles;
    }

    /// <summary>Tiles covering <paramref name="r"/>, with one tile of slack for a stroke on the boundary.</summary>
    private static IEnumerable<(int X, int Y)> RectTiles(PdfRectangle r, double pageHeight)
    {
        var scale = Dpi / 72.0;
        var x0 = Math.Min(r.Left, r.Right) * scale;
        var x1 = Math.Max(r.Left, r.Right) * scale;
        var yTop = (pageHeight - Math.Max(r.Bottom, r.Top)) * scale;
        var yBot = (pageHeight - Math.Min(r.Bottom, r.Top)) * scale;

        for (var ty = (int)(yTop / Tile) - 1; ty <= (int)(yBot / Tile) + 1; ty++)
            for (var tx = (int)(x0 / Tile) - 1; tx <= (int)(x1 / Tile) + 1; tx++)
                if (tx >= 0 && ty >= 0) yield return (tx, ty);
    }

    /// <summary>
    /// The file's text carriers as one searchable string: raw bytes plus every
    /// inflatable stream, in Latin-1 and UTF-16BE. Independent of excise's
    /// parser — the point is to catch a value excise reports that the file does
    /// not contain.
    /// </summary>
    private static string FileTextCarriers(string path)
    {
        var data = File.ReadAllBytes(path);
        var sb = new StringBuilder();
        void Add(byte[] b)
        {
            sb.Append(Encoding.Latin1.GetString(b)).Append('\n');

            // BOTH byte alignments. Decoding a whole buffer as UTF-16BE from
            // offset 0 only finds strings that happen to start on an even
            // boundary — every /Contents beginning at an odd offset decodes to
            // garbage. That is not hypothetical: it failed five fixtures whose
            // text excise had parsed perfectly, and the bug was here.
            sb.Append(Encoding.BigEndianUnicode.GetString(b)).Append('\n');
            if (b.Length > 1)
                sb.Append(Encoding.BigEndianUnicode.GetString(b, 1, b.Length - 1)).Append('\n');
        }
        Add(data);

        var i = 0;
        while (true)
        {
            var s = IndexOf(data, "stream", i);
            if (s < 0) break;
            var body = s + 6;
            if (body < data.Length && data[body] == (byte)'\r') body++;
            if (body < data.Length && data[body] == (byte)'\n') body++;
            var e = IndexOf(data, "endstream", body);
            if (e < 0) break;

            var raw = new byte[e - body];
            Array.Copy(data, body, raw, 0, raw.Length);
            foreach (var zlib in new[] { true, false })
            {
                try
                {
                    using var input = new MemoryStream(raw);
                    using Stream dec = zlib ? new ZLibStream(input, CompressionMode.Decompress)
                                            : new DeflateStream(input, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    dec.CopyTo(outMs);
                    if (outMs.Length > 0) { Add(outMs.ToArray()); break; }
                }
                catch (InvalidDataException) { }
                catch (NotSupportedException) { }
            }
            i = e + 9;
        }
        return sb.ToString();
    }

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        var pat = Encoding.ASCII.GetBytes(needle);
        for (var i = Math.Max(0, from); i <= haystack.Length - pat.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pat.Length; j++)
                if (haystack[i + j] != pat[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static byte[] SquareWithColours(string interior, string? border)
    {
        var c = border == null ? "" : $" /C {border}";
        return BuildSquare($"/F 4 /Rect [40 40 160 160]{c} /IC {interior} /BS << /W 3 >>");
    }

    private static byte[] SquareWithFlags(int flags)
    {
        return BuildSquare($"/F {flags} /Rect [40 40 160 160] /C [1 0 0] /IC [0 0 1] /BS << /W 3 >>");
    }

    private static byte[] BuildSquare(string annotBody)
    {
        var objs = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n<< /Type /Annot {(annotBody.Contains("/Subtype") ? "" : "/Subtype /Square ")}{annotBody} >>\nendobj\n",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new List<int>();
        foreach (var o in objs) { offs.Add(sb.Length); sb.Append(o); }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string? Resolve(string fixture)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var c = Path.Combine(dir, "test-pdfs", "pdfjs", fixture);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
