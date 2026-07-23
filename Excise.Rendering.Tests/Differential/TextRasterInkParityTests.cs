using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Regression gate for issue #710 (root cause of #584's "excise text renders
/// heavier than every reference").
///
/// The bug: all fill-mode text was rasterized via <c>SKCanvas.DrawText</c>,
/// whose glyph masks come from the *platform scaler* behind Skia's glyph
/// cache — CoreText/CoreGraphics on macOS, hinted FreeType on Linux,
/// DirectWrite on Windows. Those mask coverage curves are platform-dependent
/// and measurably different from the unhinted-FreeType coverage that every
/// PDF reference renderer (mutool / pdftocairo / Ghostscript) produces.
/// Measured on an IDENTICAL embedded Type 1C outline (so no font
/// substitution confound) at 10–20pt/150dpi on macOS, CoreText masks carried
/// ~17% more ink — about +0.45px of extra width on every stem — while at
/// 300pt the same pipeline matched mutool to 0.1%: a small-ppem,
/// platform-scaler artifact, not outline geometry and not antialiasing.
///
/// The fix fills text from the glyph *outline path* (Skia's own analytic
/// scan converter — exact area coverage, identical on every platform),
/// falling back to DrawText only when a run has no outline geometry
/// (bitmap-only faces such as color emoji).
///
/// This test embeds a real CFF font (Inconsolata, SIL OFL, already checked
/// in) in a from-scratch PDF, draws small (10pt) and medium (20pt) text —
/// the sizes where the platform-mask delta is largest — and asserts total
/// ink parity with mutool, an independent FreeType-based rasterizer of the
/// same embedded outline. Pre-fix the macOS ratio was ~1.17; the gate allows
/// ±10%.
///
/// The /Differences-based /Encoding is deliberate: it routes rendering
/// through the byte→glyph-ID SKTextBlob branch (the one real-world embedded
/// CFF subsets hit, e.g. #584's page 36), not the plain-Unicode fallback.
/// </summary>
public class TextRasterInkParityTests : IDisposable
{
    private readonly List<string> _temp = new();

    [Fact]
    public void EmbeddedCffSmallText_InkMatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var pdfBytes = BuildSmallTextPdf(LoadInconsolataCff());
        var path = Path.Combine(Path.GetTempPath(), $"excise-ink-parity-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfBytes);
        _temp.Add(path);

        using var doc = PdfDocument.Open(pdfBytes);
        using var ours = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, 150);
        Assert.SkipWhen(reference == null, "mutool could not render the fixture");

        var ourInk = InkSum(ours);
        var refInk = InkSum(reference!);

        refInk.Should().BeGreaterThan(500,
            "the reference render must actually contain the fixture's text ink; " +
            "an (almost) blank reference means the fixture itself is broken and " +
            "the ratio below would be meaningless");
        ourInk.Should().BeGreaterThan(500,
            "excise must render the embedded-font text at all before its weight " +
            "can be compared");

        var ratio = ourInk / refInk;
        ratio.Should().BeInRange(0.90, 1.10,
            "excise and mutool rasterize the IDENTICAL embedded CFF outline, so " +
            "total glyph ink must match closely. A high ratio means text fills " +
            "are going through platform glyph masks (CoreText/DirectWrite/hinted " +
            "FreeType) instead of Skia's analytic outline fill — issue #710: on " +
            "macOS that rendered every stem ~0.45px wider (+17% ink at 10–20pt) " +
            "than every FreeType-based reference renderer");
    }

    private static double InkSum(SKBitmap bmp)
    {
        double ink = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                // Luma-ish average is fine: the fixture is black-on-white.
                var v = (c.Red + c.Green + c.Blue) / 3.0;
                ink += (255.0 - v) / 255.0;
            }
        }

        return ink;
    }

    private static byte[] LoadInconsolataCff()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Inconsolata.cff", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Minimal from-scratch PDF (same raw-byte-offset pattern as
    /// <c>EmbeddedGlyphWordSpacingPositioningTests</c>): one page, Inconsolata
    /// embedded as /FontFile3 Type1C, a /Differences /Encoding covering the
    /// letters used, and several 10pt lines plus one 20pt line of text.
    /// </summary>
    private static byte[] BuildSmallTextPdf(byte[] cffBytes)
    {
        using var ms = new MemoryStream();

        void WriteAscii(string s)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }
        void WriteLine(string s) => WriteAscii(s + "\n");

        var offsets = new long[9];

        WriteLine("%PDF-1.4");

        offsets[1] = ms.Position;
        WriteLine("1 0 obj");
        WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        WriteLine("endobj");

        offsets[2] = ms.Position;
        WriteLine("2 0 obj");
        WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteLine("endobj");

        offsets[3] = ms.Position;
        WriteLine("3 0 obj");
        WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 300] " +
                   "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        WriteLine("endobj");

        const string line = "included in the open source definition means a set of stems";
        var sb = new System.Text.StringBuilder();
        sb.Append("BT\n/F1 10 Tf\n");
        sb.Append("40 250 Td\n");
        for (int i = 0; i < 5; i++)
            sb.Append(CultureInfo.InvariantCulture, $"({line}) Tj\n0 -14 Td\n");
        sb.Append("ET\nBT\n/F1 20 Tf\n40 120 Td\n(seven bits of medium sized text) Tj\nET");
        var content = sb.ToString();

        offsets[4] = ms.Position;
        WriteLine("4 0 obj");
        WriteLine($"<< /Length {content.Length} >>");
        WriteLine("stream");
        WriteAscii(content);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");

        // Inconsolata is monospaced (500/1000 em) — every code gets width 500.
        offsets[5] = ms.Position;
        WriteAscii("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Inconsolata " +
                   "/FirstChar 32 /LastChar 122 /Widths [");
        for (int c = 32; c <= 122; c++) WriteAscii(c == 32 ? "500" : " 500");
        WriteLine(" ] /Encoding 8 0 R /FontDescriptor 6 0 R >>");
        WriteLine("endobj");

        offsets[6] = ms.Position;
        WriteLine("6 0 obj");
        WriteLine("<< /Type /FontDescriptor /FontName /Inconsolata " +
                   "/Flags 32 /FontBBox [-100 -300 900 900] /ItalicAngle 0 " +
                   "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 " +
                   "/FontFile3 7 0 R >>");
        WriteLine("endobj");

        offsets[7] = ms.Position;
        WriteLine("7 0 obj");
        WriteLine($"<< /Subtype /Type1C /Length {cffBytes.Length} >>");
        WriteLine("stream");
        ms.Write(cffBytes, 0, cffBytes.Length);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");

        offsets[8] = ms.Position;
        WriteLine("8 0 obj");
        WriteLine("<< /Type /Encoding /Differences [32 /space " +
                  "97 /a /b /c /d /e /f /g /h /i /j /k /l /m /n /o /p /q /r /s /t /u /v /w /x /y /z] >>");
        WriteLine("endobj");

        var xrefPos = ms.Position;
        WriteLine("xref");
        WriteLine("0 9");
        WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 8; i++)
            WriteLine($"{offsets[i]:D10} 00000 n ");
        WriteLine("trailer");
        WriteLine("<< /Size 9 /Root 1 0 R >>");
        WriteLine("startxref");
        WriteLine(xrefPos.ToString(CultureInfo.InvariantCulture));
        WriteLine("%%EOF");

        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var f in _temp)
        {
            try { File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }
}
