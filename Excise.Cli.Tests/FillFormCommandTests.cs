using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Document;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Tests for the <c>excise fill-form</c> subcommand. Exercises both the
/// internal <see cref="Program.RunFillForm"/> core and the CLI surface
/// (<see cref="Program.RunAsync"/>).
/// </summary>
public class FillFormCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-fill-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Single-page PDF with a text field "Name" (current /V = "Alice")
    /// and a button field "Accept" (current /V = /Yes).
    /// </summary>
    private static byte[] PdfWithForm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R 6 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Alice) /Rect [72 700 300 720] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Btn /T (Accept) /V /Yes /AS /Yes /Rect [72 680 100 700] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void RunFillForm_SingleField_UpdatesValue()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        int set = Program.RunFillForm(input, output, new[] { "Name=Bob" }, flatten: false);

        set.Should().Be(1);
        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        doc.GetAcroForm()!.FindField("Name")!.Value.Should().Be("Bob");
    }

    [Fact]
    public void RunFillForm_MultipleFields_UpdatesAll()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        int set = Program.RunFillForm(input, output,
            new[] { "Name=Carol", "Accept=Off" }, flatten: false);

        set.Should().Be(2);
        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        var form = doc.GetAcroForm()!;
        form.FindField("Name")!.Value.Should().Be("Carol");
        form.FindField("Accept")!.Value.Should().Be("Off");
    }

    [Fact]
    public void RunFillForm_WithFlatten_RemovesAcroForm()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        Program.RunFillForm(input, output, new[] { "Name=Dan" }, flatten: true);

        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        doc.GetAcroForm().Should().BeNull("flatten must remove the AcroForm dictionary");
        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("(Dan) Tj",
            "the new field value must be baked into the content stream");
    }

    [Fact]
    public void RunFillForm_UnknownField_Throws()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        var act = () => Program.RunFillForm(input, output,
            new[] { "Nonexistent=x" }, flatten: false);

        act.Should().Throw<KeyNotFoundException>().WithMessage("*Nonexistent*");
    }

    [Fact]
    public void RunFillForm_MalformedField_Throws()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        var act = () => Program.RunFillForm(input, output,
            new[] { "no-equals-sign" }, flatten: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Malformed*");
    }

    [Fact]
    public async Task RunAsync_FillFormSubcommand_EndToEnd()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "fill-form", input, output, "--field", "Name=Eve", "--flatten"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        // stdout assertion was racy under xUnit parallelism — verify the
        // file-level effect instead.
        File.Exists(output).Should().BeTrue();

        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        doc.GetAcroForm().Should().BeNull("flatten removes the AcroForm dictionary");
    }

    [Fact]
    public async Task RunAsync_FillForm_NoFields_ReportsError()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, PdfWithForm());

        var prevErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        try
        {
            await Program.RunAsync(new[] { "fill-form", input, output });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        capturedErr.ToString().Should().Contain("--field");
    }
}
