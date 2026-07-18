using Xunit;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Avalonia;
using Excise.Core.Document;
using System.Threading.Tasks;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for B2 redaction interaction polish:
/// - R keyboard shortcut toggles redaction mode (verify in MainWindow.axaml.cs)
/// - Drag-to-select-region creates pending redaction rectangle
/// - Double-click in redaction mode auto-selects word and adds to pending
/// - Esc cancels in-progress drag
/// - RedactionPreview overlay with Apply button to commit
///
/// Note: Some tests here verify the underlying ViewModel logic;
/// UI tests (drag, double-click, Esc) require [FixedAvaloniaFact] and headless UI runner.
/// </summary>
public class RedactionInteractionTests
{
    [Fact]
    public void ToggleRedactionModeWorks()
    {
        var vm = new MainWindowViewModel();

        vm.IsRedactionMode.Should().BeFalse();

        vm.IsRedactionMode = true;
        vm.IsRedactionMode.Should().BeTrue();

        vm.IsRedactionMode = false;
        vm.IsRedactionMode.Should().BeFalse();
    }

    [Fact]
    public void CurrentRedactionAreaCanBeSet()
    {
        var vm = new MainWindowViewModel();

        var rect = new Rect(10, 20, 100, 50);
        vm.CurrentRedactionArea = rect;

        vm.CurrentRedactionArea.X.Should().Be(10);
        vm.CurrentRedactionArea.Y.Should().Be(20);
        vm.CurrentRedactionArea.Width.Should().Be(100);
        vm.CurrentRedactionArea.Height.Should().Be(50);
        vm.CurrentRedactionPageArea.Should().NotBeNull();
        vm.CurrentRedactionPageArea!.Value.Space.Should().Be(PdfCoordinateSpace.ViewerDips);
        vm.CurrentRedactionPageArea.Value.PageNumber.Should().Be(1);
    }

    [Fact]
    public void PendingRedactionsCollectionExist()
    {
        var vm = new MainWindowViewModel();

        vm.RedactionWorkflow.PendingRedactions.Should().NotBeNull();
        vm.RedactionWorkflow.PendingRedactions.Count.Should().Be(0);
    }

    [Fact]
    public void CanAddPendingRedaction()
    {
        var vm = new MainWindowViewModel();

        var redaction = new Excise.App.Models.PendingRedaction
        {
            PageNumber = 1,
            Area = new Rect(10, 20, 100, 50)
        };

        vm.RedactionWorkflow.PendingRedactions.Add(redaction);

        vm.RedactionWorkflow.PendingRedactions.Should().HaveCount(1);
        vm.RedactionWorkflow.PendingRedactions[0].PageNumber.Should().Be(1);
        vm.RedactionWorkflow.PendingRedactions[0].Area.Width.Should().Be(100);
    }

    [Fact]
    public void CanRemovePendingRedaction()
    {
        var vm = new MainWindowViewModel();

        var redaction = new Excise.App.Models.PendingRedaction
        {
            PageNumber = 1,
            Area = new Rect(10, 20, 100, 50)
        };

        vm.RedactionWorkflow.PendingRedactions.Add(redaction);
        vm.RedactionWorkflow.PendingRedactions.Should().HaveCount(1);

        vm.RedactionWorkflow.PendingRedactions.Remove(redaction);
        vm.RedactionWorkflow.PendingRedactions.Should().HaveCount(0);
    }

    [Fact]
    public void ShowPendingRedactionsPanelDependsOnRedactionMode()
    {
        var vm = new MainWindowViewModel();

        vm.IsRedactionMode = false;
        vm.IsSearchVisible = false;

        // When not in redaction mode, should not show pending redactions panel
        vm.ShowPendingRedactionsPanel.Should().BeFalse();

        vm.IsRedactionMode = true;

        // When in redaction mode and not searching, should show pending redactions panel
        vm.ShowPendingRedactionsPanel.Should().BeTrue();

        vm.IsSearchVisible = true;

        // When in redaction mode but searching, should show search results (search takes priority)
        vm.ShowPendingRedactionsPanel.Should().BeFalse();
    }

    [Fact]
    public void IsRedactionModePropertyNotifiesChanges()
    {
        var vm = new MainWindowViewModel();
        var changeCount = 0;

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.IsRedactionMode))
                changeCount++;
        };

        vm.IsRedactionMode = true;
        changeCount.Should().Be(1);

        vm.IsRedactionMode = false;
        changeCount.Should().Be(2);

        // Setting to same value still raises event (due to RaiseAndSetIfChanged)
        vm.IsRedactionMode = false;
        changeCount.Should().Be(2); // Should not increment
    }
}
