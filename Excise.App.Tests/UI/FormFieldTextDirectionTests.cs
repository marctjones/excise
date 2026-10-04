using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
