using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #2037 through <c>excise redact</c>: a dynamic XFA form excise laid out carries generated AcroForm
/// widgets, a hidden one holding a copy of the value. The engine flattens them before it locates the
/// term (decision 17), the CLI prints the flatten, and the output holds the value nowhere: the
/// inflating scanner, qpdf's decoded object dump and mutool's text read it.
/// </summary>
public class RedactXfaGeneratedFieldsTests : IDisposable
{
    private const string Secret = "Quillfeather";
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp)
            if (File.Exists(f)) try { File.Delete(f); } catch (IOException) { }
    }

    private string TempPath(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-2037-{tag}-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    /// <summary>
    /// A one-page dynamic XFA form, written as raw PDF (this assembly cannot reach Core's object store,
    /// which XfaTestForms uses): FullName shows the secret; Shadow, hidden, binds the same data node.
    /// </summary>
    private static byte[] DynamicForm()
    {
        const string xdp =
            "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\" layout=\"position\">"
            + "<pageSet><pageArea name=\"Page1\" id=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"8in\" h=\"10.5in\"/>"
            + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/></pageArea></pageSet>"
            + "<field name=\"FullName\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui></field>"
            + "<field name=\"Shadow\" presence=\"hidden\" x=\"1in\" y=\"3in\" w=\"4in\" h=\"0.4in\"><ui><textEdit/></ui>"
            + "<bind match=\"dataRef\" ref=\"$record.FullName\"/></field>"
            + "</subform></template>"
            + "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1><FullName>Jane " + Secret
            + "</FullName></form1></xfa:data></xfa:datasets>"
            + "</xdp:xdp>";
        const string placeholder = "BT /F1 12 Tf 72 700 Td (Please wait for the form to load) Tj ET";
        static string Stream(string data) => $"<< /Length {Encoding.UTF8.GetByteCount(data)} >>\nstream\n{data}\nendstream";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Fields [] /XFA 7 0 R >>",
            Stream(placeholder),
            Stream(xdp),
        };
        var output = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(output.ToString()));
            output.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.UTF8.GetByteCount(output.ToString());
        output.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            output.Append(offset.ToString("D10")).Append(" 00000 n \n");
        output.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private string LaidOutForm()
    {
        using var document = PdfDocument.Open(DynamicForm());
        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken).Status.Should().Be(XfaLayoutStatus.LaidOut);
        document.GetAcroForm()!.FindField("form1[0].Shadow[0]")!.Value.Should().Be($"Jane {Secret}",
            "fixture sanity: the hidden generated field holds the value");
        var path = TempPath("in");
        document.Save(path);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), Secret).Should().NotBeEmpty("positive control: the scanner sees the copies");
        return path;
    }

    [Fact]
    public async Task Redact_LaidOutXfaForm_FlattensTheGeneratedFields_SaysSo_AndLeavesNoCopy()
    {
        var input = LaidOutForm();
        var output = TempPath("out");

        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(new[] { "redact", input, output, Secret, "--allow-low-confidence" });
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        var stdout = captured.ToString();
        exitCode.Should().Be(0, stdout);
        var saved = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty("no hidden widget keeps the value");
        using (var reopened = PdfDocument.Open(saved))
        {
            (reopened.GetAcroForm()?.Fields ?? Array.Empty<PdfField>()).Should().BeEmpty();
            reopened.DetectXfaForm().Should().Be(PdfXfaFormKind.None, "decision 5");
        }

        if (QpdfReferenceTool.IsAvailable)
        {
            var dump = Encoding.Latin1.GetString(QpdfReferenceTool.DecodedObjectDump(output)!);
            dump.Should().NotContain(Secret).And.NotContain("/Widget");
        }
        if (MutoolReferenceRenderer.IsAvailable)
            MutoolTextExtractor.ExtractPage(output, 1).Should().NotContain(Secret).And.Contain("Jane");
        stdout.Should().Contain("generated XFA fields flattened", "the user is told the generated fields were baked into the page");
    }
}
