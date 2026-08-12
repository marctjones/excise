using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Text;
using Excise.Core.Document;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #774/#824 — a full-width line (header/footer/page number) that spans the
/// column gutter used to defeat gutter detection (the cumulative X-sweep filled
/// the gutter), so the page fell back to geometric order and the two columns
/// were woven row-major. These construction-known fixtures are the proof: the
/// exact reading order must be header → column-1 (top→bottom) → column-2 →
/// footer, and a single-column page must be byte-identical to the geometric
/// order (the conservatism guarantee).
/// </summary>
public class TwoColumnHeaderReadingOrderTests
{
    [Fact]
    public void TwoColumns_WithFullWidthHeaderAndFooter_ReadColumnByColumn()
    {
        // Header/footer span the whole width (across the gutter); Alpha/Bravo/
        // Charlie form the left column, Uno/Dos/Tres the right column, each pair
        // baseline-aligned at the same Y.
        const string content =
            "BT /F1 12 Tf " +
            "1 0 0 1 72 720 Tm (Header spanning the full page width across here) Tj " +
            "1 0 0 1 72 690 Tm (Alpha) Tj 1 0 0 1 360 690 Tm (Uno) Tj " +
            "1 0 0 1 72 672 Tm (Bravo) Tj 1 0 0 1 360 672 Tm (Dos) Tj " +
            "1 0 0 1 72 654 Tm (Charlie) Tj 1 0 0 1 360 654 Tm (Tres) Tj " +
            "1 0 0 1 72 620 Tm (Footer spanning the full page width across there) Tj ET";

        var text = ColumnAwareText(BuildPdf(content));

        int iHeader = text.IndexOf("Header", StringComparison.Ordinal);
        int iAlpha = text.IndexOf("Alpha", StringComparison.Ordinal);
        int iBravo = text.IndexOf("Bravo", StringComparison.Ordinal);
        int iCharlie = text.IndexOf("Charlie", StringComparison.Ordinal);
        int iUno = text.IndexOf("Uno", StringComparison.Ordinal);
        int iDos = text.IndexOf("Dos", StringComparison.Ordinal);
        int iTres = text.IndexOf("Tres", StringComparison.Ordinal);
        int iFooter = text.IndexOf("Footer", StringComparison.Ordinal);

        new[] { iHeader, iAlpha, iBravo, iCharlie, iUno, iDos, iTres, iFooter }
            .Should().OnlyContain(i => i >= 0, "all fixture words must be present. Got:\n" + text);

        // header → left column (top→bottom) → right column → footer.
        iHeader.Should().BeLessThan(iAlpha);
        iAlpha.Should().BeLessThan(iBravo);
        iBravo.Should().BeLessThan(iCharlie);
        iCharlie.Should().BeLessThan(iUno, "the whole left column reads before the right column (not row-major)");
        iUno.Should().BeLessThan(iDos);
        iDos.Should().BeLessThan(iTres);
        iTres.Should().BeLessThan(iFooter);
    }

    [Fact]
    public void SingleColumn_ColumnAware_IsByteIdenticalToGeometric()
    {
        const string content =
            "BT /F1 12 Tf " +
            "1 0 0 1 72 720 Tm (The quick brown fox) Tj " +
            "1 0 0 1 72 700 Tm (jumps over the lazy) Tj " +
            "1 0 0 1 72 680 Tm (dog and then rests) Tj ET";
        var pdf = BuildPdf(content);

        using var doc = PdfDocument.Open(pdf);
        var letters = doc.GetPage(1).Letters.ToList();
        var colAware = TextSelectionEngine.JoinText(
            TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware), WhitespaceMode.LineFaithful);
        var simple = TextSelectionEngine.JoinText(
            TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.Simple), WhitespaceMode.LineFaithful);

        colAware.Should().Be(simple, "single-column pages must be untouched by column-aware ordering");
    }

    private static string ColumnAwareText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var letters = doc.GetPage(1).Letters.ToList();
        return TextSelectionEngine.JoinText(
            TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware),
            WhitespaceMode.LineFaithful);
    }

    private static byte[] BuildPdf(string content)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        return AssembleObjects(objects);
    }

    private static byte[] AssembleObjects(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append($"{offsets[i]:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
