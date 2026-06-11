using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Avalonia.Controls;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;
namespace PdfEditor.Tests.UI;

/// <summary>
/// Headless-GUI tests for the AcroForm field overlay. Drives the MainWindow
/// pipeline end-to-end: load a fixture PDF with an AcroForm, render the
/// page, and assert the FormFieldsLayer canvas contains an editable input
/// per field. Edit through the input, assert the field's underlying value
/// updates and the document is marked dirty.
/// </summary>
[Collection("AvaloniaTests")]
public class FormFieldsOverlayTests
{
    private readonly ITestOutputHelper _out;
    public FormFieldsOverlayTests(ITestOutputHelper o) { _out = o; }

    private static string WriteTempFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-form-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildFormPdf());
        return path;
    }

    private static byte[] BuildFormPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>");
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
        long o7 = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Ch /T (Country) /V (US) /Opt [(US) (UK)] /Rect [72 660 200 680] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine($"{o7:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string WriteTempMultilineFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-form-multiline-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildMultilineFormPdf());
        return path;
    }

    private static byte[] BuildMultilineFormPdf()
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
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Notes) /V (Line 1) /Ff 4096 /Rect [72 660 300 720] /P 3 0 R >>");
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

    private static string WriteTempRadioFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-form-radio-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildRadioFormPdf());
        return path;
    }

    private static byte[] BuildRadioFormPdf()
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
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Btn /Ff 32768 /T (Choice) /V /Choice2 /Kids [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");
        long o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 700 92 720] /P 3 0 R /AS /Off /AP << /N << /Choice1 <<>> /Off <<>> >> >> >>");
        sb.AppendLine("endobj");
        long o7 = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 660 92 680] /P 3 0 R /AS /Choice2 /AP << /N << /Choice2 <<>> /Off <<>> >> >> >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine($"{o7:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [FixedAvaloniaFact]
    public async Task FormFieldsLayer_PaintsOneInputPerField()
    {
        var path = WriteTempFormPdf();
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            // No need to wait on OperationStatus: the form-field overlay doesn't
            // depend on the background search index, and in headless mode the
            // index-build Progress callback may never pump, so that wait just
            // burned its full timeout. We wait on the real signal — the form
            // inputs appearing — below. (#363)

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            viewer.Should().NotBeNull();
            var formLayer = FindNamedDescendant<Canvas>(viewer!, "FormFieldsLayer");
            formLayer.Should().NotBeNull("PdfViewerControl should host the FormFieldsLayer canvas");

            // Wait for the binding pipeline (vm → viewer.FormFields → redraw)
            // to settle. Style/binding application happens on the dispatcher.
            for (int i = 0; i < 30 && formLayer!.Children.Count < 3; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            formLayer!.Children.Count.Should().Be(3,
                "expected one input per field: text, button (checkbox), choice (combo)");

            formLayer.Children.OfType<TextBox>().Should().HaveCount(1, "one text field input");
            formLayer.Children.OfType<CheckBox>().Should().HaveCount(1, "one button-checkbox input");
            formLayer.Children.OfType<ComboBox>().Should().HaveCount(1, "one choice combo");

            var tabOrder = formLayer.Children
                .OfType<Control>()
                .Select(c => c.TabIndex)
                .ToList();
            tabOrder.Should().Equal(new[] { 0, 1, 2 },
                "form field tab order should follow visual top-to-bottom order");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task EditingTextField_MutatesUnderlyingFieldAndMarksDirty()
    {
        var path = WriteTempFormPdf();
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);
            // No need to wait on OperationStatus: the form-field overlay doesn't
            // depend on the background search index, and in headless mode the
            // index-build Progress callback may never pump, so that wait just
            // burned its full timeout. We wait on the real signal — the form
            // inputs appearing — below. (#363)

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = FindNamedDescendant<Canvas>(viewer!, "FormFieldsLayer");
            for (int i = 0; i < 30 && formLayer!.Children.Count == 0; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var textBox = formLayer!.Children.OfType<TextBox>().First();
            textBox.Text.Should().Be("Alice");

            // Simulate the user typing a new value and pressing Enter to
            // commit. Headless harness doesn't dispatch real LostFocus when
            // window focus moves, so we drive the Enter-key path which the
            // overlay's KeyDown handler also accepts.
            textBox.Text = "Bob";
            textBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Route = RoutingStrategies.Bubble,
                Key = Key.Enter,
            });
            await Task.Delay(50);

            var field = vm.PdfCoreDocument!.GetAcroForm()!.FindField("Name")!;
            field.Value.Should().Be("Bob",
                "PdfField.SetValue must be invoked when the input loses focus");
            vm.FileState.HasUnsavedChanges.Should().BeTrue(
                "editing a form field must dirty the document so Save activates");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task MultilineTextField_EscapeRevertsAndCtrlEnterCommits()
    {
        var path = WriteTempMultilineFormPdf();
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = FindNamedDescendant<Canvas>(viewer!, "FormFieldsLayer");
            for (int i = 0; i < 30 && formLayer!.Children.Count == 0; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var textBox = formLayer!.Children.OfType<TextBox>().Single();
            textBox.AcceptsReturn.Should().BeTrue();

            textBox.Text = "Draft";
            textBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Route = RoutingStrategies.Bubble,
                Key = Key.Escape,
            });
            textBox.Text.Should().Be("Line 1");
            vm.PdfCoreDocument!.GetAcroForm()!.FindField("Notes")!.Value.Should().Be("Line 1");

            textBox.Text = "Line 1\nLine 2";
            textBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Route = RoutingStrategies.Bubble,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.Control,
            });

            vm.PdfCoreDocument!.GetAcroForm()!.FindField("Notes")!.Value.Should().Be("Line 1\nLine 2");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task RadioButtonGroup_RendersChoiceSelectorAndCommitsValue()
    {
        var path = WriteTempRadioFormPdf();
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = FindNamedDescendant<Canvas>(viewer!, "FormFieldsLayer");
            for (int i = 0; i < 30 && formLayer!.Children.Count == 0; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            formLayer!.Children.OfType<CheckBox>().Should().BeEmpty(
                "radio groups should not be exposed as a single boolean checkbox");
            var combo = formLayer.Children.OfType<ComboBox>().Single();
            combo.SelectedItem.Should().Be("Choice2");

            combo.SelectedItem = "Choice1";
            await Task.Delay(50);

            vm.PdfCoreDocument!.GetAcroForm()!.FindField("Choice")!.Value.Should().Be("Choice1");
            vm.FileState.HasUnsavedChanges.Should().BeTrue();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
        }
        if (root is Decorator d && d.Child is Control dc)
        {
            var hit = FindNamedDescendant<T>(dc, name);
            if (hit != null) return hit;
        }
        if (root is ContentControl cc && cc.Content is Control ccc)
        {
            var hit = FindNamedDescendant<T>(ccc, name);
            if (hit != null) return hit;
        }
        return root.FindControl<T>(name);
    }
}
