using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// AcroForm fill in the CONTINUOUS view, the app's default (#1807).
///
/// Every earlier form test read the single-page <c>FormFieldsLayer</c> canvas, which the
/// ViewModel fills whether or not that canvas is on screen, and none set a view mode. So
/// they passed for as long as the default view showed a form as a plain page and took no
/// input at all. These tests read the VISIBLE continuous tree, in the view a new user gets,
/// and drive it with real pointer and keyboard events. The save is checked with mutool, which
/// is not excise, because excise reading back its own write cannot catch a value that was
/// never stored.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousFormFillTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousFormFillTests(ITestOutputHelper o) { _out = o; }

    private static string WriteFormPdf(int pages)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-contform-{Guid.NewGuid():N}.pdf");
        using var doc = PdfDocument.CreateNew();
        for (var p = 1; p <= pages; p++)
        {
            doc.Pages.AddBlank();
            doc.AddTextField(p, new PdfRectangle(72, 600, 300, 624), $"name{p}");
        }
        doc.Save(path);
        return path;
    }

    private static System.Collections.Generic.List<TextBox> ContinuousFieldInputs(PdfViewerControl viewer) =>
        viewer.GetVisualDescendants().OfType<TextBox>()
            .Where(t => t.Classes.Contains("continuous-form-field") && t.IsEffectivelyVisible)
            .ToList();

    private static async Task<TextBox> WaitForFieldOnPageAsync(
        MainWindow window, PdfViewerControl viewer, string fieldName)
    {
        TextBox? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && found == null)
        {
            window.UpdateLayout();
            found = ContinuousFieldInputs(viewer).FirstOrDefault(t =>
                ToolTip.GetTip(t) is string tip && tip.Contains(fieldName, StringComparison.Ordinal)
                && t.Bounds.Height > 0);
            if (found == null) await Task.Delay(100);
        }
        found.Should().NotBeNull(
            $"the continuous view must show a fillable input for '{fieldName}' (it showed none " +
            "before #1807: a form opened in the default view looked like a plain page)");
        return found!;
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task TypingIntoAFieldInTheDefaultView_IsStored_AndSurvivesSave()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var path = WriteFormPdf(pages: 2);
        var saved = Path.Combine(Path.GetTempPath(), $"excise-contform-out-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(200);

            await vm.LoadDocumentAsync(path);
            // The DEFAULT view: no explicit ViewMode assignment, so this test cannot
            // quietly run in single-page view, which is what hid #1807.
            vm.ViewMode.Should().Be(PdfViewMode.Continuous, "continuous scroll is the default view");

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            var edits = new System.Collections.Generic.List<(string Name, string? Value, int Page)>();
            viewer.FormFieldEdited += (_, e) => edits.Add((e.FieldName, e.NewValue, e.PageNumber));

            // A field on page 1, then one on page 2: several pages are fillable at once, and
            // the edit event must name the field's own page, not the scroll anchor.
            foreach (var (name, typed, page) in new[] { ("name1", "Alpha1", 1), ("name2", "Bravo2", 2) })
            {
                vm.CurrentPageIndex = page - 1;
                var box = await WaitForFieldOnPageAsync(window, viewer, name);

                var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window);
                centre.Should().NotBeNull();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window.MouseDown(centre!.Value, MouseButton.Left);
                    window.MouseUp(centre.Value, MouseButton.Left);
                });
                box.IsFocused.Should().BeTrue(
                    "a real click on a continuous-view field must give it focus; the viewer's " +
                    "root pointer handler must not start a selection drag over it");

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window.KeyTextInput(typed);
                    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                });
                await Task.Delay(100);

                vm.PdfCoreDocument!.GetAcroForm()!.FindField(name)!.Value.Should().Be(typed);
                edits.Should().Contain(e => e.Name == name && e.Value == typed && e.Page == page);
            }

            vm.FileState.HasUnsavedChanges.Should().BeTrue("filling a field must dirty the document");
            await vm.SaveFileAsAsync(saved);

            using (var reopened = PdfDocument.Open(saved))
            {
                reopened.GetAcroForm()!.FindField("name1")!.Value.Should().Be("Alpha1");
                reopened.GetAcroForm()!.FindField("name2")!.Value.Should().Be("Bravo2");
            }

            // The independent read: mutool draws each field's appearance as page text.
            MutoolTextExtractor.ExtractPage(saved, 1).Should().Contain("Alpha1");
            MutoolTextExtractor.ExtractPage(saved, 2).Should().Contain("Bravo2");
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(saved); } catch { }
        }
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task OnlyRealizedPages_HoldFieldInputs()
    {
        const int pages = 40;
        var path = WriteFormPdf(pages);
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(200);
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            vm.CurrentPageIndex = pages - 1;
            await WaitForFieldOnPageAsync(window, viewer, $"name{pages}");

            ContinuousFieldInputs(viewer).Count.Should().BeLessThan(pages,
                "inputs belong to realized pages only, so a long form does not keep a text " +
                "box for every page it was ever scrolled past");
            ContinuousFieldInputs(viewer)
                .Select(t => ToolTip.GetTip(t) as string ?? string.Empty)
                .Should().NotContain(tip => tip.EndsWith("name1", StringComparison.Ordinal),
                    "page 1 scrolled out of the realized window and must have released its input");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
    /// <summary>
    /// Filling a field is the most common edit there is; Cmd+Z after a wrong one must
    /// work (#1660). Undo is judged by what gets SAVED, read back with mutool.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task FillingAField_CanBeUndoneAndRedone_AndTheSavedFileFollows()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var path = WriteFormPdf(pages: 1);
        var undone = Path.Combine(Path.GetTempPath(), $"excise-contform-undo-{Guid.NewGuid():N}.pdf");
        var redone = Path.Combine(Path.GetTempPath(), $"excise-contform-redo-{Guid.NewGuid():N}.pdf");
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(200);
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            var box = await WaitForFieldOnPageAsync(window, viewer, "name1");
            var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                window.KeyTextInput("Charlie3");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            });
            await Task.Delay(100);

            vm.UndoMenuHeader.Should().Be("_Undo Edit field 'name1'");
            await vm.UndoCommand.Execute();
            FieldValue(vm, "name1").Should().BeNullOrEmpty("Undo puts back the value the field held before");
            await vm.SaveFileAsAsync(undone);
            MutoolTextExtractor.ExtractPage(undone, 1).Should().NotContain("Charlie3",
                "the undone value must not reach the saved file");

            // Save cleared the history, so redo is exercised on a fresh edit.
            var box2 = await WaitForFieldOnPageAsync(window, viewer, "name1");
            var centre2 = box2.TranslatePoint(new Point(box2.Bounds.Width / 2, box2.Bounds.Height / 2), window)!.Value;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.MouseDown(centre2, MouseButton.Left);
                window.MouseUp(centre2, MouseButton.Left);
                window.KeyTextInput("Delta4");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            });
            await Task.Delay(100);
            await vm.UndoCommand.Execute();
            await vm.RedoCommand.Execute();
            FieldValue(vm, "name1").Should().Be("Delta4");
            await vm.SaveFileAsAsync(redone);
            MutoolTextExtractor.ExtractPage(redone, 1).Should().Contain("Delta4");
        }
        finally
        {
            foreach (var f in new[] { path, undone, redone })
                try { File.Delete(f); } catch { }
        }
    }

    private static string? FieldValue(MainWindowViewModel vm, string name) =>
        vm.PdfCoreDocument!.GetAcroForm()!.FindField(name)!.Value;
}
