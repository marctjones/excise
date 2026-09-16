using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1381 — a FreeText annotation with no <c>/AP</c> must not become completely invisible.
/// <b>Regression pins</b>: the defect below shipped fixed in <c>3ffc4e3e</c> (2026-09-09).
///
/// <para><b>The defect.</b> <c>RenderFreeTextDefault</c> held two deliberate, separately
/// measured behaviours that were never measured together:</para>
/// <list type="number">
/// <item><c>/Border [0 0 0]</c> suppresses the border — correct, and measured on
/// <c>bug1871353.pdf</c>, where forcing width 1 drew a box the file explicitly declined.</item>
/// <item>A codepoint above U+00FF suppressed the text — correct in intent, and measured on
/// <c>freetext_no_appearance.pdf</c>, where drawing it produced a row of tofu that reads
/// as "this document is corrupt" rather than "an annotation is here".</item>
/// </list>
///
/// <para>The guard's own comment claimed "the box and border still draw". With
/// <c>/Border [0 0 0]</c> they do not, so the annotation vanished entirely — which is what
/// <c>bug1865341.pdf#p1</c> did: zero ink on a page whose only content is one FreeText
/// carrying the Polish word <c>Załącznik</c>. <c>3ffc4e3e</c> made the guard a SCRIPT
/// question (<c>RequiresComplexShaping</c>) instead of a code-point one, and stopped
/// <c>RenderTextFieldValue</c> forcing the value through Latin-1 when it does not survive
/// the round trip.</para>
///
/// <para><b>Why these were skipped, and why they no longer are</b> (#1503). Both carried
/// <c>[Fact(Skip = "#1381 …")]</c>, and #1381 closed — so they satisfied #1172's gate
/// (which requires that a reason EXISTS, not that it is still TRUE) while verifying
/// nothing. An earlier revision of this comment also pointed at
/// <c>tests/skip-allowlist/Excise.Rendering.Tests.txt</c>; #1172 deleted that directory.</para>
///
/// <para>The bar is <b>mutool</b>, never excise's own prior output — a fixture excise
/// grades itself against cannot see an error excise holds consistently.</para>
///
/// <para><b>Overlap, stated so it is not mistaken for independent corroboration.</b>
/// <c>3ffc4e3e</c> shipped its own pin for the corpus page — the
/// <c>pdfjs/bug1865341.pdf</c> row of <c>BlankPageRecoveryTests</c>, which holds excise
/// inside 0.60–1.40 of the same-page-box oracle ink — and the policy row
/// <c>freetext.non-latin1-contents-without-border</c> in
/// <c>tests/annotation-synthesis-policy.json</c>. <see
/// cref="Bug1865341_InksSomethingWhereMutoolDraws"/> is strictly weaker than that ratio
/// pin and is kept as a cheap "did it vanish again" check.
/// <see cref="FreeTextWithNonLatin1Contents_IsStillVisible"/> is the part nothing else
/// covers: two synthetic fixtures differing by ONE character, which isolates the code
/// point as the cause rather than anything else about the corpus page.</para>
/// </summary>
public class FreeTextNonLatin1VisibilityTests
{
    private const int Dpi = 150;

    /// <summary>
    /// The isolated case. Two fixtures identical but for one character: an annotation
    /// whose text is all Latin-1 draws; before <c>3ffc4e3e</c> the same annotation with
    /// U+0142 drew nothing. Measured 2026-09-06 at 150 dpi, BEFORE the fix — 0.00354 ink
    /// for "aécè" vs 0.00000 for "ałcè" (and 0.00328 for plain "abcd").
    ///
    /// <para>U+0142 is Latin Extended-A, which <c>RequiresComplexShaping</c> excludes, so
    /// the fixture stays on the single-line <c>RenderTextFieldValue</c> path.</para>
    ///
    /// <para>⚠️ The two assertions fail for different reasons and must not be read as one.
    /// <c>beyondInk &gt; 0</c> failing means the annotation vanished again — a live
    /// rendering defect. The <c>BeApproximately</c> comparison failing while ink is
    /// non-zero means the glyph drew but at a different weight than "aécè" (a <c>.notdef</c>
    /// box, or a fallback face with different coverage), which is a calibration question
    /// about this fixture pair, not a vanished annotation.</para>
    /// </summary>
    [Fact]
    public void FreeTextWithNonLatin1Contents_IsStillVisible()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH."); // [requires: tool:mutool]

        using var latin1 = new TempPdf(BuildFreeTextFixture("<FEFF006100E9006300E8>"));   // "aécè"
        using var beyond = new TempPdf(BuildFreeTextFixture("<FEFF00610142006300E8>"));   // "ałcè"

        var latin1Ink = ExciseInkFraction(latin1.Path);
        var beyondInk = ExciseInkFraction(beyond.Path);

        beyondInk.Should().BeGreaterThan(0.0,
            "a FreeText annotation must not disappear because one character is outside Latin-1");
        beyondInk.Should().BeApproximately(latin1Ink, 0.002,
            "the two fixtures differ by a single character, so they must ink comparably");
    }

    /// <summary>
    /// The real-world page. Excise's own synthesis policy rows
    /// <c>freetext.without-color</c> and (since #1381)
    /// <c>freetext.non-latin1-contents-without-border</c> both say "draw"; before
    /// <c>3ffc4e3e</c> excise inked nothing while mutool drew the word. <c>3ffc4e3e</c>
    /// measured 0 → 628 inked px at 150 dpi against mutool's 697.
    ///
    /// <para>Deliberately weaker than <c>BlankPageRecoveryTests</c>' 0.60–1.40 ratio pin on
    /// the same fixture: this one only asks whether the annotation is still there at all,
    /// and it asks mutool first so a fixture that stops testing the thing says so.</para>
    /// </summary>
    [Fact]
    public void Bug1865341_InksSomethingWhereMutoolDraws()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH."); // [requires: tool:mutool corpus:pdfjs]

        var path = FindPdfjsFixture("bug1865341.pdf");
        Assert.SkipWhen(path == null,
            "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();
        InkFraction(reference!).Should().BeGreaterThan(0.0,
            "mutool draws the FreeText text — if it does not, this fixture no longer tests what it was chosen for");

        ExciseInkFraction(path!).Should().BeGreaterThan(0.0,
            "the page's only content is a FreeText annotation, so zero ink means it vanished");
    }

    private static double ExciseInkFraction(string pdfPath)
    {
        using var document = Excise.Core.Document.PdfDocument.Open(pdfPath);
        using var bitmap = new Excise.Rendering.SkiaRenderer().RenderPage(
            document.Pages[0],
            new Excise.Rendering.RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
        return bitmap == null ? 0.0 : InkFraction(bitmap);
    }

    private static double InkFraction(SKBitmap bitmap)
    {
        var inked = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Red + p.Green + p.Blue < 720) inked++;
        }
        return (double)inked / (bitmap.Width * bitmap.Height);
    }

    private static string? FindPdfjsFixture(string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", "pdfjs"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// The shape of <c>bug1865341.pdf</c>: no page contents, one FreeText with no /AP,
    /// <c>/Border [0 0 0]</c>, and an offset CropBox — reduced to the parts that matter.
    /// </summary>
    private static byte[] BuildFreeTextFixture(string contentsToken)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/CropBox [92 608 321.33 706.67] /Resources << >> /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FreeText /F 4 /Rect [104.01 673.88 152.66 693.84] " +
            $"/DA (/Helv 10 Tf 0 g) /Contents {contentsToken} /Border [0 0 0] >>",
        };

        var buffer = new MemoryStream();
        void Write(string s) => buffer.Write(System.Text.Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(buffer.Position);
            Write($"{i + 1} 0 obj {objects[i]}\nendobj\n");
        }

        var xref = buffer.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return buffer.ToArray();
    }

    private sealed class TempPdf : IDisposable
    {
        public string Path { get; }

        public TempPdf(byte[] bytes)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"excise-freetext-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(Path, bytes);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort */ }
        }
    }
}
