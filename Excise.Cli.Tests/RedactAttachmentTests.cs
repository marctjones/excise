using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Automation;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1572 on the CLI and batch surfaces: <c>excise redact</c> and
/// <c>redaction.apply</c> remove every attachment by default (the 2026-09-17
/// decision), name each one, keep them only on request, and refuse a
/// portfolio. The removal claim is checked on the saved bytes and, when
/// poppler is installed, by <c>pdfdetach</c>.
/// </summary>
public sealed class RedactAttachmentTests : IDisposable
{
    private const string PageWord = "SECRET";
    private const string DocPayload = "DOCATTACH-PAYLOAD-5K1";
    private const string AnnotPayload = "ANNOTATTACH-PAYLOAD-5K1";
    private const string AnnotDesc = "ANNOTATTACH-DESC-5K1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-redact-attach-{Guid.NewGuid():N}");

    public RedactAttachmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// One page reading "SECRET PUBLIC", a document-level text attachment,
    /// and a /FileAttachment annotation whose PNG payload excise cannot read.
    /// </summary>
    private static byte[] AttachedPdf(bool portfolio = false)
    {
        const string content = "BT /F1 12 Tf 72 700 Td (SECRET PUBLIC) Tj ET";
        var png = $"\u0089PNG {AnnotPayload}";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(doc.txt) 7 0 R] >> >>"
                + (portfolio ? " /Collection << /View /D >>" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [72 600 92 620] /Contents (note) /FS 9 0 R >>",
            "<< /Type /Filespec /F (doc.txt) /UF (doc.txt) /EF << /F 8 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"doc says {DocPayload}"),
            $"<< /Type /Filespec /F (scan.png) /UF (scan.png) /Desc ({AnnotDesc}) /EF << /F 10 0 R >> >>",
            Stream("/Type /EmbeddedFile /Subtype /image#2Fpng", png),
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    private string Input(bool portfolio = false)
    {
        var path = Path.Combine(_dir, portfolio ? "portfolio.pdf" : "input.pdf");
        File.WriteAllBytes(path, AttachedPdf(portfolio));
        return path;
    }

    private static string? PdfdetachList(string path)
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
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public void Redact_RemovesEveryAttachment_ByDefault_AndNamesThem()
    {
        var input = Input();
        var output = Path.Combine(_dir, "out.pdf");
        var before = File.ReadAllBytes(input);
        foreach (var secret in new[] { DocPayload, AnnotPayload, AnnotDesc })
            SavedPdfLeakScanner.FindTerm(before, secret).Should().NotBeEmpty($"guard: {secret}");

        var result = RedactCommandHandler.Execute(new RedactCommandRequest(input, output, PageWord));

        var saved = File.ReadAllBytes(output);
        foreach (var secret in new[] { DocPayload, AnnotPayload, AnnotDesc })
            SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty(secret);
        result.Attachments.Select(a => a.Name).Should().BeEquivalentTo(new[] { "doc.txt", "scan.png" });
        result.CarrierNotes.Should().Contain(n => n.StartsWith("ATTACHMENT REMOVED: doc.txt", StringComparison.Ordinal));
        result.CarrierNotes.Should().Contain(n => n.StartsWith("ATTACHMENT REMOVED: scan.png", StringComparison.Ordinal));
        result.CarrierNotes.Should().NotContain(n => n.Contains("NOT clean"));

        if (PdfdetachList(input) is { } listed)
        {
            listed.Should().Contain("2 embedded files", "guard: poppler sees both attachments before");
            PdfdetachList(output).Should().Contain("0 embedded files",
                "poppler — not excise — finds no attachment of either kind afterwards");
        }
    }

    [Fact]
    public void Redact_KeepAttachments_KeepsThem_AndSaysTheUnreadableOneWasNotChecked()
    {
        var input = Input();
        var output = Path.Combine(_dir, "kept.pdf");

        var result = RedactCommandHandler.Execute(new RedactCommandRequest(
            input, output, PageWord, KeepAttachments: true));

        var saved = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(saved, DocPayload).Should().NotBeEmpty("kept");
        SavedPdfLeakScanner.FindTerm(saved, AnnotPayload).Should().NotBeEmpty("kept");
        result.Attachments.Single(a => a.Name == "doc.txt").Disposition
            .Should().Be(AttachmentDisposition.KeptTermNotFound);
        result.Attachments.Single(a => a.Name == "scan.png").Disposition
            .Should().Be(AttachmentDisposition.KeptNotChecked);
        result.CarrierNotes.Should().Contain(n => n.StartsWith("ATTACHMENT KEPT: scan.png", StringComparison.Ordinal)
                                                 && n.Contains("NOT checked"));
        result.CarrierNotes.Should().Contain(n => n.Contains("NOT clean"),
            "an unchecked attachment makes the redaction unclean");
    }

    [Fact]
    public void Redact_Portfolio_IsRefused_AndWritesNothing()
    {
        var input = Input(portfolio: true);
        var output = Path.Combine(_dir, "portfolio-out.pdf");

        var act = () => RedactCommandHandler.Execute(new RedactCommandRequest(input, output, PageWord));

        act.Should().Throw<PdfPortfolioRedactionException>().WithMessage("*portfolio*");
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Batch_RedactionApply_ReportsRemovedAttachments_AndHonoursKeepAttachments()
    {
        var input = Input();
        var workflow = Path.Combine(_dir, "workflow.json");
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "strip", command = PdfCommandIds.ApplyRedaction, input = "input.pdf",
                    output = "stripped.pdf", text = PageWord, confirmDestructive = true,
                },
                new
                {
                    id = "keep", command = PdfCommandIds.ApplyRedaction, input = "input.pdf",
                    output = "kept.pdf", text = PageWord, confirmDestructive = true, keepAttachments = true,
                },
            },
        }));

        var (exitCode, stdout) = await RunBatchAsync(workflow);

        exitCode.Should().Be(0, stdout);
        using var json = JsonDocument.Parse(stdout);
        var steps = json.RootElement.GetProperty("steps");
        var stripped = steps[0].GetProperty("result").GetProperty("attachments");
        stripped.EnumerateArray().Select(a => a.GetProperty("disposition").GetString())
            .Should().Equal("Removed", "Removed");
        stripped.EnumerateArray().Select(a => a.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "doc.txt", "scan.png" });
        stripped[0].GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(0);

        var kept = steps[1].GetProperty("result").GetProperty("attachments");
        kept.EnumerateArray().Select(a => a.GetProperty("disposition").GetString())
            .Should().BeEquivalentTo(new[] { "KeptTermNotFound", "KeptNotChecked" });
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(Path.Combine(_dir, "stripped.pdf")), DocPayload)
            .Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(Path.Combine(_dir, "kept.pdf")), DocPayload)
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task Batch_RedactionApply_OnAPortfolio_FailsWithPortfolioRefused()
    {
        Input(portfolio: true);
        var workflow = Path.Combine(_dir, "portfolio.json");
        File.WriteAllText(workflow, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            steps = new object[]
            {
                new
                {
                    id = "redact", command = PdfCommandIds.ApplyRedaction, input = "portfolio.pdf",
                    output = "portfolio-out.pdf", text = PageWord, confirmDestructive = true,
                },
            },
        }));

        var (exitCode, stdout) = await RunBatchAsync(workflow);

        exitCode.Should().NotBe(0);
        stdout.Should().Contain("PORTFOLIO_REFUSED");
        File.Exists(Path.Combine(_dir, "portfolio-out.pdf")).Should().BeFalse();
    }

    private static async Task<(int ExitCode, string StdOut)> RunBatchAsync(string workflow)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        Console.SetError(new StringWriter());
        Environment.ExitCode = 0;
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(["batch", workflow, "--json"]);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }

        var processExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        return (exitCode == 0 ? processExitCode : exitCode, capturedOut.ToString());
    }
}
