using System.Diagnostics;
using System.Text;
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
}
