using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>#1959: the shared single/continuous input factory follows edited text direction.</summary>
[Collection("AvaloniaTests")]
public class FormFieldTextDirectionTests
{
    [FixedAvaloniaTheory]
    [InlineData("שלום", true)]
    [InlineData("مرحبا", true)]
    [InlineData("123 ... שלום", true)]
    [InlineData("١٢٣ مرحبا", true)]
    [InlineData("Reviewed by أحمد", false)]
    [InlineData("، reviewed", false)]
    [InlineData("", false)]
    [InlineData("123 ...", false)]
    public void InitialValue_UsesFirstStrongLetter(string text, bool rtl)
    {
        using var doc = Document();
        var field = Field(doc, text);
        var box = Build(field, new Sink());

        box.FlowDirection.Should().Be(rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
        box.TextAlignment.Should().Be(TextAlignment.Start);
    }

    [FixedAvaloniaFact]
    public void EditingEscapeAndRefusal_RecomputeDirectionWithoutStoringAnEdit()
    {
        using var doc = Document();
        var field = Field(doc, "שלום");
        var sink = new Sink { Allow = false };
        var box = Build(field, sink);

        box.Text = "Latin edit";
        box.FlowDirection.Should().Be(FlowDirection.LeftToRight);
        PressKey(box, Key.Escape);
        box.Text.Should().Be("שלום");
        box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
        sink.Stored.Should().Be(0);

        box.Text = "another Latin edit";
        PressKey(box, Key.Enter);
        field.Value.Should().Be("שלום");
        box.Text.Should().Be("שלום");
        box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
        sink.Stored.Should().Be(0);

        sink.Allow = true;
        box.Text = "committed";
        PressKey(box, Key.Enter);
        field.Value.Should().Be("committed");
        sink.Stored.Should().Be(1);
        box.FlowDirection.Should().Be(FlowDirection.LeftToRight);
        box.Text = "مرحبا";
        box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
        box.Text = "";
        box.FlowDirection.Should().Be(FlowDirection.LeftToRight);
    }

    [FixedAvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultilineAndReadOnly_KeepDirectionAndFieldBehavior(bool readOnly)
    {
        using var doc = Document();
        var field = Field(doc, "مرحبا", multiline: true, readOnly: readOnly);
        var box = Build(field, new Sink());

        box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
        box.AcceptsReturn.Should().BeTrue();
        box.TextWrapping.Should().Be(TextWrapping.Wrap);
        box.IsReadOnly.Should().Be(readOnly);
        box.IsEnabled.Should().Be(!readOnly);
    }

    [FixedAvaloniaFact]
    public void CheckboxStyledTextField_RemainsCenteredWhileDirectionChanges()
    {
        using var doc = Document();
        var field = Field(doc, "שלום", small: true);
        var box = Build(field, new Sink());

        box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
        box.TextAlignment.Should().Be(TextAlignment.Center);
        box.HorizontalContentAlignment.Should().Be(global::Avalonia.Layout.HorizontalAlignment.Center);
        box.Text = "x";
        box.FlowDirection.Should().Be(FlowDirection.LeftToRight);
        box.TextAlignment.Should().Be(TextAlignment.Center);
    }

    [FixedAvaloniaTheory(Timeout = 120000)]
    [InlineData(PdfViewMode.SinglePage)]
    [InlineData(PdfViewMode.Continuous)]
    public async Task VisibleOverlay_KeyboardEditingAndEscapeFollowDirection(PdfViewMode mode)
    {
        using var doc = Document();
        var field = doc.AddTextField(1, new PdfRectangle(72, 600, 300, 624), "value", defaultValue: "start");
        field.RawDictionary.SetString("V", "שלום");
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 1000, Height = 900 };
        var edits = 0;
        viewer.FormFieldEdited += (_, _) => edits++;
        window.Show();
        try
        {
            viewer.Document = doc;
            viewer.FormFields = new[] { field };
            viewer.PageFormFieldsProvider = page => doc.GetPage(page).GetFormFields();
            viewer.ViewMode = mode;

            TextBox? box = null;
            for (var i = 0; i < 150 && box == null; i++)
            {
                window.UpdateLayout();
                box = viewer.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Width > 0
                        && t.Text == "שלום");
                if (box == null) await Task.Delay(100);
            }
            box.Should().NotBeNull("the active overlay must expose a visible RTL input");
            box!.FlowDirection.Should().Be(FlowDirection.RightToLeft);
            box.TextAlignment.Should().Be(TextAlignment.Start);
            var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window);
            center.Should().NotBeNull();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.MouseDown(center!.Value, MouseButton.Left);
                window.MouseUp(center.Value, MouseButton.Left);
                box.SelectAll();
                window.KeyTextInput("Latin edit");
            });
            box.IsFocused.Should().BeTrue("the real click must reach the active overlay");
            box.Text.Should().Be("Latin edit");
            box.FlowDirection.Should().Be(FlowDirection.LeftToRight);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            box.Text.Should().Be("שלום");
            box.FlowDirection.Should().Be(FlowDirection.RightToLeft);
            field.Value.Should().Be("שלום");
            edits.Should().Be(0, "Escape must not commit a field edit");

            box.SelectAll();
            window.KeyTextInput("Committed");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            field.Value.Should().Be("Committed");
            box.FlowDirection.Should().Be(FlowDirection.LeftToRight);
            edits.Should().Be(1, "Enter commits exactly once through the active overlay");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static PdfDocument Document()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        return doc;
    }

    private static PdfField Field(PdfDocument doc, string text, bool multiline = false, bool readOnly = false, bool small = false)
    {
        var field = doc.AddTextField(1, small ? new PdfRectangle(20, 20, 48, 31) : new PdfRectangle(20, 20, 220, 60),
            "value", defaultValue: "start", multiline: multiline, readOnly: readOnly);
        // Existing Unicode field data; this test concerns the input, not font authoring.
        field.RawDictionary.SetString("V", text);
        return field;
    }

    private static TextBox Build(PdfField field, Sink sink) =>
        (TextBox)FormFieldInputFactory.Build(field, field.Rect!.Value.Width, field.Rect.Value.Height, 0, sink)!;

    private static void PressKey(TextBox box, Key key) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = box });

    private sealed class Sink : IFormFieldEditSink
    {
        internal bool Allow = true;
        internal int Stored;
        public bool AdmitEdit() => Allow;
        public void EditStored(PdfField field, string? newValue, string? oldValue) => Stored++;
        public void EditRejected(string fieldName, string message) { }
    }
}
