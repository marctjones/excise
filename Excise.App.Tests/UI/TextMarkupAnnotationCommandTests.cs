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

    /// <summary>
    /// FreeText (#934 row A) — drag plus a text prompt. The overload taking
    /// contents directly is the test seam; the command supplies null and the
    /// dialog prompts, exactly as the sticky-note path does.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task FreeTextCommand_WithDragAndText_ProducesAFreeTextAnnotationCarryingTheText()
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
            await vm.AddFreeTextAnnotationFromDragAsync("Reviewer comment");

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            var annots = saved.GetPage(1).GetAnnotations().ToList();

            annots.Should().Contain(a => a.Subtype == PdfAnnotationSubtype.FreeText,
                "the command must produce a FreeText annotation, not some other subtype");
            annots.Should().Contain(a => (a.Contents ?? "").Contains("Reviewer comment"),
                "a text box whose text is lost is not a text box — subtype alone is not enough (#933)");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// An empty prompt is how a cancelled dialog arrives. A FreeText box with no
    /// text is not a useful annotation, so nothing should be added.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task FreeTextWithEmptyText_AddsNothing()
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
            await vm.AddFreeTextAnnotationFromDragAsync("   ");

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.FreeText,
                    "a cancelled or blank prompt must not leave an empty box on the page");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Stamp (#934 row B). The NAME is the payload — a stamp that lands with the
    /// wrong name, or none, is the wrong stamp, and no subtype assertion notices.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData("Confidential")]
    [InlineData("Draft")]
    [InlineData("Approved")]
    public async Task StampCommand_WithDragAndName_ProducesAStampCarryingThatName(string stampName)
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            await vm.AddStampAnnotationFromDragAsync(stampName);

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            var stamps = saved.GetPage(1).GetAnnotations()
                .Where(a => a.Subtype == PdfAnnotationSubtype.Stamp).ToList();

            stamps.Should().NotBeEmpty($"the command must produce a Stamp annotation for {stampName}");
            // /Name is surfaced as IconName; PdfAnnotation.Name is /NM, the
            // annotation's unique id. Asserting the wrong one is how this test
            // first failed, and it exposed that IconName's own documentation
            // mentioned only sticky-note icons.
            stamps.Should().Contain(a => a.IconName == stampName,
                $"the stamp's /Name must be {stampName} — a stamp with the wrong name is the " +
                "wrong stamp, and a subtype-only assertion cannot tell (#933)");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// All 15 standard names are offered and all 15 work. The menu is generated
    /// from Core's list, so a name the menu offers that Core rejects would throw
    /// at click time — this is the guard against that drifting apart.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task EveryStandardStampName_IsAccepted()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            var names = MainWindowViewModel.StandardStampNames;
            names.Should().HaveCount(15, "ISO 32000-1 Table 181 defines fifteen standard stamps");

            foreach (var name in names)
            {
                DragABox(vm);
                await vm.AddStampAnnotationFromDragAsync(name);
            }

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            var placed = saved.GetPage(1).GetAnnotations()
                .Where(a => a.Subtype == PdfAnnotationSubtype.Stamp)
                .Select(a => a.IconName).ToList();

            foreach (var name in names)
                placed.Should().Contain(name, $"the menu offers {name}, so it must place one");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A name Core does not recognise must be reported, not silently turned into
    /// a nameless stamp. Core throws; the command must catch and surface it.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task StampWithAnUnknownName_AddsNothingAndDoesNotThrow()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            var act = async () => await vm.AddStampAnnotationFromDragAsync("NotARealStampName");
            await act.Should().NotThrowAsync("an invalid name must surface as an error, not a crash");

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.Stamp,
                    "a rejected name must leave no stamp behind rather than a nameless one");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// ImageStamp (#934 row C) — signature placement. The image is the payload,
    /// so this asserts the stamp exists AND that the picture survived: Core
    /// validates the RGB buffer is exactly width*height*3, so a stride or
    /// channel mistake in the decode throws rather than producing a corrupt
    /// stamp.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ImageStampCommand_WithDragAndImage_ProducesAStampCarryingThePicture()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        var png = Path.Combine(dir, "signature.png");
        WriteTestPng(png, 24, 12);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            await vm.AddImageStampAnnotationFromDragAsync(png);

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Stamp,
                    "an image stamp is a Stamp annotation whose appearance is the chosen picture");

            // The image must actually be embedded, not merely referenced by a
            // path that will not exist on anyone else's machine.
            var bytes = File.ReadAllBytes(output);
            bytes.LongLength.Should().BeGreaterThan(new FileInfo(source).Length,
                "the stamp image must be embedded in the saved document");

            // AND the picture must be RIGHT. "A Stamp exists" passed happily
            // with the red and blue channels swapped — a decode bug that
            // produces a correctly-sized buffer and a plausible-looking stamp,
            // which is exactly the failure a subtype assertion cannot see.
            // The fixture colour is deliberately lopsided (R=20, B=160) so a
            // swap is unmistakable.
            var rendered = new Excise.Rendering.SkiaRenderer().RenderPage(
                saved.GetPage(1), new Excise.Rendering.RenderOptions { Dpi = 72 });
            using (rendered)
            {
                var box = StampProbePoint(saved.GetPage(1));
                var px = rendered.GetPixel(box.X, box.Y);
                ((int)px.Blue).Should().BeGreaterThan(px.Red + 40,
                    $"the stamp fixture is blue-dominant (R=20 B=160) but rendered as " +
                    $"R={px.Red} G={px.Green} B={px.Blue} — the decode swapped channels, " +
                    "which no 'a Stamp exists' assertion can detect");
            }
        }
        finally { Cleanup(dir); }
    }

    /// <summary>A cancelled picker adds nothing — the provider returning null.</summary>
    [FixedAvaloniaFact]
    public async Task ImageStampWithCancelledPicker_AddsNothing()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);
            vm.SetImageStampPathProviderForTests(() => Task.FromResult<string?>(null));

            DragABox(vm);
            await vm.AddImageStampAnnotationFromDragAsync();

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.Stamp,
                    "a cancelled picker must leave the page untouched");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A file that is not a decodable image must be reported, not crash and not
    /// produce an empty stamp.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ImageStampWithAnUndecodableFile_AddsNothingAndDoesNotThrow()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Stamp target");
        var notAnImage = Path.Combine(dir, "notes.txt");
        File.WriteAllText(notAnImage, "this is not a picture");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            DragABox(vm);
            var act = async () => await vm.AddImageStampAnnotationFromDragAsync(notAnImage);
            await act.Should().NotThrowAsync("an undecodable file must surface as a message, not a crash");

            await vm.SaveFileAsAsync(output);
            using var saved = PdfDocument.Open(output);
            saved.GetPage(1).GetAnnotations()
                .Should().NotContain(a => a.Subtype == PdfAnnotationSubtype.Stamp,
                    "a file that could not be decoded must leave no stamp behind");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// ONE DOCUMENT, NOT TWO (#917).
    ///
    /// The viewer and the save path must be the SAME object. While they were
    /// two, every mutation had to be mirrored by hand, and forgetting produced
    /// a correct saved file with an unchanged screen — invisible to any test
    /// that saves and reopens, which is how it survived #912 and had to be
    /// re-fixed on every row of #934.
    ///
    /// Asserted again AFTER a mutation, because the old re-sync path replaced
    /// the viewer's document with a freshly reparsed one on every change: a
    /// check that only ran at load time would pass while the two drifted apart
    /// on first use. That reparse is also #922's 1401ms.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ViewerAndSaveDocument_AreTheSameInstance_BeforeAndAfterAMutation()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Clause under review");
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(source);

            vm.SaveDocumentForTests.Should().NotBeNull();
            vm.PdfCoreDocument.Should().BeSameAs(vm.SaveDocumentForTests,
                "opening the file twice is what made every mutation need a hand-written mirror (#917)");

            var beforeMutation = vm.PdfCoreDocument;
            DragABox(vm);
            await vm.AddSquareAnnotationFromDragAsync();

            vm.PdfCoreDocument.Should().BeSameAs(vm.SaveDocumentForTests,
                "a mutation must not split them apart again");
            vm.PdfCoreDocument.Should().BeSameAs(beforeMutation,
                "the per-mutation re-sync used to hand the viewer a NEW reparsed document — " +
                "that serialize-and-reparse round trip is #922's cost and must not come back");

            vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
                .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Square,
                    "and the change must be visible through the viewer without saving");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A point inside the drawn stamp, in rendered pixels at 72 dpi. The drag
    /// box is in viewer DIPs, so this converts through the same page geometry
    /// the renderer uses rather than assuming they coincide.
    /// </summary>
    private static (int X, int Y) StampProbePoint(PdfPage page)
    {
        var annot = page.GetAnnotations().First(a => a.Subtype == PdfAnnotationSubtype.Stamp);
        var r = annot.Rect.Normalize();
        var mb = page.MediaBox.Normalize();
        // PDF y grows upward; the raster's y grows downward.
        var cx = (r.Left + r.Right) / 2 - mb.Left;
        var cy = mb.Top - (r.Bottom + r.Top) / 2;
        return ((int)Math.Round(cx), (int)Math.Round(cy));
    }

    /// <summary>A small solid PNG — enough to exercise decode and embedding.</summary>
    private static void WriteTestPng(string path, int w, int h)
    {
        using var bitmap = new SkiaSharp.SKBitmap(w, h);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            canvas.Clear(new SkiaSharp.SKColor(20, 60, 160));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(path);
        data.SaveTo(fs);
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
