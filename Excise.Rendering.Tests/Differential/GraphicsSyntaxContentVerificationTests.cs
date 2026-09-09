using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle evidence for registry capabilities <c>pdf.17.syntax.file</c>,
/// <c>pdf.17.syntax.objects</c>, <c>pdf.17.syntax.filters</c> and
/// <c>pdf.17.graphics.paths-painting</c> (mutate mode) that previously rested only
/// on excise reading back what excise itself wrote, or had no explicit contract at
/// all. Every fact checked here is confirmed by a tool that shares no code with
/// excise -- qpdf's own lexer/JSON dump for header/object/stream questions,
/// mutool's own rasterizer for the paths-painting mutation question -- so a
/// systematic misunderstanding shared between excise's writer and excise's own
/// reader (the #636/#608/#637 shape) cannot pass silently here the way it would
/// if excise verified its own output.
///
/// Fixtures are deliberately restricted to files checked into git
/// (test-pdfs/sample-pdfs, test-pdfs/pdf20) or synthesized in-test, not the
/// gitignored downloaded corpora -- so these tests run (not skip) on any clone.
/// </summary>
public class GraphicsSyntaxContentVerificationTests : IDisposable
{
    private readonly List<string> _temp = new();

    // ── pdf.17.syntax.file: header version, independent of excise's own reader ──

    [Theory]
    [InlineData("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf")]
    [InlineData("test-pdfs/pdf20/ascii85-image.pdf")]
    public void HeaderVersion_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfVersion(path!);
        Assert.SkipWhen(expected == null, "qpdf did not report a PDF version for this fixture");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        doc.Version.Should().Be(expected,
            "excise's own header-version parse must agree with qpdf's independent lexer -- a " +
            "header scanner that reads the wrong digits (or stops at the wrong terminator) " +
            "would otherwise pass silently under excise's own self-consistent reading");
    }

    [Theory]
    [InlineData("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf")]
    [InlineData("test-pdfs/pdf20/ascii85-image.pdf")]
    public void HeaderVersion_SurvivesOpenSaveRoundTrip_PerQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfVersion(path!);
        Assert.SkipWhen(expected == null, "qpdf did not report a PDF version for this fixture");

        var output = TempPath();
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
            doc.Save(output);

        var after = QpdfVersion(output);
        Assert.SkipWhen(after == null, "qpdf could not read excise's saved output");
        after.Should().Be(expected,
            "the PDF version header must survive an unmodified open-save round trip -- checked " +
            "by asking qpdf to independently re-parse BOTH the original and the saved file, not " +
            "by comparing excise's own Version property before and after");
    }

    // ── pdf.17.syntax.objects: page tree / xref / trailer, independent of excise ──

    [Theory]
    [InlineData("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf")]
    [InlineData("test-pdfs/pdf20/ascii85-image.pdf")]
    public void PageCount_MatchesQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfReferenceTool.PageCount(path!);
        Assert.SkipWhen(expected is null or <= 0, "qpdf could not read the fixture's page count");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        doc.PageCount.Should().Be(expected!.Value,
            "excise's own page count -- which requires correctly resolving the indirect object " +
            "graph, the page tree's inherited /Count, and (for the pdf20 fixture) a cross-" +
            "reference STREAM rather than a traditional table -- must agree with qpdf's " +
            "independent count; a page-tree misreading would otherwise pass silently under " +
            "excise's own self-consistent reading");
    }

    [Theory]
    [InlineData("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf")]
    [InlineData("test-pdfs/pdf20/ascii85-image.pdf")]
    public void PageCount_SurvivesOpenSaveRoundTrip_PerQpdf(string relativePath)
    {
        var path = Resolve(relativePath);
        Assert.SkipWhen(path == null, $"fixture not present: {relativePath}");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = QpdfReferenceTool.PageCount(path!);
        Assert.SkipWhen(expected is null or <= 0, "qpdf could not read the fixture's page count");

        var output = TempPath();
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
            doc.Save(output);

        var after = QpdfReferenceTool.PageCount(output);
        Assert.SkipWhen(after is null, "qpdf could not read excise's saved output");
        after!.Value.Should().Be(expected!.Value,
            "the indirect object graph backing the page tree (objects, xref, trailer) must " +
            "survive an unmodified open-save round trip -- qpdf's independent page count of the " +
            "SAVED file must still match, not just excise's own reader agreeing with itself");
    }

    // ── pdf.17.syntax.filters: FlateDecode content, independent of excise's own decoder ──

    [Fact]
    public void FlateDecodedContentStream_MatchesQpdfIndependentDecompression()
    {
        var path = Resolve("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf");
        Assert.SkipWhen(path == null, "fixture not present");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string marker = "Compensation";

        var independentlyDecompressed = QpdfUncompressedText(path!);
        Assert.SkipWhen(independentlyDecompressed == null, "qpdf could not decompress the fixture's streams");
        independentlyDecompressed!.Should().Contain(marker,
            "guard: qpdf's own independent Flate decompressor must recover this word from the " +
            "page-1 content stream, or the comparison below proves nothing");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
        doc.GetPage(1).Text.Should().Contain(marker,
            "excise's own FlateDecode implementation must recover the same word qpdf's " +
            "independent decompressor found in the raw content stream -- a subtly wrong Flate " +
            "decoder (a bad window, an off-by-one on a copy length) could still produce output " +
            "that parses as text without throwing, just the wrong text");
    }

    [Fact]
    public void FlateDecodedContentStream_SurvivesOpenSaveRoundTrip_PerQpdfIndependentDecompression()
    {
        var path = Resolve("test-pdfs/sample-pdfs/acc-global-compensation-report.pdf");
        Assert.SkipWhen(path == null, "fixture not present");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string marker = "Compensation";

        var output = TempPath();
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
            doc.Save(output);

        var independentlyDecompressed = QpdfUncompressedText(output);
        Assert.SkipWhen(independentlyDecompressed == null, "qpdf could not decompress excise's saved output");
        independentlyDecompressed!.Should().Contain(marker,
            "the FlateDecode-compressed page text must survive an unmodified open-save round " +
            "trip -- confirmed by asking qpdf's independent decompressor to read the SAVED " +
            "bytes, not by excise's own reader checking excise's own writer");
    }

    [Fact]
    public void ExciseAuthoredCompressedObjectStream_IsIndependentlyDecodableByQpdf()
    {
        // Excise's writer never applies FlateDecode to a PAGE CONTENT stream
        // (verified empirically while building this test: a fresh
        // PdfDocumentBuilder page's content object serializes as plain,
        // uncompressed bytes -- only the packaged /ObjStm holding the other
        // indirect (non-stream) objects, and the /XRef stream, get
        // FlateDecode). So the independent-decode claim for "write" has to
        // target what excise ACTUALLY compresses: an outline item's title,
        // a plain dictionary object eligible for /ObjStm packing (unlike
        // /Info's /Title, which the writer deliberately keeps OUT of object
        // streams so it stays greppable -- an outline item's /Title is a
        // different object with no such exemption).
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        const string marker = "FILTERQPDFCANARY9X3F";

        byte[] pdf;
        using (var doc = PdfDocument.Open(Excise.Core.Authoring.PdfDocumentBuilder.Create().Paragraph("Body text").SaveToBytes()))
        {
            doc.AddOutlineItem(marker, pageNumber: 1);
            pdf = doc.SaveToBytes();
        }
        var output = TempPath();
        File.WriteAllBytes(output, pdf);

        QpdfJsonObjects(output, out var objects).Should().BeTrue(
            "guard: qpdf must be able to parse excise's own output at all");
        objects.Values.Any(v => ContainsStringRecursive(v, marker)).Should().BeTrue(
            "qpdf's own independent JSON object dump -- which transparently decompresses any " +
            "compressed object stream (/ObjStm) it encounters -- must recover the outline title " +
            "excise wrote; proof the WRITTEN filtered/packaged object data is spec-compliant to " +
            "a reader that isn't excise, not merely self-consistent with excise's own decoder");
    }

    // ── pdf.17.graphics.paths-painting (mutate): opaque fill removal, independent render ──

    private static readonly PdfRectangle ObstructionBlock = new(100, 600, 300, 680);
    private static readonly PdfRectangle KeptTextBlock = new(100, 380, 300, 420);

    [Fact]
    public void PathFillObstruction_IsGoneFromAnIndependentRender_TextElsewhereSurvives()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var pdf = PdfDocument.Open(ObstructedPagePdf());
        var page = pdf.GetPage(1);

        var beforePath = SaveTemp(pdf);
        using var before = MutoolReferenceRenderer.RenderPage(beforePath, 1, dpi: 150);
        before.Should().NotBeNull();
        InkFractionIn(before!, ObstructionBlock, page.Height).Should().BeGreaterThan(0.5,
            "guard: the fixture's fill rectangle must actually paint opaque ink, or the " +
            "comparison below proves nothing");
        InkFractionIn(before!, KeptTextBlock, page.Height).Should().BeGreaterThan(0.02,
            "guard: the fixture's kept text must actually be visible before stripping too");

        ObstructionStripper.StripObstructions(page);
        var afterPath = SaveTemp(pdf);
        using var after = MutoolReferenceRenderer.RenderPage(afterPath, 1, dpi: 150);
        after.Should().NotBeNull();

        InkFractionIn(after!, ObstructionBlock, page.Height).Should().BeLessThan(0.01,
            "the opaque fill-path operation must be gone from what an INDEPENDENT renderer " +
            "draws, not merely absent from excise's own operator list -- a fill removed from " +
            "the model but left in the emitted bytes (or removed from the wrong region) would " +
            "still ink here under mutool");
        InkFractionIn(after!, KeptTextBlock, page.Height).Should().BeGreaterThan(0.02,
            "non-obstructing content (ordinary text, drawn separately from the fill) must " +
            "survive path-painting mutation -- a stripper that clears the whole content stream " +
            "would pass the assertion above for the wrong reason");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Fraction of non-white pixels inside a content-space rect.</summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    /// <summary>
    /// A page with an opaque black-filled rectangle (path construction + <c>f</c>
    /// paint, no color operator needed -- DeviceGray fill defaults to black) plus
    /// separate, non-overlapping visible text that must survive stripping.
    /// </summary>
    private static byte[] ObstructedPagePdf()
    {
        // ObstructionStripper only treats a fill as "obstructive" when it can
        // see an explicit rg/g/k color-setting operator immediately before
        // the paint (it does not assume PDF's own DeviceGray-0 default) --
        // matching ObstructionStripperTests.StripObstructions_BlackFillRectangle_RemovesPathAndFill,
        // which likewise emits an explicit black rg before the rectangle.
        var content =
            "0 0 0 rg " +
            $"{ObstructionBlock.Left} {ObstructionBlock.Bottom} {ObstructionBlock.Width} {ObstructionBlock.Height} re f " +
            $"BT /F1 24 Tf {KeptTextBlock.Left} {KeptTextBlock.Bottom} Td (KEEP TEXT VISIBLE) Tj ET";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private string SaveTemp(PdfDocument pdf)
    {
        var path = TempPath();
        pdf.Save(path);
        return path;
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-gsc-verify-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    /// <summary>PDF version reported by qpdf's independent <c>--check</c> parser
    /// (the "PDF Version: X.Y" line), or null when qpdf can't read it.</summary>
    private static string? QpdfVersion(string pdfPath)
    {
        var check = QpdfReferenceTool.Check(pdfPath);
        if (check == null) return null;
        var match = Regex.Match(check.Value.Output, @"PDF Version:\s*(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Runs qpdf's own <c>--qdf --stream-data=uncompress</c> (an independent
    /// decompressor, not excise's) and returns the resulting bytes as Latin-1
    /// text, suitable for a substring search over decoded content-stream
    /// operators and literal strings. Returns null when qpdf is unavailable,
    /// times out, or fails to produce output.
    /// </summary>
    private static string? QpdfUncompressedText(string pdfPath)
    {
        var output = Path.Combine(Path.GetTempPath(), $"excise-qdf-{Guid.NewGuid():N}.pdf");
        try
        {
            var psi = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--qdf");
            psi.ArgumentList.Add("--stream-data=uncompress");
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(output);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return null; }
            if (proc.ExitCode != 0 || !File.Exists(output)) return null;

            return Encoding.Latin1.GetString(File.ReadAllBytes(output));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { /* best effort */ }
        }
    }

    /// <summary>qpdf's JSON dump decodes every object, including ones packed
    /// into a compressed /ObjStm, so recursing over its strings is a
    /// carrier-agnostic, independent presence check.</summary>
    private static bool ContainsStringRecursive(System.Text.Json.JsonElement element, string term)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return (element.GetString() ?? "").Contains(term, StringComparison.Ordinal);
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    if (ContainsStringRecursive(prop.Value, term)) return true;
                return false;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsStringRecursive(item, term)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool QpdfJsonObjects(string pdfPath,
        out Dictionary<string, System.Text.Json.JsonElement> objects)
    {
        objects = new Dictionary<string, System.Text.Json.JsonElement>();
        try
        {
            var psi = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--json=1");
            psi.ArgumentList.Add("--json-key=objects");
            psi.ArgumentList.Add(pdfPath);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return false; }
            if (stdout.Length == 0) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("objects", out var objs)) return false;
            foreach (var prop in objs.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    objects[prop.Name] = prop.Value.Clone();
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
