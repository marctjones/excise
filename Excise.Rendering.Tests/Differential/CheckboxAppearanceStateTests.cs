using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// An <c>/AS</c> naming a state that <c>/AP /N</c> does not define means DRAW
/// NOTHING — not "draw whatever else is in there".
///
/// HOW THIS WAS FOUND, AND WHY THAT MATTERS
///
/// Not by a test. By opening a blank IRS Form W-9 in the packaged app and
/// looking at the screenshot: every checkbox in section 3a had a tick in it.
/// mutool renders the same page with empty boxes.
///
/// The appearance resolver fell through to "first usable entry in /AP /N" when
/// <c>/AS</c> did not match. That is not an exotic malformation — it is how an
/// OFF checkbox is normally written. §12.5.5: <c>/AS</c> selects the appearance
/// from the sub-dictionary, and producers routinely omit <c>/Off</c> from
/// <c>/N</c> because "off" means there is nothing to draw. W-9 verbatim:
///
/// <code>
///   /AP &lt;&lt; /D &lt;&lt; /1 12 0 R /Off 11 0 R &gt;&gt;
///          /N &lt;&lt; /1 13 0 R &gt;&gt; &gt;&gt;     &lt;- no /Off
///   /AS /Off   /V /Off
/// </code>
///
/// So the fallback selected <c>/1</c> — the CHECKED appearance — and excise
/// ticked every unchecked box on a blank federal form. On a form-heavy document
/// that is not a cosmetic difference: it inverts the meaning of the page.
///
/// The lesson worth keeping: the entire annotation suite (2,827 tests) was green
/// through this. Ink-fraction and "did it draw something" assertions cannot tell
/// a tick from an empty box, because both are ink. A human looked at a picture.
/// </summary>
public class CheckboxAppearanceStateTests
{
    private const int Dpi = 100;

    /// <summary>
    /// The real-world witness, against an independent renderer. A blank W-9 must
    /// not gain ink that mutool does not draw.
    ///
    /// Asserted as "excise must not exceed mutool by much" rather than as an
    /// equality: excise deliberately draws an Acrobat-style highlight around
    /// unfilled form fields (#885) that mutool does not, so a small positive
    /// delta is expected and correct. Eight spurious ticks are not small.
    /// </summary>
    [Fact]
    public void BlankW9_AddsNoDarkInkInsideItsCheckboxes()
    {
        var path = FindCorpusFile("smoke", "irs-w9.pdf");
        Assert.SkipWhen(path == null, "gitignored smoke corpus fixture not present."); // [requires: corpus:smoke]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(path!);
        var boxes = doc.GetPage(1).GetAnnotations()
            .Where(a => a.RawDictionary.GetNameOrNull("FT") == "Btn")
            .Select(a => a.Rect)
            .ToList();
        boxes.Should().NotBeEmpty("the fixture must still contain /FT /Btn widgets");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();
        using var excise = Render(doc);

        double pageHeight = doc.GetPage(1).Height;
        int oracleDark = DarkInside(reference!, boxes, pageHeight);
        int exciseDark = DarkInside(excise, boxes, pageHeight);

        exciseDark.Should().BeLessThanOrEqualTo(oracleDark,
            $"excise put {exciseDark} dark pixels inside the checkboxes of a BLANK W-9 against " +
            $"mutool's {oracleDark}. Every box is /AS /Off and /AP /N defines no /Off appearance, " +
            "so there is nothing to draw; substituting the only entry in /N drew the CHECKED " +
            "appearance and inverted the meaning of the form");
    }

    /// <remarks>
    /// Asserted as a RATIO against the same fixture with <c>/AS /On</c>, not as
    /// "draws nothing". A widget with no resolvable appearance still gets
    /// excise's synthesized field chrome (#885) — a thin highlight border, which
    /// is correct and which Acrobat also draws. An <c>Ink == 0</c> assertion
    /// denies that behaviour and fails for the wrong reason; it did, at 888 px
    /// of chrome, on the first draft of this test.
    ///
    /// The ON appearance here is a filled 80×80 square, so substituting it
    /// produces ink of a completely different order. Comparing the two cases is
    /// what actually distinguishes "drew the chrome" from "drew the tick".
    /// </remarks>
    [Fact]
    public void AsNamingAnUndefinedState_DoesNotSubstituteTheDefinedOne()
    {
        var offPath = WriteTemp(CheckboxPdf(appearanceState: "Off"));
        var onPath = WriteTemp(CheckboxPdf(appearanceState: "On"));
        try
        {
            using var offDoc = PdfDocument.Open(offPath);
            using var offBmp = Render(offDoc);
            using var onDoc = PdfDocument.Open(onPath);
            using var onBmp = Render(onDoc);

            int off = Ink(offBmp), on = Ink(onBmp);

            on.Should().BeGreaterThan(5000,
                "the ON appearance is a filled 80x80 square and must render when /AS names it — " +
                "without this the fix could have made every checkbox invisible and the " +
                "assertion below would pass for the wrong reason");

            off.Should().BeLessThan(on / 4,
                "/AS /Off names a state /AP /N does not define, so the ON appearance must NOT " +
                "be substituted. Some ink is expected and correct — the synthesized field " +
                "highlight (#885) — but nothing of the order of the filled square");
        }
        finally { File.Delete(offPath); File.Delete(onPath); }
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One widget whose <c>/AP /N</c> defines only <c>/On</c> — a filled black
    /// square covering most of the page. <paramref name="appearanceState"/>
    /// selects whether <c>/AS</c> names it or names the undefined <c>/Off</c>.
    /// </summary>
    private static byte[] CheckboxPdf(string appearanceState)
    {
        const string onAp = "0 0 0 rg 0 0 80 80 re f";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [5 0 R] >>\nendobj\n",
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Btn /Rect [10 10 90 90] " +
            $"/AS /{appearanceState} /V /Off /AP << /N << /On 6 0 R >> >> >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 80 80] /Length {onAp.Length} >>" +
            $"\nstream\n{onAp}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

    private static int Ink(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200) n++;
            }
        return n;
    }

    /// <summary>
    /// Dark pixels inside the given PDF-space rectangles.
    ///
    /// DARK, not "ink": excise deliberately draws a light field highlight (#885)
    /// that mutool does not, so a plain ink count cannot separate "highlighted an
    /// empty box" from "drew a tick in it". A ZapfDingbats check mark is black.
    ///
    /// Scoped to the boxes because the difference is ~180 pixels against 66,000
    /// on the page — a page-level assertion cannot see it, which is how a green
    /// 2,827-test annotation suite missed this entirely.
    /// </summary>
    private static int DarkInside(
        SKBitmap b, IEnumerable<Excise.Core.Document.PdfRectangle> rects, double pageHeight)
    {
        double scale = Dpi / 72.0;
        int total = 0;
        foreach (var r in rects)
        {
            int x0 = (int)(Math.Min(r.Left, r.Right) * scale);
            int x1 = (int)(Math.Max(r.Left, r.Right) * scale);
            int y0 = (int)((pageHeight - Math.Max(r.Top, r.Bottom)) * scale);
            int y1 = (int)((pageHeight - Math.Min(r.Top, r.Bottom)) * scale);
            for (int y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(b.Width, x1); x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.Red < 120 && c.Green < 120 && c.Blue < 120) total++;
                }
        }
        return total;
    }


    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-cb-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
