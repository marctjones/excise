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

    private static System.Collections.Generic.List<T> ContinuousFieldInputs<T>(PdfViewerControl viewer) where T : Control =>
        viewer.GetVisualDescendants().OfType<T>()
            .Where(t => t.Classes.Contains("continuous-form-field") && t.IsEffectivelyVisible)
            .ToList();

    internal static Task<T> WaitForFieldOnPageAsync<T>(
        MainWindow window, PdfViewerControl viewer, string fieldName) where T : Control =>
        WaitForFieldInputAsync<T>(window, viewer, fieldName,
            t => ToolTip.GetTip(t) is string tip && tip.Contains(fieldName, StringComparison.Ordinal));

    private static async Task<T> WaitForFieldInputAsync<T>(
        MainWindow window, PdfViewerControl viewer, string what, Func<T, bool> match) where T : Control
    {
        T? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && found == null)
        {
            window.UpdateLayout();
            found = ContinuousFieldInputs<T>(viewer).FirstOrDefault(t => match(t) && t.Bounds.Height > 0);
            if (found == null) await Task.Delay(100);
        }
        found.Should().NotBeNull(
            $"the continuous view must show a fillable input for '{what}' (it showed none " +
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
            viewer.FormFieldEdited += (_, e) => edits.Add((e.Field.FullName, e.NewValue, e.PageNumber));

            // A field on page 1, then one on page 2: several pages are fillable at once, and
            // the edit event must name the field's own page, not the scroll anchor.
            foreach (var (name, typed, page) in new[] { ("name1", "Alpha1", 1), ("name2", "Bravo2", 2) })
            {
                vm.CurrentPageIndex = page - 1;
                var box = await WaitForFieldOnPageAsync<TextBox>(window, viewer, name);

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
            await WaitForFieldOnPageAsync<TextBox>(window, viewer, $"name{pages}");

            ContinuousFieldInputs<TextBox>(viewer).Count.Should().BeLessThan(pages,
                "inputs belong to realized pages only, so a long form does not keep a text " +
                "box for every page it was ever scrolled past");
            ContinuousFieldInputs<TextBox>(viewer)
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

            var box = await WaitForFieldOnPageAsync<TextBox>(window, viewer, "name1");
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
            var box2 = await WaitForFieldOnPageAsync<TextBox>(window, viewer, "name1");
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

    /// <summary>
    /// Three fields a name cannot tell apart (#1865): one with no /T anywhere (the empty full
    /// name, #1864) and two nameless kids of the field "Pair", which both take its name
    /// (§12.7.3.2). Each widget carries its own /TU, which is the key qpdf reports them by.
    /// </summary>
    private static string WriteFieldsANameCannotTellApartPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R /Annots [5 0 R 9 0 R 10 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /TU (nameless) /V (Nobody) /Rect [72 700 300 720] /P 3 0 R >>",
            "<< /FT /Tx /T (Pair) /Kids [7 0 R 8 0 R] >>",
            "<< /Parent 6 0 R /V (One) /Kids [9 0 R] >>",
            "<< /Parent 6 0 R /V (Two) /Kids [10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /TU (first) /Rect [72 600 300 620] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /TU (second) /Rect [72 500 300 520] /P 3 0 R >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(Path.GetTempPath(), $"excise-contform-names-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    /// <summary>
    /// qpdf's own reading of the saved form: each widget's value keyed by its /TU, and
    /// /NeedAppearances. Not excise's parser, which would read its own write back.
    /// </summary>
    internal static (System.Collections.Generic.Dictionary<string, string?> ValueByTooltip, bool NeedAppearances)
        QpdfFormFields(string pdf)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("qpdf", $"--json=2 --json-key=acroform \"{pdf}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        // #1068/#1516: drain both pipes concurrently and bound the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qpdf did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();

        using var json = System.Text.Json.JsonDocument.Parse(stdout.GetAwaiter().GetResult());
        var acroform = json.RootElement.GetProperty("acroform");
        var values = new System.Collections.Generic.Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in acroform.GetProperty("fields").EnumerateArray())
        {
            // qpdf writes a text string as "u:<text>".
            var value = field.GetProperty("value").GetString();
            values[field.GetProperty("alternativename").GetString()!] =
                value != null && value.StartsWith("u:", StringComparison.Ordinal) ? value[2..] : value;
        }
        return (values, acroform.GetProperty("needappearances").GetBoolean());
    }

    private static async Task FillAsync(MainWindow window, PdfViewerControl viewer, string shown, string typed)
    {
        var box = await WaitForFieldInputAsync<TextBox>(window, viewer, shown, t => t.Text == shown);
        var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            box.SelectAll();
            window.KeyTextInput(typed);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        });
        await Task.Delay(100);
    }

    /// <summary>
    /// #1865: the edit reached the document by FULL NAME. The nameless field's edit found no
    /// field, and an edit to the second "Pair" kid was written into the first one as well.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task FieldsANameCannotTellApart_EachKeepTheirOwnValue_InTheSavedFile()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var path = WriteFieldsANameCannotTellApartPdf();
        var saved = Path.Combine(Path.GetTempPath(), $"excise-contform-names-out-{Guid.NewGuid():N}.pdf");
        var window = new MainWindow { Width = 1280, Height = 900 };
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            window.DataContext = vm;
            window.Show();
            await Task.Delay(200);
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            await FillAsync(window, viewer, "Nobody", "Zulu1");
            await FillAsync(window, viewer, "Two", "Yankee2");
            await vm.SaveFileAsAsync(saved);

            var (values, needAppearances) = QpdfFormFields(saved);
            values.Should().Equal(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["nameless"] = "Zulu1",
                ["first"] = "One",
                ["second"] = "Yankee2",
            }, "each edit belongs to the field the user typed into, and no other");
            needAppearances.Should().BeTrue(
                "a field read from a file is not redrawn; the saved form asks the reader to redraw it");
        }
        finally
        {
            window.Close();
            try { File.Delete(path); } catch { }
            try { File.Delete(saved); } catch { }
        }
    }

    /// <summary>
    /// #1865: Undo put the old value back by FULL NAME, so undoing the nameless field did
    /// nothing and undoing the second "Pair" kid overwrote the first.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task FieldsANameCannotTellApart_UndoPutsBackEachOwnValue_InTheSavedFile()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var path = WriteFieldsANameCannotTellApartPdf();
        var saved = Path.Combine(Path.GetTempPath(), $"excise-contform-names-undo-{Guid.NewGuid():N}.pdf");
        var window = new MainWindow { Width = 1280, Height = 900 };
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            window.DataContext = vm;
            window.Show();
            await Task.Delay(200);
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            await FillAsync(window, viewer, "Nobody", "Zulu1");
            await FillAsync(window, viewer, "Two", "Yankee2");
            await vm.UndoCommand.Execute();
            await vm.UndoCommand.Execute();
            await vm.SaveFileAsAsync(saved);

            QpdfFormFields(saved).ValueByTooltip.Should().Equal(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["nameless"] = "Nobody",
                ["first"] = "One",
                ["second"] = "Two",
            }, "undoing both edits must put back what each field held, and touch no other field");
        }
        finally
        {
            window.Close();
            try { File.Delete(path); } catch { }
            try { File.Delete(saved); } catch { }
        }
    }
}
