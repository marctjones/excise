using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Document;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Tests for the form-authoring CLI subcommands: add-field and
/// autodetect-fields.
/// </summary>
public class AddFieldCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-author-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Bare PDF — no AcroForm. Inputs to authoring tests.</summary>
    private static byte[] BarePdf(string contentStream = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {contentStream.Length} >>");
        sb.AppendLine("stream");
        sb.AppendLine(contentStream);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void RunAddField_TextField_AddsToBlankPdf()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        Program.RunAddField(input, output, "Text", "Name", page: 1,
            rectStr: "72,700,300,720", value: "default", options: Array.Empty<string>());

        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        var f = doc.GetAcroForm()!.FindField("Name");
        f.Should().NotBeNull();
        f!.FieldType.Should().Be(PdfFieldType.Text);
        f.Value.Should().Be("default");
    }

    [Fact]
    public void RunAddField_Checkbox_AddsButtonField()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        Program.RunAddField(input, output, "Checkbox", "Agree", page: 1,
            rectStr: "72,700,90,720", value: "Yes", options: Array.Empty<string>());

        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        var f = doc.GetAcroForm()!.FindField("Agree")!;
        f.FieldType.Should().Be(PdfFieldType.Button);
        f.Value.Should().Be("Yes");
    }

    [Fact]
    public void RunAddField_Choice_RequiresOptions()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        var act = () => Program.RunAddField(input, output, "Choice", "Country",
            page: 1, rectStr: "72,700,200,720", value: null, options: Array.Empty<string>());

        act.Should().Throw<ArgumentException>().WithMessage("*--option is required*");
    }

    [Fact]
    public void RunAddField_Choice_AddsWithOptions()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        Program.RunAddField(input, output, "Choice", "Country", page: 1,
            rectStr: "72,700,200,720", value: "US", options: new[] { "US", "UK" });

        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        var f = doc.GetAcroForm()!.FindField("Country")!;
        f.FieldType.Should().Be(PdfFieldType.Choice);
        f.Options.Should().BeEquivalentTo(new[] { "US", "UK" });
    }

    [Fact]
    public void RunAddField_BadType_Throws()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        var act = () => Program.RunAddField(input, output, "Banana", "F",
            page: 1, rectStr: "72,700,90,720", value: null, options: Array.Empty<string>());

        act.Should().Throw<ArgumentException>().WithMessage("*Unknown field type*");
    }

    [Fact]
    public void RunAddField_BadRect_Throws()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        var act = () => Program.RunAddField(input, output, "Text", "F",
            page: 1, rectStr: "not-a-rect", value: null, options: Array.Empty<string>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_AddField_EndToEnd()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf());

        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "add-field", input, output,
                "--type", "Text",
                "--name", "Email",
                "--page", "1",
                "--rect", "72,700,300,720"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        // stdout content is racy under xUnit parallel test execution
        // (Console.Out is a process-wide singleton). Assert on the
        // observable side-effect — the field landed in the output PDF —
        // rather than on captured chatter.
        File.Exists(output).Should().BeTrue();
        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        doc.GetAcroForm()!.FindField("Email").Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_AutodetectFields_PrintsAndApplies()
    {
        var input = TempPath(".pdf");
        var output = TempPath(".pdf");
        File.WriteAllBytes(input, BarePdf("100 700 m 300 700 l S\n320 700 12 12 re S"));

        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[]
            {
                "autodetect-fields", input, output, "--apply"
            });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        exitCode.Should().Be(0);
        // See note in RunAsync_AddField_EndToEnd: assert on the file, not
        // on captured stdout (which races with sibling tests).
        using var doc = PdfDocument.Open(File.ReadAllBytes(output));
        doc.GetAcroForm()!.Fields.Should().HaveCount(2);
    }
}
