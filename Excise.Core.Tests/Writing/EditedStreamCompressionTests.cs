using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// #1549 — stream data excise writes itself (a redacted or edited content
/// stream, an image-only redaction raster page) is saved Flate-encoded instead
/// of raw.
/// </summary>
/// <remarks>
/// <para>Before the fix <c>PdfStream.DecodedData</c>'s setter stored the bytes
/// uncompressed, so redaction made files LARGER than their input. Measured with
/// <c>excise redact</c> on the smoke corpus: irs-w4.pdf 208,845 → 1,001,684
/// bytes (4.8x) for one term; a one-page <c>--flatten-ocr</c> of irs-w9.pdf
/// 140,815 → 151,472,872 bytes.</para>
///
/// <para>Compressing redaction output has a cost this file also pins: a term
/// left inside a content stream is no longer visible to a raw byte scan of the
/// saved file. The planted-leak test proves <see cref="SavedPdfLeakScanner"/>
/// still sees it, so leak assertions must go through the scanner.</para>
/// </remarks>
public class EditedStreamCompressionTests
{
    private const string Term = "Farrar";

    /// <summary>
    /// A one-page document whose content stream is Flate-encoded, the way
    /// production PDFs ship: 60 lines of text, "Farrar" on every fifth line.
    /// </summary>
    private static byte[] CompressedTextPagePdf()
    {
        var content = new StringBuilder("BT /F1 10 Tf 12 TL 40 760 Td\n");
        for (int i = 0; i < 60; i++)
        {
            var name = i % 5 == 0 ? $"Louise Anne {Term}" : "the applicant";
            content.Append(
                $"(Line {i}: this declaration was signed by {name} before the clerk.) Tj T*\n");
        }
        content.Append("ET\n");
        var flate = Deflate(Encoding.Latin1.GetBytes(content.ToString()));

        using var file = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }
        Ascii("%PDF-1.7\n" +
              "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
              "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
              "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
              $"4 0 obj\n<< /Length {flate.Length} /Filter /FlateDecode >>\nstream\n");
        file.Write(flate, 0, flate.Length);
        Ascii("\nendstream\nendobj\n" +
              "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n" +
              "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return file.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static PdfStream PageContentStream(PdfDocument doc)
    {
        var contents = doc.Resolve(doc.GetPage(1).Dictionary.GetOptional("Contents")!);
        if (contents is PdfArray array)
            contents = doc.Resolve(array[0]);
        return contents.Should().BeOfType<PdfStream>().Subject;
    }

    [Fact]
    public void RedactingATerm_DoesNotGrowTheSavedFile()
    {
        var source = CompressedTextPagePdf();

        using var plain = PdfDocument.Open(source);
        var plainSaved = plain.SaveToBytes();

        using var doc = PdfDocument.Open(source);
        doc.RedactText(Term, drawBlackRect: true).VerifiedRemovals.Should().Be(12);
        var redactedSaved = doc.SaveToBytes();

        // Measured 2026-09-17: plain save 1,062 bytes, redacted 1,149 (1.08x) —
        // the rewritten stream carries TJ splits and a covering box per hit.
        // Before #1549 the redacted file was 6,145 bytes (5.79x): the whole
        // content stream went out raw. 1.15 leaves room for writer noise and
        // still fails any return to raw output by a wide margin.
        ((double)redactedSaved.Length / plainSaved.Length).Should().BeLessThan(1.15,
            $"a redaction must not inflate the file (plain save {plainSaved.Length} bytes, " +
            $"redacted {redactedSaved.Length})");
    }

    [Fact]
    public void RedactedContentStream_IsSavedFlateEncoded_AndReadsBack()
    {
        using var doc = PdfDocument.Open(CompressedTextPagePdf());
        doc.RedactText(Term, drawBlackRect: false).VerifiedRemovals.Should().Be(12);
        var saved = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        var stream = PageContentStream(reopened);
        stream.Filters.Should().Equal("FlateDecode");
        stream.Length.Should().Be(stream.EncodedData.Length, "/Length must describe the encoded bytes");

        var text = reopened.GetPage(1).Text;
        text.Should().Contain("the applicant").And.Contain("Louise Anne").And.NotContain(Term);
    }

    [Fact]
    public void RedactedFile_PassesQpdfCheck()
    {
        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf not on PATH");

        using var doc = PdfDocument.Open(CompressedTextPagePdf());
        doc.RedactText(Term).VerifiedRemovals.Should().Be(12);
        var path = Path.Combine(Path.GetTempPath(), $"excise-1549-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, doc.SaveToBytes());
        try
        {
            var (exitCode, output) = RunProcess(qpdf!, "--check", path);
            exitCode.Should().Be(0, $"qpdf (an independent reader) must accept the output:\n{output}");
            output.Should().NotContain("WARNING");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The instrument check. A term written into a content stream by excise is
    /// now compressed on save: a raw scan cannot see it, and the leak scanner
    /// (which inflates) must. If the scanner stopped inflating, the second
    /// assertion fails; if excise stopped compressing, the first one does and
    /// the second proves nothing.
    /// </summary>
    [Fact]
    public void PlantedTermInARewrittenContentStream_IsFoundByTheLeakScanner_NotByARawScan()
    {
        using var doc = PdfDocument.Open(CompressedTextPagePdf());
        var page = doc.GetPage(1);
        var planted = Encoding.Latin1.GetString(page.GetContentStreamBytes())
            + $"BT /F1 10 Tf 40 40 Td (PLANTED {Term} LEAK) Tj ET\n";
        page.SetContentStreamBytes(Encoding.Latin1.GetBytes(planted));
        var saved = doc.SaveToBytes();

        (Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved))
            .Should().NotContain(Term,
                "the rewritten stream is Flate-encoded, so a raw scan is blind to it — " +
                "if this finds the term, the stream went out raw and #1549 regressed");

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().Contain(h => h.Contains("inflated stream"),
            "the term IS in the file; the leak scanner must inflate the stream and report it");
    }

    [Fact]
    public void DecodedDataSetter_FlateEncodes_AndKeepsFilterAndLengthConsistent()
    {
        var stream = new PdfStream();
        stream.SetName("Filter", "ASCIIHexDecode");
        stream["DecodeParms"] = new PdfDictionary();
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 10 10 l S\n", 50)));

        stream.DecodedData = data;

        stream.Filters.Should().Equal("FlateDecode");
        stream.ContainsKey("DecodeParms").Should().BeFalse();
        stream.Length.Should().Be(stream.EncodedData.Length);
        stream.EncodedData.Length.Should().BeLessThan(data.Length);
        Inflate(stream.EncodedData).Should().Equal(data, "Flate is lossless");
        stream.DecodedData.Should().BeSameAs(data);
    }

    [Fact]
    public void DecodedDataSetter_StoresRaw_WhenFlateWouldNotShrinkTheBytes()
    {
        var stream = new PdfStream();
        stream.SetName("Filter", "FlateDecode");
        var data = new byte[] { 1, 2, 3 };

        stream.DecodedData = data;

        stream.IsFiltered.Should().BeFalse();
        stream.EncodedData.Should().Equal(data);
        stream.Length.Should().Be(3);
    }

    [Fact]
    public void DecodedDataSetter_KeepsAnXmpMetadataStreamRaw()
    {
        var stream = new PdfStream();
        stream.SetName("Type", "Metadata");
        stream.SetName("Subtype", "XML");
        var xmp = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + new string(' ', 2000) + "</x:xmpmeta>");

        stream.DecodedData = xmp;

        stream.IsFiltered.Should().BeFalse("XMP must stay readable without decompression (§14.3.2)");
        stream.EncodedData.Should().Equal(xmp);
    }

    [Fact]
    public void RasterRedactionPage_IsSavedFlateEncoded_Losslessly()
    {
        const int w = 200, h = 150;
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = 255;
            rgb[i + 1] = (byte)(i / 3 % w);
            rgb[i + 2] = 0;
        }

        using var doc = PdfDocument.Open(CompressedTextPagePdf());
        doc.AddRgbRasterPage(rgb, w, h, 200, 150);
        var saved = doc.SaveToBytes();

        saved.Length.Should().BeLessThan(rgb.Length / 4,
            $"a {rgb.Length}-byte raster must not be written raw");

        using var reopened = PdfDocument.Open(saved);
        var image = reopened.GetPage(reopened.PageCount).GetXObject("Im0")
            .Should().BeOfType<PdfStream>().Subject;
        image.Filters.Should().Equal(new[] { "FlateDecode" }, "lossless only — never DCT for redaction output");
        Inflate(image.EncodedData).Should().Equal(rgb);
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static string? FindOnPath(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin" }))
        {
            var candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static (int ExitCode, string Output) RunProcess(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000).Should().BeTrue($"{executable} should exit within 30 seconds");
        return (proc.ExitCode, stdout + stderrTask.Result);
    }
}
