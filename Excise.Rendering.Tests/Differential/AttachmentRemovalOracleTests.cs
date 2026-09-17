using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1572, judged by tools that are not excise. <c>ScrubEmbeddedFiles</c> used
/// to remove only the catalog name tree and <c>/AF</c>, so a file attached
/// through a page's <c>/FileAttachment</c> annotation survived a redacted copy
/// that reported "attachments scrubbed". Each redaction entry point is run on
/// the same input and the output is read by poppler (<c>pdfdetach</c>, which
/// lists annotation attachments), qpdf (its own object graph) and the
/// decompressing saved-byte scanner.
/// </summary>
public sealed class AttachmentRemovalOracleTests : IDisposable
{
    private const string PagePayload = "PAGEATTACH-9Z4";
    private const string AnnotPayload = "ANNOTATTACH-9Z4";
    private const string AnnotDesc = "ANNOTDESC-9Z4";
    private const string DocPayload = "DOCATTACH-9Z4";
    private static readonly string[] Secrets = { PagePayload, AnnotPayload, AnnotDesc, DocPayload };

    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static byte[] Fixture()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Visible body text) Tj ET";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(doc.txt) 7 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] /AF [11 0 R] >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [72 600 92 620] /Contents (a note) /FS 9 0 R >>",
            "<< /Type /Filespec /F (doc.txt) /UF (doc.txt) /EF << /F 8 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"document level {DocPayload}"),
            $"<< /Type /Filespec /F (annot.txt) /UF (annot.txt) /Desc ({AnnotDesc}) /EF << /F 10 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"annotation {AnnotPayload}"),
            "<< /Type /Filespec /F (page.txt) /UF (page.txt) /AFRelationship /Supplement /EF << /F 12 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"page level {PagePayload}"),
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-attach-oracle-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    private static string? Pdfdetach(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("pdfdetach")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-list");
            psi.ArgumentList.Add(path);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public static TheoryData<string> Flows() => new() { "safe-copy (GUI)", "RedactText (CLI, batch, scripting)", "RedactArea", "ScrubMetadata (RemoveAllMetadata)" };

    private static void Run(string flow, PdfDocument document)
    {
        var box = new PdfRectangle(60, 690, 300, 720);
        switch (flow)
        {
            case "safe-copy (GUI)":
                // What RedactionWorkflowService does: area passes, then the
                // shared safety policy with its defaults.
                document.GetPage(1).RedactArea(box);
                RedactedCopySafetyPolicy.Evaluate(document, RedactedCopySafetyRequest.ForAreas(
                    new[] { new RedactedCopySafetyArea(1, PdfPageRect.FromContentPoints(1, box), "Visible") }))
                    .AttachmentResults.Should().HaveCount(3, "the report names every removed file");
                break;
            case "RedactText (CLI, batch, scripting)":
                document.RedactText("Visible").Attachments.Should().HaveCount(3);
                break;
            case "RedactArea":
                document.GetPage(1).RedactArea(box);
                break;
            default:
                document.ScrubMetadata(scrubAttachments: true);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Flows))]
    public void EveryRedactionFlow_RemovesPageAndDocumentAttachments_AsPopplerAndQpdfSeeIt(string flow)
    {
        var inputPath = Write(Fixture());
        var before = Pdfdetach(inputPath);
        Assert.SkipWhen(before == null, "pdfdetach (poppler) is not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is not installed");

        // Guards: every oracle sees the leak before the redaction.
        before.Should().Contain("doc.txt").And.Contain("annot.txt");
        QpdfReferenceTool.ListAnnotations(inputPath)!.Should().Contain(a => a.Subtype == "FileAttachment");
        foreach (var secret in Secrets)
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(inputPath), secret).Should().NotBeEmpty($"guard: {secret}");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        Run(flow, document);
        var outputPath = Write(document.SaveToBytes());

        Pdfdetach(outputPath).Should().Contain("0 embedded files",
            $"{flow}: poppler must find neither the document-level nor the annotation attachment");
        QpdfReferenceTool.ListAnnotations(outputPath)!.Should().NotContain(a => a.Subtype == "FileAttachment",
            $"{flow}: qpdf must find no FileAttachment annotation in the object graph");
        var saved = File.ReadAllBytes(outputPath);
        foreach (var secret in Secrets)
            SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty($"{flow}: {secret}");
    }

    // ───────────── #1582: name-tree key, page /AF, nested compressed PDF ─────────────

    private const string Token = "KESTRELTOKEN-1582";

    private static byte[] Flate(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.ASCII.GetBytes(text));
        return ms.ToArray();
    }

    private static byte[] RawPdf(IReadOnlyList<byte[]> bodies)
    {
        var ms = new MemoryStream();
        void Put(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        Put("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Position);
            Put($"{i + 1} 0 obj\n");
            ms.Write(bodies[i]);
            Put("\nendobj\n");
        }
        var xref = ms.Position;
        Put($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Put($"{o:D10} 00000 n \n");
        Put($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] StreamBody(string dict, byte[] data)
        => Encoding.Latin1.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n")
            .Concat(data).Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray();

    private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>The inner PDF: page text, Flate-compressed, so raw bytes never show the token.</summary>
    private static byte[] InnerPdf() => RawPdf(new[]
    {
        Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
        Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
        StreamBody("/Filter /FlateDecode", Flate($"BT /F1 12 Tf 72 700 Td (Inner memo {Token} ends) Tj ET")),
        Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
    });

    /// <summary>
    /// #1582's three carriers on one page that also shows the token: a name-tree
    /// KEY holding it (the filespec's /F is neutral), a text file attached only
    /// through the page /AF, and an attached PDF whose compressed page text holds it.
    /// </summary>
    private static byte[] Issue1582Fixture()
    {
        var inner = InnerPdf();
        Encoding.Latin1.GetString(inner).Should().NotContain(Token, "guard: the nested PDF compresses its text");
        return RawPdf(new[]
        {
            Ascii($"<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [({Token}.txt) 6 0 R (inner.pdf) 8 0 R] >> >> >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /AF [10 0 R] >>"),
            StreamBody("", Ascii($"BT /F1 12 Tf 72 700 Td (Name {Token} here) Tj ET")),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Ascii("<< /Type /Filespec /F (neutral.txt) /UF (neutral.txt) /EF << /F 7 0 R >> >>"),
            StreamBody("/Type /EmbeddedFile", Ascii("nothing to see here")),
            Ascii("<< /Type /Filespec /F (inner.pdf) /UF (inner.pdf) /EF << /F 9 0 R >> >>"),
            StreamBody("/Type /EmbeddedFile /Subtype /application#2Fpdf", inner),
            Ascii("<< /Type /Filespec /F (page-notes.txt) /UF (page-notes.txt) /AFRelationship /Supplement /EF << /F 11 0 R >> >>"),
            StreamBody("/Type /EmbeddedFile", Ascii($"page note mentions {Token} once")),
        });
    }

    private static (int ExitCode, byte[] Stdout) RunTool(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{tool} timed out");
        }
        copy.Wait();
        return (process.ExitCode, output.ToArray());
    }

    /// <summary>
    /// Every place qpdf (not excise) reports the token: in an object's JSON
    /// (strings, name-tree keys) or in its fully decoded stream data.
    /// </summary>
    private static List<string> QpdfObjectsHolding(string path, string token)
    {
        var (_, json) = RunTool("qpdf", "--json", "--json-key=qpdf", "--decode-level=all",
            "--json-stream-data=inline", path);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<string>();
        foreach (var obj in doc.RootElement.GetProperty("qpdf")[1].EnumerateObject())
        {
            if (!obj.Name.StartsWith("obj:", StringComparison.Ordinal)) continue;
            var raw = obj.Value.GetRawText();
            var holds = raw.Contains(token, StringComparison.Ordinal);
            if (!holds && obj.Value.TryGetProperty("stream", out var stream)
                && stream.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            {
                holds = Encoding.Latin1.GetString(Convert.FromBase64String(data.GetString()!)).Contains(token, StringComparison.Ordinal);
            }
            if (holds) hits.Add(obj.Name);
        }
        return hits;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Issue1582_NameTreeKey_PageAf_AndNestedPdf_AreClean_AsQpdfAndMutoolSeeIt(bool keepAttachments)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool is not installed");

        var inputPath = Write(Issue1582Fixture());
        QpdfObjectsHolding(inputPath, Token).Should().HaveCountGreaterThanOrEqualTo(3,
            "guard: qpdf sees the token in the page, the name tree and the page /AF file");
        var innerIn = Path.Combine(Path.GetTempPath(), $"excise-1582-inner-in-{Guid.NewGuid():N}.pdf");
        _temp.Add(innerIn);
        File.WriteAllBytes(innerIn, RunTool("qpdf", "--show-attachment=inner.pdf", inputPath).Stdout);
        MutoolTextExtractor.ExtractPage(innerIn, 1).Should().Contain(Token, "guard: mutool reads the nested PDF's text");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var report = document.RedactText(Token, new RedactionOptions { KeepAttachments = keepAttachments });
        var outputPath = Write(document.SaveToBytes());

        QpdfObjectsHolding(outputPath, Token).Should().BeEmpty("qpdf must find the token in no object and no decoded stream");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), Token).Should().BeEmpty();

        var byName = report.Attachments.ToDictionary(a => a.Name);
        if (!keepAttachments)
        {
            report.Attachments.Should().OnlyContain(a => a.Disposition == AttachmentDisposition.Removed);
            Pdfdetach(outputPath)?.Should().Contain("0 embedded files");
            return;
        }

        byName["neutral.txt"].Disposition.Should().Be(AttachmentDisposition.Removed,
            "its name-tree key held the term");
        byName["page-notes.txt"].Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved);
        byName["inner.pdf"].Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved);
        report.IsCleanSuccess.Should().BeTrue(report.ToString());

        var (code, innerBytes) = RunTool("qpdf", "--show-attachment=inner.pdf", outputPath);
        code.Should().Be(0, "the nested PDF is kept");
        var innerOut = Path.Combine(Path.GetTempPath(), $"excise-1582-inner-out-{Guid.NewGuid():N}.pdf");
        _temp.Add(innerOut);
        File.WriteAllBytes(innerOut, innerBytes);
        var innerText = MutoolTextExtractor.ExtractPage(innerOut, 1);
        innerText.Should().NotContain(Token, "mutool must not read the term in the kept nested PDF")
            .And.Contain("Inner memo", "the rest of the nested page survives");
        RunTool("qpdf", "--show-attachment=neutral.txt", outputPath).ExitCode.Should().NotBe(0,
            "qpdf must not list the file whose key held the term");
    }
}
