using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1753 — an underline, box, strike-through or highlight drawn to one
/// redacted word's extent goes with the word. Left at the old width beside a
/// closed gap it states the removed word's width: the issue recovered 8 of 8
/// words that way. A decoration spanning more than the word (a sentence
/// highlight, a rule across the line) says nothing about the word's width and
/// is kept.
/// </summary>
/// <remarks>
/// The secret ends its line, so closing the gap moves nothing into the
/// word's old extent, and no box is drawn: whatever ink mutool renders there
/// afterwards is the decoration.
/// </remarks>
public class RedactionWordDecorationTests : IDisposable
{
    private const string Secret = "ALBERTINA";
    private const int Dpi = 150;
    private const double PageHeight = 120;

    // 20 pt Helvetica from x=20 at baseline 50. "Name: " advances 3.223 em
    // (64.46 pt) and ALBERTINA 5.557 em (111.14 pt), so the word spans
    // 84.46..195.60.
    private const string Line = "BT /F1 20 Tf 20 50 Td (Name: ALBERTINA) Tj ET";
    private static readonly PdfRectangle WordExtent = new(84.46, 43, 195.6, 68);

    private readonly List<string> _temp = new();

    public static TheoryData<string, string> WordSizedDecorations() => new()
    {
        { "underline", "84.46 46.5 111.14 1.2 re f" },
        { "box", "2 w 82.46 44 115.14 21 re S" },
        { "highlight", "1 1 0 rg 84.46 45 111.14 20 re f 0 g" },
        { "strike-through", "1.5 w 84.46 56 m 195.6 56 l S" },
    };

    [Theory]
    [MemberData(nameof(WordSizedDecorations))]
    public void WidthClosing_RemovesTheDecorationSizedToTheWord(string kind, string decoration)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Fixture($"q {decoration} Q\n{Line}");
        using (var before = MutoolReferenceRenderer.RenderPage(WriteTemp(source), 1, Dpi))
            InkFractionIn(before!, WordExtent).Should().BeGreaterThan(0.01,
                $"fixture sanity: the {kind} is drawn before redaction");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.FixedMarker }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret).And.Contain("Name");
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, WordExtent).Should().BeLessThan(0.001,
            $"the {kind} was sized to the removed word, so it states its width (#1753)");
        report.Removals.Should().Contain(r => r.Feature.Contains("sized to a redacted word") && r.Count == 1,
            "every removal is reported");
    }

    [Fact]
    public void WidthClosing_KeepsADecorationSpanningMoreThanTheWord()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // A highlight over the whole "Name: ALBERTINA" sentence and a rule
        // across the line: neither is sized to the word, so neither measures it.
        var source = Fixture($"q 0 1 1 rg 18 45 180 20 re f Q q 10 40 380 0.8 re f Q\n{Line}");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.FixedMarker }, out var report);
        var path = WriteTemp(saved);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret);
        using var after = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(after!, new PdfRectangle(150, 47, 190, 63)).Should().BeGreaterThan(0.9,
            "the sentence highlight is kept, under where the word was");
        InkFractionIn(after!, new PdfRectangle(250, 40, 380, 40.8)).Should().BeGreaterThan(0.5,
            "the rule across the line is kept");
        report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
    }

    [Fact]
    public void PreserveLayout_KeepsTheDecoration_ItsBoxAlreadyStatesTheWidth()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Only the default changed (#1715): an explicit CollapsePreserveLayout
        // caller gets the output it always got.
        var source = Fixture($"q 84.46 46.5 111.14 1.2 re f Q\n{Line}");

        var saved = Redact(source,
            RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.CollapsePreserveLayout },
            out var report);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        using var after = MutoolReferenceRenderer.RenderPage(WriteTemp(saved), 1, Dpi);
        InkFractionIn(after!, new PdfRectangle(84.46, 46.5, 195.6, 47.7)).Should().BeGreaterThan(0.5);
        report.Removals.Should().NotContain(r => r.Feature.Contains("sized to a redacted word"));
    }

    public static TheoryData<string, string> WordSizedAnnotations() => new()
    {
        // The Rect of an Underline or StrikeOut can be the stroke alone, below
        // or through the glyphs rather than around them.
        { "Highlight", "84.46 43 195.6 68" },
        { "Underline", "84.46 45.5 195.6 47.5" },
        { "StrikeOut", "84.46 55 195.6 57" },
    };

    [Theory]
    [MemberData(nameof(WordSizedAnnotations))]
    public void WidthClosing_RemovesTheMarkupAnnotationSizedToTheWord(string subtype, string rect)
    {
        var quads = "84.46 68 195.6 68 84.46 43 195.6 43";
        var source = Fixture(Line,
            $"/Annots [<< /Type /Annot /Subtype /{subtype} /Rect [{rect}] /QuadPoints [{quads}] /C [1 0 0] >>]");
        SavedPdfLeakScanner.FindTerm(source, $"/{subtype}").Should().NotBeEmpty("fixture sanity");

        var saved = Redact(source, RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.FixedMarker }, out _);

        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, $"/{subtype}").Should().BeEmpty(
            $"a {subtype} annotation at the word's width states it (#1753)");
    }

    private static byte[] Redact(byte[] source, RedactionOptions options, out RedactionReport report)
    {
        using var doc = PdfDocument.Open(source);
        report = doc.RedactText(Secret, options);
        return doc.SaveToBytes();
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box)
    {
        const double scale = Dpi / 72.0;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Ceiling(box.Right * scale));
        int y0 = Math.Max(0, (int)Math.Floor((PageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling((PageHeight - box.Bottom) * scale));
        int ink = 0, total = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                total++;
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static byte[] Fixture(string content, string pageExtra = "") => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 {PageHeight}] /Contents 4 0 R " +
        $"/Resources << /Font << /F1 5 0 R >> >> {pageExtra} >>",
        Stream(content),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static string Stream(string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-decoration-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
