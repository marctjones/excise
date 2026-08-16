using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #889 — settling "should a viewer invent an appearance for an annotation with
/// no <c>/AP</c>?" by the method the issue itself named: a third opinion.
///
/// THE PROBLEM #889 WAS FILED ON
///
/// mutool and pdftocairo disagree about this, and they disagree in BOTH
/// directions — mutool draws Redact/Widget/Sound/FileAttachment and not
/// Line/Ink/PolyLine/Link; pdftocairo does the reverse. So "match the oracle"
/// has no meaning here, and the corpus gate's most-inked rule (#883) silently
/// made the more permissive renderer the standard. §12.5.5 says a reader
/// SHOULD generate an appearance, not that it must, and says nothing about what
/// a Sound or FileAttachment icon looks like. There is no answer to copy.
///
/// The gate has since been fixed to score ink locality by MAJORITY (#932), so
/// the 21 pages this file is about no longer report as excise defects. The
/// knowledge below is what that fix was built from; keep it, because the gate
/// now DEPENDS on excise continuing to sit with the majority — if excise
/// started synthesizing appearances the majority does not draw, the gate would
/// not catch it and this file is what would.
///
/// WHAT SETTLED IT
///
/// Ghostscript is a third independent engine (pdftoppm is Poppler again, so it
/// is not a second vote). Measured at 72 dpi, inked pixels, page 1:
///
/// <code>
///  fixture                    subtype          excise  mutool  cairo     gs   majority
///  6-3-3-t01-fail-d           Line                  0       0    111      0   blank
///  6-3-3-t01-fail-o           Ink                   0       0    444      0   blank
///  6-3-3-t01-fail-h           PolyLine              0       0    351      0   blank
///  bug_821454                 Link                  0       0    520      0   blank
///  6-3-3-t01-fail-b           Redact                0    5596      0      0   blank
///  redact_annot               Redact                0     268      0      0   blank
///  6-3-3-t01-fail-q           Sound                 0      58      0      0   blank
///  6-3-3-t01-fail-p           FileAttachment        0      48      0      0   blank
///  isartor-6-6-1-t01-fail-a   Link               1160       0   1160   1716   DRAWS
///  6-3-3-t01-fail-p (2nd)     Widget             3023     201   2860   3024   DRAWS
///  calculate                  Widget                0      61      0     46   DRAWS  <- the one gap
/// </code>
///
/// On 12 of 13 rows excise already matched the 2-of-3 majority. The lone
/// exception was <c>calculate.pdf</c>, fixed alongside this file. So #889's
/// answer is not "pick a side" — it is that excise was right nearly everywhere,
/// and the single-oracle comparison had been mis-scoring it.
///
/// WHY THESE ASSERTIONS ARE SHAPED THIS WAY
///
/// They assert AGREEMENT WITH THE MAJORITY, computed at runtime from the
/// oracles present, not a hard-coded ink number. A number baked in from one
/// machine's mutool would pin a measurement rather than a property, and this
/// repo has already been bitten by mutool differing across platforms
/// (RTL/spacing on macOS vs Linux). The numbers above are documentation of what
/// was seen, not the thing being checked.
/// </summary>
public class AnnotationSynthesisMajorityTests
{
    private const int Dpi = 72;

    /// <summary>
    /// The subtypes where the majority draws NOTHING. excise must not start
    /// inventing appearances for them — and since #932 the corpus gate scores
    /// these pages by majority too, so it would no longer flag it if excise
    /// did. This is the check that would.
    /// </summary>
    [Theory]
    [InlineData("verapdf-corpus", "veraPDF test suite 6-3-3-t01-fail-d.pdf", "Line")]
    [InlineData("verapdf-corpus", "veraPDF test suite 6-3-3-t01-fail-o.pdf", "Ink")]
    [InlineData("verapdf-corpus", "veraPDF test suite 6-3-3-t01-fail-h.pdf", "PolyLine")]
    [InlineData("pdfium", "redact_annot.pdf", "Redact")]
    [InlineData("verapdf-corpus", "veraPDF test suite 6-3-3-t01-fail-q.pdf", "Sound")]
    public void WhereTheMajorityDrawsNothing_ExciseDrawsNothing(
        string corpus, string name, string subtype)
    {
        var path = FindCorpusFile(corpus, name);
        Assert.SkipWhen(path == null, $"gitignored {corpus} corpus fixture not present."); // [requires: corpus:verapdf-corpus corpus:pdfium]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        int drawing = CountOraclesThatDraw(path!);
        Assert.SkipWhen(drawing < 0, "fewer than two independent oracles available to form a majority");

        drawing.Should().BeLessThan(2,
            $"this test's premise is that the {subtype} majority is BLANK. If two engines now " +
            "draw it, the majority has moved and excise should follow it — re-measure #889 " +
            "rather than deleting this assertion");

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc);
        Ink(excise).Should().Be(0,
            $"a /{subtype} annotation with no /AP has no appearance the spec defines. Inventing " +
            "one means siding with a single renderer against two others — #889 exists because " +
            "the corpus gate's most-inked rule made that look like a fix, and since #932 the " +
            "gate scores by majority and would not object at all");
    }

    /// <summary>
    /// The other direction, and the one real gap #889 turned up: a text field
    /// carrying a VALUE. Unlike a Sound icon, what to draw is fully specified by
    /// the file — <c>/V</c> is the content — so a viewer declining to draw it is
    /// losing information rather than declining to invent it.
    ///
    /// <c>calculate.pdf</c> has two <c>/FT /Tx</c> widgets with <c>/V (5)</c> and
    /// <c>/V (2)</c>, no <c>/AP</c>, no <c>/MK</c>, and no <c>/DA</c> anywhere in
    /// the file. excise had the value-rendering path already and bailed out of it
    /// on the missing <c>/DA</c>.
    /// </summary>
    [Theory]
    [InlineData("pdfium", "calculate.pdf")]
    [InlineData("isartor", "isartor-6-6-1-t01-fail-a.pdf")]
    public void WhereTheMajorityDraws_ExciseDraws(string corpus, string name)
    {
        var path = FindCorpusFile(corpus, name);
        Assert.SkipWhen(path == null, $"gitignored {corpus} corpus fixture not present."); // [requires: corpus:pdfium corpus:isartor]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        int drawing = CountOraclesThatDraw(path!);
        Assert.SkipWhen(drawing < 0, "fewer than two independent oracles available to form a majority");

        drawing.Should().BeGreaterThanOrEqualTo(2,
            "this test's premise is that a majority of independent engines draw this page");

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc);
        Ink(excise).Should().BeGreaterThan(0,
            "two of three independent engines draw this and excise must not be the outlier — " +
            "for a text field the content is defined by /V, so declining to draw it loses " +
            "information the file actually carries");
    }

    /// <summary>
    /// The control that keeps the fix honest. An EMPTY text field with no /AP
    /// must still render nothing.
    ///
    /// Without this, the /DA fix would be satisfied by a build that drew a box
    /// or a stray baseline for every unfilled form field — and real government
    /// forms are mostly unfilled fields, so that would diverge from mutool on
    /// exactly the documents that matter most.
    /// </summary>
    [Fact]
    public void AnEmptyTextField_StillDrawsNothing()
    {
        var path = FindCorpusFile("pdfium", "calculate.pdf");
        Assert.SkipWhen(path == null, "gitignored pdfium corpus fixture not present."); // [requires: corpus:pdfium]

        // Same document with the values removed: whatever ink appears must come
        // from /V, not from the field chrome.
        var stripped = File.ReadAllBytes(path!);
        var text = System.Text.Encoding.Latin1.GetString(stripped)
            .Replace("/V (5)", "/V ()  ")
            .Replace("/V (2)", "/V ()  ");
        var tmp = Path.Combine(Path.GetTempPath(), $"excise-889-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(tmp, System.Text.Encoding.Latin1.GetBytes(text));
        try
        {
            using var doc = PdfDocument.Open(tmp);
            using var bmp = Render(doc);
            Ink(bmp).Should().Be(0,
                "an unfilled field must stay invisible — drawing chrome for every empty widget " +
                "is what the /MK-only policy in RenderWidgetDefault exists to prevent, and it " +
                "would change every government form excise renders");
        }
        finally { File.Delete(tmp); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How many INDEPENDENT engines draw ink on page 1. Returns -1 when fewer
    /// than two are available, so a majority cannot be formed.
    ///
    /// Deliberately excludes pdftoppm: it is Poppler, the same engine as
    /// pdftocairo, and counting it would turn a 1-1 split into a fake 2-1.
    /// </summary>
    private static int CountOraclesThatDraw(string path)
    {
        int available = 0, drawing = 0;

        if (MutoolReferenceRenderer.IsAvailable)
        {
            available++;
            using var b = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null && Ink(b) > 0) drawing++;
        }
        if (PdftocairoReferenceRenderer.IsAvailable)
        {
            available++;
            using var b = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null && Ink(b) > 0) drawing++;
        }
        if (GhostscriptReferenceRenderer.IsAvailable)
        {
            available++;
            using var b = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null && Ink(b) > 0) drawing++;
        }

        return available >= 2 ? drawing : -1;
    }

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

    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
