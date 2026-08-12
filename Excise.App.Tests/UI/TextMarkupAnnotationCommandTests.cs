using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Avalonia.Headless.XUnit;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #912 — Underline, StrikeOut and Squiggly reached from the GUI.
///
/// Core could author 15 annotation subtypes; the app exposed two. These three
/// are the cheap ones: they reuse the SAME text-selection gesture Highlight
/// already used, so no new input handling was involved — only the wiring that
/// had never been done.
///
/// WHAT THESE ASSERT, AND WHY IT IS NOT THE COMMAND EXISTING
///
/// `CommandBindingSweepTests` already proves every command resolves, and the
/// click sweep proves invoking it does not crash. Neither can tell whether
/// clicking "Add Underline" produces an UNDERLINE — both would pass just as
/// happily if all three commands were wired to the Highlight handler, which is
/// the obvious way to get this wrong when copying an existing path.
///
/// So each test executes the real command and asserts the SUBTYPE on the saved
/// document, and a fourth asserts the three produce three DIFFERENT subtypes —
/// which a copy-paste error could not satisfy.
/// </summary>
[Collection("AvaloniaTests")]
public class TextMarkupAnnotationCommandTests
{
    [FixedAvaloniaFact]
    public async Task UnderlineCommand_WithSelection_ProducesAnUnderlineAnnotation() =>
        await AssertMarkup(vm => vm.AddUnderlineAnnotationFromSelectionCommand,
            PdfAnnotationSubtype.Underline);

    [FixedAvaloniaFact]
    public async Task StrikeOutCommand_WithSelection_ProducesAStrikeOutAnnotation() =>
        await AssertMarkup(vm => vm.AddStrikeOutAnnotationFromSelectionCommand,
            PdfAnnotationSubtype.StrikeOut);

    [FixedAvaloniaFact]
    public async Task SquigglyCommand_WithSelection_ProducesASquigglyAnnotation() =>
        await AssertMarkup(vm => vm.AddSquigglyAnnotationFromSelectionCommand,
            PdfAnnotationSubtype.Squiggly);

    /// <summary>
    /// The guard against the copy-paste failure. Three commands must produce
    /// three DISTINCT subtypes — wiring them all to the same handler would pass
    /// every test above that shares a subtype, and this one catches it.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task TheThreeCommands_ProduceThreeDistinctSubtypes()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Clause under review");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            foreach (var cmd in new[]
            {
                vm.AddUnderlineAnnotationFromSelectionCommand,
                vm.AddStrikeOutAnnotationFromSelectionCommand,
                vm.AddSquigglyAnnotationFromSelectionCommand,
            })
            {
                SelectSomeText(vm);
                await cmd.Execute();
            }

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            var subtypes = saved.GetPage(1).GetAnnotations()
                .Select(a => a.Subtype).Distinct().ToList();

            subtypes.Should().Contain(PdfAnnotationSubtype.Underline);
            subtypes.Should().Contain(PdfAnnotationSubtype.StrikeOut);
            subtypes.Should().Contain(PdfAnnotationSubtype.Squiggly);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// THE ON-SCREEN CHECK — and the one the file-based tests above cannot make.
    ///
    /// A document is open twice: the save document and the viewer document
    /// (<see cref="MainWindowViewModel.PdfCoreDocument"/>), two separate
    /// PdfDocument instances. Authoring onto only the save document produces a
    /// correct saved FILE and an unchanged SCREEN — the user clicks "Add
    /// Underline", nothing appears, and the underline shows up only after a
    /// save-and-reopen. Every assertion above would stay green through that,
    /// because they all read the saved file.
    ///
    /// This asserts the viewer document BEFORE any save. It is the difference
    /// between "the annotation is in the file" and "the feature works".
    /// </summary>
    [FixedAvaloniaFact]
    public async Task MarkupIsVisibleWithoutSaving_BecauseTheViewerDocumentIsMirrored()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Clause under review");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            // Guard the premise: if the two instances were ever unified, the
            // mirror becomes a no-op and this test would pass vacuously.
            vm.PdfCoreDocument.Should().NotBeNull();

            SelectSomeText(vm);
            await vm.AddUnderlineAnnotationFromSelectionCommand.Execute();
            SelectSomeText(vm);
            await vm.AddStrikeOutAnnotationFromSelectionCommand.Execute();
            SelectSomeText(vm);
            await vm.AddSquigglyAnnotationFromSelectionCommand.Execute();

            var onScreen = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
                .Select(a => a.Subtype).Distinct().ToList();

            onScreen.Should().Contain(PdfAnnotationSubtype.Underline);
            onScreen.Should().Contain(PdfAnnotationSubtype.StrikeOut);
            onScreen.Should().Contain(PdfAnnotationSubtype.Squiggly,
                "the viewer renders PdfCoreDocument, so markup that is only on the save " +
                "document is invisible until the user saves and reopens the file");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// No selection means no annotation and no crash. The click sweep invokes
    /// these commands with no selection on every run, so this is the behaviour
    /// it relies on. (The path also shows a "select text first" message; that
    /// is not asserted here, only the absence of the annotation.)
    /// </summary>
    [FixedAvaloniaFact]
    public async Task WithNoSelection_AddsNothing()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Clause under review");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            await vm.AddStrikeOutAnnotationFromSelectionCommand.Execute();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.StrikeOut,
                    "with nothing selected there is no rectangle to mark up, so the command " +
                    "must annotate nothing rather than pick an arbitrary region");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// #912's second row — Square and Circle from the DRAG rectangle rather than
    /// a text selection. Same assertions as the markup row, because the same
    /// two ways of getting it wrong apply: wiring both to one handler, and
    /// forgetting the viewer mirror.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task SquareCommand_WithDragArea_ProducesASquareAnnotation() =>
        await AssertShape(vm => vm.AddSquareAnnotationFromDragCommand, PdfAnnotationSubtype.Square);

    [FixedAvaloniaFact]
    public async Task CircleCommand_WithDragArea_ProducesACircleAnnotation() =>
        await AssertShape(vm => vm.AddCircleAnnotationFromDragCommand, PdfAnnotationSubtype.Circle);

    /// <summary>
    /// The copy-paste guard: two commands, two DISTINCT subtypes. Wiring Circle
    /// to AddSquare passes both single-subtype tests above and fails this one.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task SquareAndCircle_ProduceDistinctSubtypes()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Shape target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            await vm.AddSquareAnnotationFromDragCommand.Execute();
            DragABox(vm);
            await vm.AddCircleAnnotationFromDragCommand.Execute();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            var subtypes = saved.GetPage(1).GetAnnotations().Select(a => a.Subtype).Distinct().ToList();

            subtypes.Should().Contain(PdfAnnotationSubtype.Square);
            subtypes.Should().Contain(PdfAnnotationSubtype.Circle,
                "two shape commands must produce two different subtypes — wiring both to the " +
                "same handler is the obvious error when copying an existing path");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Shapes must reach the VIEWER document too, before any save. Same reason
    /// as the markup row: a file-only assertion cannot see an annotation that
    /// is in the saved bytes and invisible on screen.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ShapesAreVisibleWithoutSaving_BecauseTheViewerDocumentIsMirrored()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Shape target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);
            vm.PdfCoreDocument.Should().NotBeNull();

            DragABox(vm);
            await vm.AddSquareAnnotationFromDragCommand.Execute();

            vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Select(a => a.Subtype)
                .Should().Contain(PdfAnnotationSubtype.Square,
                    "the viewer renders PdfCoreDocument, so a shape only on the save document " +
                    "is invisible until the user saves and reopens");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>No drag, no shape — and no crash. The click sweep relies on this.</summary>
    [FixedAvaloniaFact]
    public async Task ShapeWithNoDrag_AddsNothing()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Shape target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            await vm.AddSquareAnnotationFromDragCommand.Execute();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.Square,
                    "with no drag there is no rectangle, so the command must annotate nothing");
        }
        finally { Cleanup(dir); }
    }

    private static async Task AssertShape(
        Func<MainWindowViewModel, ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>> pick,
        PdfAnnotationSubtype expected)
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Shape target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            await pick(vm).Execute();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations().Should().Contain(a => a.Subtype == expected,
                $"executing the real command must put a {expected} annotation on the page");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>The drag gesture shapes reuse — the redaction box rectangle.</summary>
    private static void DragABox(MainWindowViewModel vm)
    {
        vm.CurrentRedactionPageArea = PdfPageRect.ViewerDips(
            1, x: 100, y: 100, width: 200, height: 120,
            renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task AssertMarkup(
        Func<MainWindowViewModel, ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>> pick,
        PdfAnnotationSubtype expected)
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Clause under review");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            SelectSomeText(vm);
            await pick(vm).Execute();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations().Should().Contain(a => a.Subtype == expected,
                $"executing the real command must put a {expected} annotation on the page — " +
                "a command that resolves and does not crash can still be wired to the wrong " +
                "handler, which is the likely error when copying the Highlight path");
        }
        finally { Cleanup(dir); }
    }

    private static void SelectSomeText(MainWindowViewModel vm)
    {
        vm.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(
            1, x: 120, y: 120, width: 180, height: 30,
            renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
        vm.SelectedText = "Clause under review";
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-912-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "in.pdf"), Path.Combine(dir, "out.pdf"), dir);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
