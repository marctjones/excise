using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #973 — one malformed object must not cost the whole page.
///
/// pdfium's <c>bug_481363.pdf</c> writes its /ColorSpace object as
/// <c>[ /Lab 4&lt; /WhitePoint … &gt;&gt; ]</c>. The stray <c>4</c> makes the
/// lexer read <c>&lt;</c> as a hex string and the first <c>/</c> inside it is
/// not a hex digit, so <c>PdfDocument.GetObject</c> threw
/// <c>PdfParseException: Invalid hex digit '/'</c>. That object is reachable
/// only from the page's <c>/Resources /ColorSpace</c>, so the throw surfaced
/// out of a resource lookup and refused the ENTIRE page while other readers
/// rendered one. It was the pdfium corpus's only <c>EXCISE_SIDE_GAP</c>.
///
/// The fixture below reproduces the malformation byte for byte over a page
/// whose content stream is one filled rectangle, so the coverage does not
/// depend on the gitignored corpus. Measured at 72 dpi on that fixture (whose
/// content stream selects the broken /CS1, as the corpus file's does), all
/// five renderers agree EXACTLY — 10,000 inked px, bbox (100,200)-(199,299):
///
///     excise 10000   mutool 10000   pdftocairo 10000   pdftoppm 10000   gs 10000
///
/// On the real corpus file the agreement is bounded by a second, unrelated
/// malformation (its page object is <c>2 0 obj &lt;&lt; &lt;&lt;</c>, which
/// mutool declines to load at all, so mutool produces a blank page there):
/// excise 10400 px / bbox (99,191)-(200,292) against pdftocairo's and
/// pdftoppm's 10201 px / (100,192)-(200,292) — the same rectangle, one pixel
/// wider because the content stream paints it with <c>b</c> (fill AND stroke).
/// </summary>
public class MalformedObjectPageRecoveryTests : IDisposable
{
    private const int Dpi = 72;

    private readonly List<string> _temp = new();

    /// <summary>
    /// The property that matters and does not need any tool installed: excise
    /// produces a page, and the page carries the content the broken object has
    /// nothing to do with.
    /// </summary>
    [Fact]
    public void MalformedColorSpaceObject_DoesNotRefuseThePage()
    {
        var path = WriteTemp(MalformedHexColorSpacePdf());

        using var bmp = RenderWithExcise(path);

        bmp.Width.Should().Be(400);
        InkedPixels(bmp).Should().BeGreaterThan(9000,
            "the page's own rectangle is 100x100pt = 10,000px at 72 dpi and has " +
            "nothing to do with the unparseable /ColorSpace object");
    }

    /// <summary>
    /// The no-self-oracle half. excise deciding it recovered proves only that
    /// its bugs are self-consistent; two independent engines rendering the same
    /// rectangle is what makes the old refusal a defect.
    /// </summary>
    [Fact]
    public void MalformedColorSpaceObject_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(MalformedHexColorSpacePdf());

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull();
        cairo.Should().NotBeNull();

        var reference = InkedPixels(mutool!);
        reference.Should().BeGreaterThan(9000,
            "MuPDF skips the invalid hex character and renders the page");
        InkedPixels(cairo!).Should().BeCloseTo(reference, 200u,
            "Poppler recovers the same way — two independent engines, one answer");

        using var excise = RenderWithExcise(path);
        InkedPixels(excise).Should().BeCloseTo(reference, 200u,
            "excise must render what the corroborated majority renders, not refuse " +
            "the page over a resource neither of them could read either");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One page, one filled rectangle, and a /ColorSpace resource whose object
    /// is the malformation from bug_481363.pdf verbatim.
    ///
    /// The content stream MUST select /CS1, exactly as the corpus file does.
    /// An earlier draft of this fixture left it unreferenced on the theory that
    /// it kept the test single-purpose; the mutation check caught that this
    /// made the test vacuous — nothing ever resolved object 4, so it passed
    /// with the fix reverted. The defect only exists along the path that
    /// RESOLVES the broken object.
    /// </summary>
    private static byte[] MalformedHexColorSpacePdf()
    {
        const string content = "/CS1 cs 0 -100 -100 sc\n100 100 100 100 re f";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 400] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R "
                + "/Resources << /ColorSpace << /CS1 4 0 R >> >> >>\nendobj\n",
            "4 0 obj [\n  /Lab 4<\n    /WhitePoint [0.9505 1.00 1.0890 ]\n"
                + "    /Range [-100 100 -100 100 ]\n  >>\n]\nendobj\n",
            $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        });
    }

    private static byte[] Assemble(string[] objects)
    {
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

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private static uint InkedPixels(SKBitmap bmp)
    {
        uint ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-973-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
        GC.SuppressFinalize(this);
    }
}
