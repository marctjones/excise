using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1671, the user-facing half. Authoring refuses text its font cannot
/// represent instead of writing '?', but a refusal only helps if the user is
/// TOLD: the form-fill path swallowed the exception twice (once in the viewer
/// control, once in the view model's save-document sync) and the file kept the
/// old value with no message at all. Every refusal below must reach
/// <see cref="ToastService"/>, and must say the value was NOT saved.
/// </summary>
[Collection("AvaloniaTests")]
public class UnencodableTextSurfacedTests
{
    private const string Polish = "Łukasz";

    private static string WriteBlankPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-unencodable-{Guid.NewGuid():N}.pdf");
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        File.WriteAllBytes(path, doc.SaveToBytes());
        return path;
    }

    private static (MainWindowViewModel Vm, List<ToastService.ToastEventArgs> Toasts) CreateViewModel()
    {
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, e) => toasts.Add(e);
        return (MainWindowViewModelTestFactory.Create(toastService: toastService), toasts);
    }

    private static string AuthoredFieldName(MainWindowViewModel vm)
        => vm.PdfCoreDocument!.GetAcroForm()!.Fields.Single().FullName;

    // ---- form fill ------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task TypingUnencodableText_IntoAnAuthoredField_ToldToTheUser_AndTheStoredValueKept()
    {
        var path = WriteBlankPdf();
        try
        {
            var (vm, toasts) = CreateViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);
            vm.OnFormFieldRectDrawn(new PdfRectangle(72, 600, 300, 620), 1);
            var editsBefore = vm.FileState.FormFieldEditsCount;

            var textBox = await FindFieldTextBoxAsync(window);
            textBox.Text = Polish;
            CommitWithEnter(textBox);
            await Task.Delay(50);

            var refusal = toasts.Should().ContainSingle(t => t.Severity == ToastService.ToastSeverity.Error).Subject;
            refusal.Message.Should().Contain("NOT saved");
            refusal.Details.Should().Contain("Ł").And.Contain("U+0141");
            vm.PdfCoreDocument!.GetAcroForm()!.FindField(AuthoredFieldName(vm))!.Value
                .Should().NotBe(Polish, "the field must not hold text it could not draw");
            textBox.Text.Should().NotBe(Polish, "the input must not keep showing a value that was not stored");
            vm.FileState.FormFieldEditsCount.Should().Be(editsBefore, "a refused edit is not an edit");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task TypingLatin1AccentedText_IntoAnAuthoredField_IsStored_WithoutAnyToast()
    {
        var path = WriteBlankPdf();
        try
        {
            var (vm, toasts) = CreateViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await Task.Delay(100);
            await vm.LoadDocumentAsync(path);
            vm.OnFormFieldRectDrawn(new PdfRectangle(72, 600, 300, 620), 1);

            var textBox = await FindFieldTextBoxAsync(window);
            textBox.Text = "José Müller";
            CommitWithEnter(textBox);
            await Task.Delay(50);

            toasts.Should().BeEmpty();
            vm.PdfCoreDocument!.GetAcroForm()!.FindField(AuthoredFieldName(vm))!.Value.Should().Be("José Müller");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact]
    public async Task SavingDocumentSync_OfUnencodableFieldValue_IsToldToTheUser()
    {
        // The second swallow: the sync into the save document. Driven directly
        // (no viewer) so it holds whichever caller reaches it.
        var path = WriteBlankPdf();
        try
        {
            var (vm, toasts) = CreateViewModel();
            await vm.LoadDocumentAsync(path);
            vm.OnFormFieldRectDrawn(new PdfRectangle(72, 600, 300, 620), 1);
            var name = AuthoredFieldName(vm);

            vm.OnFormFieldEdited(name, Polish);

            toasts.Should().ContainSingle(t => t.Severity == ToastService.ToastSeverity.Error)
                .Which.Message.Should().Contain("NOT saved");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ---- Bates ---------------------------------------------------------

    [FixedAvaloniaFact]
    public async Task BatesNumbering_WithAPrefixTheFontCannotDraw_IsRefusedAndToldToTheUser_BeforeAnyPageIsStamped()
    {
        var path = WriteBlankPdf();
        try
        {
            var (vm, toasts) = CreateViewModel();
            await vm.LoadDocumentAsync(path);
            var pagesEditedBefore = vm.FileState.PageEditsCount;

            vm.ApplyBatesNumbering(new BatesOptions { Prefix = "ŁÓDŹ-", NumberOfDigits = 6 });

            var refusal = toasts.Should().ContainSingle(t => t.Severity == ToastService.ToastSeverity.Error).Subject;
            refusal.Message.Should().Contain("Could not apply Bates numbers");
            refusal.Details.Should().Contain("U+0141").And.Contain("Bates prefix");
            vm.FileState.PageEditsCount.Should().Be(pagesEditedBefore);
            vm.PdfCoreDocument!.GetPage(1).GetContentStreamBytes().Should().BeEmpty("no page may be stamped when the prefix cannot be drawn");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ---- typewriter ----------------------------------------------------

    [FixedAvaloniaFact]
    public async Task TypewriterText_TheFontCannotDraw_WarnsWhenTyped_OncePerBox()
    {
        var path = WriteBlankPdf();
        try
        {
            var (vm, toasts) = CreateViewModel();
            await vm.LoadDocumentAsync(path);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 600, 300, 640), 1);
            var id = vm.TypewriterTextOperations.Single().Id;

            vm.OnTypewriterTextEdited(id, "Ł", 1);
            vm.OnTypewriterTextEdited(id, "Łu", 1);
            vm.OnTypewriterTextEdited(id, "Łuk", 1);

            var warning = toasts.Should().ContainSingle(t => t.Severity == ToastService.ToastSeverity.Warning,
                "one warning per stretch of unencodable text, not one per keystroke").Subject;
            warning.Details.Should().Contain("U+0141").And.Contain("refused");

            vm.OnTypewriterTextEdited(id, "José", 1);
            toasts.Should().HaveCount(1, "text the font can draw is not warned about");

            vm.OnTypewriterTextEdited(id, "Ł", 1);
            toasts.Where(t => t.Severity == ToastService.ToastSeverity.Warning).Should().HaveCount(2,
                "a box fixed and then broken again warns again");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ---- helpers -------------------------------------------------------

    private static void CommitWithEnter(TextBox textBox)
        => textBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Bubble,
            Key = Key.Enter,
        });

    private static async Task<TextBox> FindFieldTextBoxAsync(MainWindow window)
    {
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var formLayer = FindDescendant<Canvas>(viewer, "FormFieldsLayer");
        for (var i = 0; i < 40 && formLayer?.Children.OfType<TextBox>().Any() != true; i++)
        {
            await Task.Delay(50);
            window.UpdateLayout();
        }

        return formLayer!.Children.OfType<TextBox>().Single();
    }

    private static T? FindDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
                if (child is Control c && FindDescendant<T>(c, name) is { } hit)
                    return hit;
        }
        if (root is Decorator d && d.Child is Control dc && FindDescendant<T>(dc, name) is { } dHit)
            return dHit;
        if (root is ContentControl cc && cc.Content is Control ccc && FindDescendant<T>(ccc, name) is { } cHit)
            return cHit;
        return root.FindControl<T>(name);
    }
}
