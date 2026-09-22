using Xunit;
using AwesomeAssertions;
using Avalonia;
using Excise.Core.Document;

namespace Excise.App.Tests.Unit;

/// <summary>
/// View-model level redaction interaction state: the tagged coordinate space of
/// the current redaction area and the mode-change notification the toolbar binds
/// to. The gestures themselves (R shortcut, drag, double-click, Esc) are driven
/// by real input in Excise.App.Tests/UI/Redaction*WorkflowTests.
/// </summary>
public class RedactionInteractionTests
{
    [Fact]
    public void CurrentRedactionAreaCanBeSet()
    {
        var vm = MainWindowViewModelTestFactory.Create();

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
    public void IsRedactionModePropertyNotifiesChanges()
    {
        var vm = MainWindowViewModelTestFactory.Create();
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
