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
using Excise.Core.Document;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

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
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-{Guid.NewGuid():N}.pdf");
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

    private static string WriteTempCheckboxStyledFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-checkstyled-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildCheckboxStyledFormPdf());
        return path;
    }

    // #1897: a plain /FT /Tx field the form designer drew as a small square
    // (a "type an x to check" box, e.g. the IRS W-9's classification-letter
    // box, 28.8x11pt) alongside an ordinary wide text field, both with no
    // /Ff bits at all — the field TYPE is Text either way, only the box
    // shape tells them apart.
    private static byte[] BuildCheckboxStyledFormPdf()
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
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Check) /V (x) /MaxLen 1 /Rect [72 700 100.8 711] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Wide) /V (Alice) /Rect [72 650 400 670] /P 3 0 R >>");
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

    private static string WriteTempMultilineFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-multiline-{Guid.NewGuid():N}.pdf");
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

    private static string WriteTempMultilineWithDaFormPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-multiline-da-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildMultilineWithDaFormPdf());
        return path;
    }

    // #1898: a tall box (200pt — h*0.6 would give 120pt, absurd for a field
    // with its own 14pt /DA) whose /Ff bit-12 IS set, so this isolates part
    // (b) of the fix (FontSize/TextWrapping) from part (a) (the XFA signal,
    // covered in Excise.Core.Tests.Document.PdfFieldXfaMultilineTests).
    private static byte[] BuildMultilineWithDaFormPdf()
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
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Address) /V (123 Main St) /Ff 4096 /DA (/Helv 14 Tf 0 g) /Rect [72 500 300 700] /P 3 0 R >>");
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

    // #1898: the IRS W-9's "Requester's name and address" (f1_09) in
    // miniature — a 186x38pt widget with /Ff bit 12 set, no /DA of its own,
    // and the AcroForm-level /DA "/Helv 0 Tf" (ISO 32000-2 12.7.4.3: size 0
    // is auto-size, which excise falls back from to a fixed point size).
    private static byte[] BuildW9ShapedMultilineFormPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) >> >>");
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
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (f1_09) /V () /Ff 4096 /Rect [396 562 582.2 600] /P 3 0 R >>");
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
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-radio-{Guid.NewGuid():N}.pdf");
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
            var vm = MainWindowViewModelTestFactory.Create();
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
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
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
            var vm = MainWindowViewModelTestFactory.Create();
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
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
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
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
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
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
            for (int i = 0; i < 30 && formLayer!.Children.Count == 0; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            formLayer!.Children.OfType<CheckBox>().Should().BeEmpty(
                "radio groups should not be exposed as a single boolean checkbox");
            var combo = formLayer.Children.OfType<ComboBox>().Single();
            combo.SelectedItem.Should().Be("Choice2");

            // #1446: the shown label is escaped, the stored option and committed value stay raw.
            var label = combo.ItemTemplate!.Build("a\u202Eb").Should().BeOfType<TextBlock>().Subject;
            label.Text.Should().Be(Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay("a\u202Eb"))
                .And.NotContain("\u202E");
            combo.ItemsSource!.Cast<string>().Should().Contain("Choice1", "the option list itself is not rewritten");

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

    [FixedAvaloniaFact]
    public async Task CheckboxStyledTextField_CentersContent_WideFieldStaysLeftAligned()
    {
        var path = WriteTempCheckboxStyledFormPdf();
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
            for (int i = 0; i < 30 && formLayer!.Children.OfType<TextBox>().Count() < 2; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var textBoxes = formLayer!.Children.OfType<TextBox>().ToList();
            textBoxes.Should().HaveCount(2);

            var checkboxStyled = textBoxes.Single(t => t.Text == "x");
            checkboxStyled.HorizontalContentAlignment.Should().Be(
                global::Avalonia.Layout.HorizontalAlignment.Center,
                "a checkbox-sized text field should center its typed mark like a checkmark");
            checkboxStyled.TextAlignment.Should().Be(global::Avalonia.Media.TextAlignment.Center);

            var wideField = textBoxes.Single(t => t.Text == "Alice");
            wideField.HorizontalContentAlignment.Should().Be(
                global::Avalonia.Layout.HorizontalAlignment.Left,
                "an ordinary wide text field must keep its existing left alignment");
            wideField.TextAlignment.Should().Be(global::Avalonia.Media.TextAlignment.Left);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task MultilineTextField_WrapsAndUsesDaFontSize_NotBoxHeight()
    {
        var path = WriteTempMultilineWithDaFormPdf();
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
            for (int i = 0; i < 30 && formLayer!.Children.OfType<TextBox>().Count() < 1; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var textBox = formLayer!.Children.OfType<TextBox>().Single();
            textBox.AcceptsReturn.Should().BeTrue();
            textBox.TextWrapping.Should().Be(global::Avalonia.Media.TextWrapping.Wrap,
                "a multi-line field must wrap long text at its own width instead of scrolling");
            // /DA is in PDF points; the overlay canvas is in layer units
            // (dips at the page's logical DPI), so compare font to box in
            // the same units: 14pt of a 200pt-tall /Rect.
            (textBox.FontSize / textBox.Height).Should().BeApproximately(14.0 / 200.0, 0.001,
                "the field's own /DA point size must be used, in the box's own units, not a fraction of the 200pt-tall box");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task MultilineTextField_AutoSizedDa_UsesPointSizeInLayerUnits_AndWraps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-form-w9-multiline-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildW9ShapedMultilineFormPdf());
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);

            await vm.LoadDocumentAsync(path);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var formLayer = viewer!.SinglePagePart.FormFieldsLayer;
            for (int i = 0; i < 30 && formLayer!.Children.OfType<TextBox>().Count() < 1; i++)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }

            var textBox = formLayer!.Children.OfType<TextBox>().Single();
            const double rectHeightPt = 38.0;
            double layerUnitsPerPoint = textBox.Height / rectHeightPt;
            double fontPt = textBox.FontSize / layerUnitsPerPoint;
            _out.WriteLine($"box {textBox.Width:F1}x{textBox.Height:F1} layer units, " +
                           $"{layerUnitsPerPoint:F3} units/pt, FontSize {textBox.FontSize:F2} units = {fontPt:F2}pt");

            // /DA size 0 (auto) falls back to the plain 10pt default — a
            // point size, so in the box's own units it is 10/38 of its
            // height, however the layer is scaled.
            fontPt.Should().BeApproximately(10.0, 0.01,
                "an auto-sized multi-line field gets a normal 10pt line, not a size in raw layer units");

            // Independent oracle: Avalonia's own text layout engine, fed the
            // box's font, wrapping and content width. Long text must wrap
            // onto several lines, none wider than the box, and the box must
            // hold at least two of those lines (the W-9 box is meant for a
            // name and an address).
            var text = string.Join(' ', Enumerable.Repeat("Requester name and address", 6));
            double contentWidth = textBox.Width - textBox.Padding.Left - textBox.Padding.Right
                - textBox.BorderThickness.Left - textBox.BorderThickness.Right;
            double contentHeight = textBox.Height - textBox.Padding.Top - textBox.Padding.Bottom
                - textBox.BorderThickness.Top - textBox.BorderThickness.Bottom;
            using var layout = new global::Avalonia.Media.TextFormatting.TextLayout(
                text,
                new global::Avalonia.Media.Typeface(textBox.FontFamily),
                textBox.FontSize,
                foreground: null,
                textWrapping: textBox.TextWrapping,
                maxWidth: contentWidth);
            var lines = layout.TextLines;
            double lineHeight = lines[0].Height;
            _out.WriteLine($"wrapped into {lines.Count} lines, widest {lines.Max(l => l.Width):F1} of {contentWidth:F1}, " +
                           $"{contentHeight / lineHeight:F2} lines fit");
            lines.Count.Should().BeGreaterThan(1, "a multi-line field wraps long text instead of scrolling");
            lines.Max(l => l.Width).Should().BeLessThanOrEqualTo(contentWidth);
            (contentHeight / lineHeight).Should().BeGreaterThanOrEqualTo(2,
                "a 38pt-tall multi-line box holds at least two lines at its own font size");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// #1842 step 1: an edit to a field that does not know its own page is reported on
    /// the viewer's page AT COMMIT TIME, not the page current when the input was built.
    /// The shared input factory is static and reports through <c>IFormFieldEditSink</c>;
    /// an input outlives page changes (a continuous slot's inputs always do, and a
    /// bare single-page overlay keeps its inputs until the host resets
    /// <c>FormFields</c>), so a page captured at build time would be stale.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task EditToAFieldWithNoPage_IsReportedOnTheCurrentPageAtCommitTime()
    {
        var document = PdfDocument.Open(BuildPagelessFieldPdf());
        var field = document.GetAcroForm()!.Fields.Single();
        field.PageNumber.Should().BeNull("fixture: the widget is on no page's /Annots and has no /P");

        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        try
        {
            viewer.Document = document;
            viewer.FormFields = new[] { field };
            var layer = viewer.SinglePagePart.FormFieldsLayer!;
            for (int i = 0; i < 40 && (viewer.IsLoading || layer.Children.Count == 0); i++)
            {
                await Task.Delay(25);
                window.UpdateLayout();
            }
            var box = layer.Children.OfType<TextBox>().Single();
            viewer.CurrentPage.Should().Be(1, "fixture: the input was built on page 1");

            viewer.CurrentPage = 2;
            for (int i = 0; i < 40 && viewer.IsLoading; i++) { await Task.Delay(25); window.UpdateLayout(); }
            layer.Children.OfType<TextBox>().Single().Should().BeSameAs(box,
                "fixture: a page turn does not rebuild the overlay by itself");

            var edits = new System.Collections.Generic.List<int>();
            viewer.FormFieldEdited += (_, e) => edits.Add(e.PageNumber);
            box.Text = "typed on page 2";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });

            field.Value.Should().Be("typed on page 2", "Enter commits a single-line field");
            edits.Should().Equal(new[] { 2 },
                "a field with no page of its own is reported on the page current when the edit is committed");
        }
        finally
        {
            window.Close();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            document.Dispose();
        }
    }

    /// <summary>
    /// #1842 step 1 / #1874: the host's <c>FormFieldEditGate</c> is asked before a
    /// value is stored, through the sink the shared input factory reports to. A
    /// refusal stores nothing, reports nothing, and puts the stored value back in
    /// the box. No other viewer test drove the gate through a real input.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task RefusedByTheEditGate_StoresNothing_ReportsNothing_AndRestoresTheBox()
    {
        var document = PdfDocument.Open(BuildPagelessFieldPdf());
        var field = document.GetAcroForm()!.Fields.Single();

        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        try
        {
            viewer.Document = document;
            int asked = 0;
            viewer.FormFieldEditGate = () => { asked++; return false; };
            viewer.FormFields = new[] { field };
            var layer = viewer.SinglePagePart.FormFieldsLayer!;
            for (int i = 0; i < 40 && (viewer.IsLoading || layer.Children.Count == 0); i++)
            {
                await Task.Delay(25);
                window.UpdateLayout();
            }
            var box = layer.Children.OfType<TextBox>().Single();

            int edits = 0;
            viewer.FormFieldEdited += (_, _) => edits++;
            box.Text = "not allowed";
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });

            asked.Should().Be(1, "the gate is asked before the value is stored");
            field.Value.Should().Be("start", "a refused edit is not stored");
            edits.Should().Be(0, "a refused edit is not reported as an edit");
            box.Text.Should().Be("start", "the box shows the stored value again");
        }
        finally
        {
            window.Close();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            document.Dispose();
        }
    }

    /// <summary>Two pages; one text field whose widget no page lists and that names no /P.</summary>
    private static byte[] BuildPagelessFieldPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Pageless) /V (start) /Rect [72 700 300 720] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
