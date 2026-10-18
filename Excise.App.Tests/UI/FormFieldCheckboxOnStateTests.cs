using System.Linq;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2051: checking a form checkbox stores the widget's own on-state. The overlay
/// stored "Yes" for every checkbox, and a widget whose appearance is named "On"
/// (or "1", or an export value) then drew unchecked with /V set.
/// </summary>
[Collection("AvaloniaTests")]
public class FormFieldCheckboxOnStateTests
{
    [FixedAvaloniaTheory]
    [InlineData("Yes")]
    [InlineData("On")]
    [InlineData("1")]
    public void CheckingTheOverlay_StoresTheWidgetsOwnOnState(string onState)
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        var field = doc.AddCheckBox(1, new PdfRectangle(72, 700, 86, 714), "agree");
        RenameOnState(doc, field, onState);
        var sink = new Sink();
        var box = (CheckBox)FormFieldInputFactory.Build(field, 14, 14, 0, sink)!;

        box.IsChecked = true;

        sink.Rejected.Should().BeNull();
        field.Value.Should().Be(onState);
        field.WidgetDictionaries.Single().GetNameOrNull("AS").Should().Be(onState);

        box.IsChecked = false;

        field.Value.Should().Be("Off");
        sink.Stored.Should().Be(2);
    }

    private static void RenameOnState(PdfDocument doc, PdfField field, string onState)
    {
        if (onState == "Yes") return;
        var widget = field.WidgetDictionaries.Single();
        var ap = (PdfDictionary)doc.Resolve(widget.GetOptional("AP")!);
        foreach (var key in new[] { "N", "D" })
        {
            if (ap.GetOptional(key) is not { } obj || doc.Resolve(obj) is not PdfDictionary states) continue;
            if (states.GetOptional("Yes") is not { } on) continue;
            states.Remove("Yes");
            states.Set(onState, on);
        }
        field.ButtonExportValues.Should().Equal(onState);
    }

    private sealed class Sink : IFormFieldEditSink
    {
        internal int Stored;
        internal string? Rejected;
        public bool AdmitEdit() => true;
        public void EditStored(PdfField field, string? newValue, string? oldValue) => Stored++;
        public void EditRejected(string fieldName, string message) => Rejected = message;
    }
}
