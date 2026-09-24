using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Discard Pending Type-over Edits used to throw unsaved work away with no way back (#1813). It is now
/// on the undo stack like every other typewriter edit.
/// </summary>
[Collection("AvaloniaTests")]
public class TypewriterDiscardUndoTests
{
    [FixedAvaloniaFact]
    public async Task DiscardingPendingTypewriterEdits_CanBeUndone_AndRedone()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise-twdiscard-{System.Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(path, "Original text");
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(path);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            var id = vm.TypewriterTextOperations.Single().Id;
            vm.OnTypewriterTextEdited(id, "KEEPME", 1);

            vm.DiscardPendingTypewriterEdits();
            vm.TypewriterTextOperations.Should().BeEmpty("the discard removed the pending edit");
            vm.UndoMenuHeader.Should().Be("_Undo Discard type-over edits");

            await vm.UndoCommand.Execute();
            vm.TypewriterTextOperations.Should().ContainSingle().Which.Text.Should().Be("KEEPME",
                "undo brings back the discarded edit, text included");

            await vm.RedoCommand.Execute();
            vm.TypewriterTextOperations.Should().BeEmpty();
            window.Close();
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }

    [FixedAvaloniaFact]
    public async Task DiscardWithNothingPending_AddsNoUndoEntry()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise-twdiscard0-{System.Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(path, "Original text");
        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(path);

            vm.DiscardPendingTypewriterEdits();

            vm.CanUndo.Should().BeFalse("nothing was discarded, so there is nothing to undo");
            window.Close();
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }
}
