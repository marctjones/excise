using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// What a typewriter box looks like when you are not editing it (#1648).
///
/// <para>Reported live: "When I click off of a text box I just made the box
/// outline should not be visible again. I should be able to click on the
/// box/text again and edit it, but when I am not in the box editing it the text
/// should just be displayed as it will show up in the saved document."</para>
///
/// <para>The chrome — outline, tinted fill, drag handle, delete button, resize
/// grip — was keyed to the MODE, so every pending box wore it the whole time
/// typewriter mode was on, and there was no way to see the document as it would
/// save without leaving the mode. It is now keyed to the one box being edited.
/// </para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class TypewriterBoxChromeTests
{
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OnlyTheBoxBeingEdited_WearsItsChrome()
    {
        var (vm, window, pdf) = await OpenWithTwoBoxesAsync();
        try
        {
            var boxes = TypewriterEditors(window);
            boxes.Should().HaveCount(2, "two pending boxes were placed");

            // The second box was placed last, so it is the one being edited.
            var editing = boxes[1];
            var idle = boxes[0];

            Chrome(editing).Should().BeTrue("the box you are typing in shows its outline and handles");
            Chrome(idle).Should().BeFalse(
                "a box you are not editing renders as the document will render it — no outline, "
                + "no fill, no handles");

            // The idle box still holds its text and is still clickable, so it
            // can be edited again.
            TextBoxOf(idle).Text.Should().Be("first");
            idle.IsHitTestVisible.Should().BeTrue("clicking the text must be able to re-enter editing");
            TextBoxOf(idle).IsReadOnly.Should().BeTrue(
                "read-only rather than disabled: a read-only TextBox still takes focus, so the "
                + "click lands the caret where the user pointed");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClickingAnIdleBox_MovesTheChromeToIt()
    {
        var (vm, window, pdf) = await OpenWithTwoBoxesAsync();
        try
        {
            var boxes = TypewriterEditors(window);
            var first = boxes[0];
            var second = boxes[1];
            // Both states asserted BEFORE the click. Without this the test
            // passes on a build where every box wears chrome, because blurring
            // the second one strips its own chrome on the way out — measured,
            // by planting exactly that.
            Chrome(second).Should().BeTrue("the last box placed is the one being edited");
            Chrome(first).Should().BeFalse("and the other one is not");

            TextBoxOf(first).Focus();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            Chrome(first).Should().BeTrue("the box you clicked into is now the one being edited");
            Chrome(second).Should().BeFalse("and the one you left goes back to looking like the document");
            TextBoxOf(first).IsReadOnly.Should().BeFalse("editing means editable");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task PlacingANewBox_DiscardsOneNobodyTypedIn()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.TypewriterTextOperations.Should().HaveCount(1);

            // Place a second box without typing in the first.
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 520, 300, 560), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.TypewriterTextOperations.Should().HaveCount(1,
                "an empty box the user moved on from is a click they backed out of; keeping it "
                + "leaves an invisible artefact that only reappears next time they enter the mode");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task LeavingTypewriterMode_DiscardsEmptyBoxes_AndKeepsTypedOnes()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var typed = vm.TypewriterTextOperations.Single();
            vm.OnTypewriterTextEdited(typed.Id, "kept", 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.OnTypewriterTextCreated(new PdfRectangle(72, 520, 300, 560), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.TypewriterTextOperations.Should().HaveCount(2, "one typed, one empty");

            vm.IsTypewriterMode = false;
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.TypewriterTextOperations.Select(o => o.Text).Should().Equal(["kept"],
                "leaving the mode drops what nobody typed in and keeps what they did");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ReEnteringTypewriterMode_DressesNothingUntilABoxIsPicked()
    {
        // The case the per-box rule exists for that focus tracking alone does
        // not cover: a redraw where NOTHING takes focus. Under the old
        // mode-keyed chrome every box was dressed again the moment the mode
        // came back, so the page stopped looking like the document without the
        // user asking to edit anything.
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.OnTypewriterTextEdited(vm.TypewriterTextOperations.Single().Id, "typed", 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.IsTypewriterMode = false;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsTypewriterMode = true;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            window.UpdateLayout();

            var box = TypewriterEditors(window).Single();
            Chrome(box).Should().BeFalse(
                "re-entering the mode edits nothing until the user picks a box");
            TextBoxOf(box).Text.Should().Be("typed", "and the text is still there to be picked");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this editor is wearing its editing chrome. Read off the drag
    /// handle, which is the affordance the user was asking for ("I should be
    /// able to click on the top border... and move the location of the box") —
    /// so a fix that hid the outline but left the handles would fail here.
    /// </summary>
    private static bool Chrome(Control editor)
    {
        var borders = editor.GetVisualDescendants().OfType<Border>().ToList();
        var handleVisible = borders.Any(b => b.Height is 10 && b.IsVisible);
        var buttonVisible = editor.GetVisualDescendants().OfType<Button>().Any(b => b.IsVisible);
        var outlined = borders.Any(b => b.BorderThickness.Top > 0
                                        && b.BorderBrush is SolidColorBrush { Color.A: > 0 });
        return handleVisible && buttonVisible && outlined;
    }

    private static TextBox TextBoxOf(Control editor) =>
        editor.GetVisualDescendants().OfType<TextBox>().Single();

    private static Control[] TypewriterEditors(Window window)
    {
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        var layer = viewer.GetVisualDescendants().OfType<Canvas>()
            .Single(c => c.Name == "TypewriterLayer");
        return layer.Children.OfType<Control>().ToArray();
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenAsync()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"excise-1648-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdf, "Typewriter chrome");

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsTypewriterMode = true;
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        return (vm, window, pdf);
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenWithTwoBoxesAsync()
    {
        var opened = await OpenAsync();
        opened.Vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        var first = opened.Vm.TypewriterTextOperations.Single();
        opened.Vm.OnTypewriterTextEdited(first.Id, "first", 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        opened.Vm.OnTypewriterTextCreated(new PdfRectangle(72, 520, 300, 560), 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        opened.Vm.OnTypewriterTextEdited(opened.Vm.TypewriterTextOperations.Last().Id, "second", 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        opened.Window.UpdateLayout();
        return opened;
    }

    private static void Close(Window window, string pdf)
    {
        window.Close();
        TestPdfGenerator.CleanupTestFile(pdf);
    }
}
