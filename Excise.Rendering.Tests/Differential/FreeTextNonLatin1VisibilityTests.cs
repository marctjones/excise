using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1381 — a FreeText annotation with no <c>/AP</c> must not become completely invisible.
///
/// <para><b>The defect.</b> <c>RenderFreeTextDefault</c> holds two deliberate, separately
/// measured behaviours that were never measured together:</para>
/// <list type="number">
/// <item><c>/Border [0 0 0]</c> suppresses the border — correct, and measured on
/// <c>bug1871353.pdf</c>, where forcing width 1 drew a box the file explicitly declined.</item>
/// <item>A codepoint above U+00FF suppresses the text — correct in intent, and measured on
/// <c>freetext_no_appearance.pdf</c>, where drawing it produced a row of tofu that reads
/// as "this document is corrupt" rather than "an annotation is here".</item>
/// </list>
///
/// <para>The guard's own comment claims "the box and border still draw". With
/// <c>/Border [0 0 0]</c> they do not, and the annotation vanishes entirely — which is
/// what <c>bug1865341.pdf#p1</c> does today: zero ink on a page whose only content is one
/// FreeText carrying the Polish word <c>Załącznik</c>.</para>
///
/// <para><b>These tests are skipped until #1381 is fixed.</b> They verify nothing in the
/// meantime; that is stated plainly rather than papered over with an assertion of current
/// behaviour, which would pin the defect in place. The allow-list entries in
/// <c>tests/skip-allowlist/Excise.Rendering.Tests.txt</c> cite the issue.</para>
///
/// <para>The bar is <b>mutool</b>, never excise's own prior output — a fixture excise
/// grades itself against cannot see an error excise holds consistently.</para>
/// </summary>
public class FreeTextNonLatin1VisibilityTests
{
    private const int Dpi = 150;

    /// <summary>
    /// The isolated case. Two fixtures identical but for one character: an annotation
    /// whose text is all Latin-1 draws; the same annotation with U+0142 draws nothing.
    /// Measured 2026-09-06 at 150 dpi — 0.00354 ink vs 0.00000.
    /// </summary>
    [Fact(Skip = "#1381: a codepoint above U+00FF drops the whole FreeText when /Border is 0.")]
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
    /// The real-world page. Excise's own synthesis policy row
    /// <c>freetext.without-color</c> says "draw"; today excise inks nothing while mutool
    /// draws the word.
    /// </summary>
    [Fact(Skip = "#1381: bug1865341.pdf renders zero ink; see FreeTextWithNonLatin1Contents_IsStillVisible.")]
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
