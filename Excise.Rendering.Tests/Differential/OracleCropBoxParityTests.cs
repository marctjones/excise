using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1380 — the reference renderers must rasterise the SAME REGION excise does.
///
/// <para>pdftocairo and Ghostscript default to the <b>MediaBox</b>. excise, mutool,
/// PDFium and PDFBox display the <b>CropBox</b>, which is what §7.7.3.3 requires: the
/// CropBox is "the region to which the contents of the page shall be clipped (cropped)
/// when displayed or printed". Until this was fixed, neither renderer was told to use it,
/// so on every page whose CropBox differs from its MediaBox those two oracles produced a
/// different-sized bitmap of a different part of the page — and the corpus scan scored
/// that as excise disagreeing with the references.</para>
///
/// <para><b>Why this went unnoticed for so long.</b> At least 41 corpus files have
/// <c>CropBox != MediaBox</c> reading only the uncompressed page dictionaries, but just 7
/// surfaced among the 41 pages that differed from the references. On the rest the majority
/// rule (#932) outvoted the two mis-invoked oracles, so a harness defect was silently
/// absorbed by the voting rule. CLAUDE.md documented the symptom for the
/// <c>MISSING_CONTENT</c> vote — "pdftocairo renders the /MediaBox where excise, mutool
/// and pdfium render the /CropBox — get no vote" — and that workaround was applied there
/// and nowhere else. The <c>diffFraction</c> path never got it and the invocation itself
/// was never fixed.</para>
///
/// <para>Measured at the scan's own 150 dpi, excise-vs-pdftocairo diffFraction before and
/// after the flag: bug1802506 0.1328 → 0.0041, issue2884_reduced 0.1988 → 0.0246,
/// bug1922766 0.1625 → 0.0367, copy_paste_ligatures 0.2482 → 0.0474, issue4402_reduced
/// 0.3106 → 0.0799, issue16316 0.4784 → 0.1420, issue2177 0.5912 → 0.1653.</para>
///
/// <para><b>The expectation is derived from the fixture, never from excise.</b> The
/// assertion is that each oracle's bitmap matches the CropBox declared in a
/// purpose-built PDF this test writes itself. Comparing the oracles to excise's own
/// output would let a shared error pass — the failure mode the no-self-oracle rule
/// exists to prevent.</para>
/// </summary>
public class OracleCropBoxParityTests
{
    private const int Dpi = 150;

    // Deliberately offset from the MediaBox origin and a different aspect ratio, so a
    // renderer using the wrong box cannot coincidentally produce the right dimensions.
    private const double MediaW = 612, MediaH = 792;
    private const double CropX0 = 92, CropY0 = 608, CropX1 = 321.33, CropY1 = 706.67;

    [Fact]
    public void Pdftocairo_RasterisesTheCropBox_NotTheMediaBox()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable,
            "pdftocairo not on PATH."); // [requires: tool:pdftocairo]

        using var path = new TempPdf(BuildCropBoxFixture());
        using var bitmap = PdftocairoReferenceRenderer.RenderPage(path.Path, 1, Dpi);

        AssertMatchesCropBox(bitmap, "pdftocairo");
    }

    [Fact]
    public void Ghostscript_RasterisesTheCropBox_NotTheMediaBox()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable,
            "ghostscript not on PATH."); // [requires: tool:gs]

        using var path = new TempPdf(BuildCropBoxFixture());
        using var bitmap = GhostscriptReferenceRenderer.RenderPage(path.Path, 1, Dpi);

        AssertMatchesCropBox(bitmap, "ghostscript");
    }

    /// <summary>
    /// mutool already honours the CropBox with no flag, so it is the control: if this
    /// ever fails, the fixture is wrong rather than the invocation.
    /// </summary>
    [Fact]
    public void Mutool_RasterisesTheCropBox_AndIsTheControlForThisFixture()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH."); // [requires: tool:mutool]

        using var path = new TempPdf(BuildCropBoxFixture());
        using var bitmap = MutoolReferenceRenderer.RenderPage(path.Path, 1, Dpi);

        AssertMatchesCropBox(bitmap, "mutool");
    }

    private static void AssertMatchesCropBox(SKBitmap? bitmap, string oracle)
    {
        bitmap.Should().NotBeNull($"{oracle} should render the single-page fixture");

        var expectedW = (int)Math.Round((CropX1 - CropX0) * Dpi / 72.0);
        var expectedH = (int)Math.Round((CropY1 - CropY0) * Dpi / 72.0);
        var mediaW = (int)Math.Round(MediaW * Dpi / 72.0);

        // One pixel of slack: renderers differ on rounding a fractional box edge. The
        // MediaBox is 2.7x wider, so this tolerance cannot hide the defect.
        bitmap!.Width.Should().BeCloseTo(expectedW, 1,
            $"{oracle} must rasterise the CropBox ({expectedW}px wide), not the MediaBox ({mediaW}px) — §7.7.3.3");
        bitmap.Height.Should().BeCloseTo(expectedH, 1,
            $"{oracle} must rasterise the CropBox ({expectedH}px tall) — §7.7.3.3");
    }

    /// <summary>
    /// A one-page PDF whose CropBox is a small offset region of a Letter MediaBox, with a
    /// filled rectangle covering the whole CropBox so a renderer that used the MediaBox
    /// would differ in content as well as in size.
    /// </summary>
    private static byte[] BuildCropBoxFixture()
    {
        var content = System.Text.Encoding.ASCII.GetBytes(
            $"1 0 0 rg {CropX0:0.##} {CropY0:0.##} {CropX1 - CropX0:0.##} {CropY1 - CropY0:0.##} re f\n");

        var objects = new List<byte[]>
        {
            System.Text.Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            System.Text.Encoding.ASCII.GetBytes("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            System.Text.Encoding.ASCII.GetBytes(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {MediaW:0.##} {MediaH:0.##}] " +
                $"/CropBox [{CropX0:0.##} {CropY0:0.##} {CropX1:0.##} {CropY1:0.##}] " +
                "/Resources << >> /Contents 4 0 R >>"),
            Concat(
                System.Text.Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n"),
                content,
                System.Text.Encoding.ASCII.GetBytes("endstream")),
        };

        var buffer = new MemoryStream();
        void Write(string s) => buffer.Write(System.Text.Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(buffer.Position);
            Write($"{i + 1} 0 obj ");
            buffer.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xref = buffer.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return buffer.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, at, part.Length);
            at += part.Length;
        }
        return result;
    }

    private sealed class TempPdf : IDisposable
    {
        public string Path { get; }

        public TempPdf(byte[] bytes)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"excise-cropbox-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(Path, bytes);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort */ }
        }
    }
}
