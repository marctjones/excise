using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1877: a save disposes the document it wrote and reopens the file. Plain Save
/// left the view model and the viewer on the disposed instance while the service
/// held the new one (#917's split again), so an edit made after it went to an
/// instance the next save never wrote. Save As reopened the file a second time.
/// </summary>
[Collection("AvaloniaTests")]
public class EverySaveKeepsOneDocumentTests
{
    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task EverySave_LeavesTheAppOnTheInstanceItReopened_SoAnEditAfterItReachesTheNextSave()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var dir = Path.Combine(Path.GetTempPath(), $"excise-one-instance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "in.pdf"), output = Path.Combine(dir, "out.pdf"), other = Path.Combine(dir, "other.pdf");
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddTextField(1, new PdfRectangle(72, 600, 300, 624), "name", defaultValue: "Original", tooltip: "name");
            doc.Save(source);
        }
        var service = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var reopened = new List<PdfDocument?>();
        service.DocumentReleased += reason =>
        {
            if (reason == DocumentReleaseReason.SaveReload) reopened.Add(service.GetCurrentDocument());
        };
        var vm = MainWindowViewModelTestFactory.Create(documentService: service, thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        try
        {
            window.Show();
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            await vm.LoadDocumentAsync(source);
            await AnnotationPlacementAccuracyTests.WaitForIdleLayout(window);
            vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(output);

            async Task Save(string what, Func<Task> save)
            {
                reopened.Clear();
                await save();
                reopened.Should().ContainSingle($"{what} reads the file it wrote once");
                var saved = reopened[0];
                ReferenceEquals(vm.SaveDocumentForTests, saved).Should().BeTrue($"after {what} the service holds what it reopened");
                ReferenceEquals(vm.PdfCoreDocument, saved).Should().BeTrue(
                    $"after {what} the view model must edit the document the next save writes");
                ReferenceEquals(viewer.Document, saved).Should().BeTrue($"after {what} the viewer must show it");
            }
            void Fill(string value)
            {
                var field = vm.PdfCoreDocument!.GetAcroForm()!.FindField("name")!;
                var old = field.Value;
                field.SetValue(value);
                vm.OnFormFieldEdited(field, value, old);
            }
            string? Saved(string path) => ContinuousFormFillTests.QpdfFormFields(path).ValueByTooltip["name"];

            await Save("a Save of the unchanged original", async () => await vm.SaveFileCommand.Execute());

            Fill("Alpha");
            await Save("a Save of the edited original, which goes through Save As", async () => await vm.SaveFileCommand.Execute());
            Saved(output).Should().Be("Alpha");

            Fill("Bravo");
            await Save("a plain Save", async () => await vm.SaveFileCommand.Execute());
            Saved(output).Should().Be("Bravo", "an edit made after a Save must reach the next one");

            Fill("Charlie");
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 400, 300, 440), 1);
            vm.OnTypewriterTextEdited(vm.TypewriterTextOperations.Single().Id, "Typed over", 1);
            await Save("a plain Save that flattens typewriter text", async () => await vm.SaveFileCommand.Execute());
            Saved(output).Should().Be("Charlie", "an edit made after a plain Save must reach the next one");
            MutoolTextExtractor.ExtractPage(output, 1).Should().Contain("Typed over");

            Fill("Delta");
            await Save("a Save As", () => vm.SaveFileAsAsync(other));
            Saved(other).Should().Be("Delta");
            Fill("Echo");
            await Save("a plain Save after Save As", async () => await vm.SaveFileCommand.Execute());
            Saved(other).Should().Be("Echo");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
