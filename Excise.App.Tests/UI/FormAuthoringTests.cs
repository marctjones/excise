using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Headless tests for form authoring: enter authoring mode, fire a
/// FormFieldRectDrawn event, assert a real field gets created. Also covers
/// the auto-detect button.
/// </summary>
[Collection("AvaloniaTests")]
public class FormAuthoringTests
{
    private readonly ITestOutputHelper _out;
    public FormAuthoringTests(ITestOutputHelper o) { _out = o; }

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

    private static string WritePdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-author-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [FixedAvaloniaFact]
    public async Task ToggleFormAuthoringMode_FlipsInteractionMode()
    {
        var path = WritePdf(BarePdf());
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);

            vm.InteractionMode.Should().Be(InteractionMode.None);

            vm.ToggleFormAuthoringModeCommand.Execute().Subscribe();
            await Task.Delay(50);

            vm.IsFormAuthoringMode.Should().BeTrue();
            vm.InteractionMode.Should().Be(InteractionMode.FormAuthoring);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FixedAvaloniaFact]
    public async Task OnFormFieldRectDrawn_CreatesTextFieldWithUniqueName()
    {
        var path = WritePdf(BarePdf());
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);

            vm.FormAuthoringFieldType = PdfFieldType.Text;
            vm.OnFormFieldRectDrawn(new PdfRectangle(72, 700, 300, 720), pageNumber: 1);
            vm.OnFormFieldRectDrawn(new PdfRectangle(72, 670, 300, 690), pageNumber: 1);

            var fields = vm.PdfCoreDocument!.GetAcroForm()!.Fields;
            fields.Should().HaveCount(2);
            fields.Select(f => f.FullName).Should().BeEquivalentTo(new[] { "Text1", "Text2" },
                "field names must be unique and sequential");
            vm.FileState.HasUnsavedChanges.Should().BeTrue("authoring must mark the document dirty");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FixedAvaloniaFact]
    public async Task OnFormFieldRectDrawn_CheckboxType_CreatesButtonField()
    {
        var path = WritePdf(BarePdf());
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);

            vm.FormAuthoringFieldType = PdfFieldType.Button;
            vm.OnFormFieldRectDrawn(new PdfRectangle(100, 700, 112, 712), pageNumber: 1);

            var f = vm.PdfCoreDocument!.GetAcroForm()!.Fields.Single();
            f.FieldType.Should().Be(PdfFieldType.Button);
            f.FullName.Should().Be("Checkbox1");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FixedAvaloniaFact]
    public async Task AutoDetectFieldsCommand_ScansAndAppliesSuggestions()
    {
        // PDF with one underline and one checkbox-sized outline.
        var path = WritePdf(BarePdf("100 700 m 300 700 l S\n320 700 12 12 re S"));
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);

            int created = await vm.AutoDetectFieldsCommand.Execute().FirstAsync();

            created.Should().Be(2);
            var fields = vm.PdfCoreDocument!.GetAcroForm()!.Fields;
            fields.Should().HaveCount(2);
            fields.Select(f => f.FieldType).Should().BeEquivalentTo(
                new[] { PdfFieldType.Text, PdfFieldType.Button });
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
