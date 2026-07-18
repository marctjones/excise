using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.App.ViewModels;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class FormWorkflowTests
{
    private static string WriteTempFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-workflow-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildFormPdf());
        return path;
    }

    private static byte[] BuildFormPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>");
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
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_PreservesFilledInteractiveFormValue()
    {
        var inputPath = WriteTempFormPdf();
        var outputPath = Path.Combine(Path.GetTempPath(), $"excise-form-filled-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = new MainWindowViewModel();
            await vm.LoadDocumentAsync(inputPath);

            var field = vm.PdfCoreDocument!.GetAcroForm()!.FindField("Name")!;
            field.SetValue("Bob");
            vm.OnFormFieldEdited("Name", "Bob");

            await vm.SaveFileAsAsync(outputPath);

            using var reopened = PdfDocument.Open(outputPath);
            var form = reopened.GetAcroForm();
            form.Should().NotBeNull("Save As should preserve an interactive form");
            form!.FindField("Name")!.Value.Should().Be("Bob");
            form.NeedsAppearances.Should().BeTrue();
            vm.FileState.HasUnsavedChanges.Should().BeFalse();
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task SaveFlattenedFormCopyAsAsync_BakesFormValueAndRemovesAcroForm()
    {
        var inputPath = WriteTempFormPdf();
        var outputPath = Path.Combine(Path.GetTempPath(), $"excise-form-flat-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = new MainWindowViewModel();
            await vm.LoadDocumentAsync(inputPath);

            var field = vm.PdfCoreDocument!.GetAcroForm()!.FindField("Name")!;
            field.SetValue("Carol");
            vm.OnFormFieldEdited("Name", "Carol");

            await vm.SaveFlattenedFormCopyAsAsync(outputPath);

            using var reopened = PdfDocument.Open(outputPath);
            reopened.GetAcroForm().Should().BeNull("flattened form copies should not retain interactive form fields");
            Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes())
                .Should().Contain("(Carol) Tj");
            reopened.GetPage(1).GetAnnotations().Should().BeEmpty();
            vm.FileState.HasUnsavedChanges.Should().BeFalse();
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }
}
