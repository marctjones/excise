using Avalonia;
using AwesomeAssertions;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Unit tests for RedactionWorkflowManager.
/// Tests the mark-then-apply workflow state management.
///
/// See Issue #27: Add unit tests for RedactionWorkflowManager
/// </summary>
public class RedactionWorkflowManagerTests
{
    private readonly RedactionWorkflowManager _manager;

    public RedactionWorkflowManagerTests()
    {
        _manager = new RedactionWorkflowManager();
    }

    #region MarkArea Tests

    [Fact]
    public void MarkArea_AddsToPendingList()
    {
        // Arrange
        var area = new Rect(100, 100, 200, 50);

        // Act
        _manager.MarkArea(1, area, "Test text");

        // Assert
        _manager.PendingRedactions.Should().HaveCount(1);
        _manager.PendingCount.Should().Be(1);
        _manager.HasPendingRedactions.Should().BeTrue();
    }

    [Fact]
    public void MarkArea_SetsCorrectProperties()
    {
        // Arrange
        var area = new Rect(100, 100, 200, 50);
        var previewText = "Secret data";

        // Act
        _manager.MarkArea(2, area, previewText);

        // Assert
        var pending = _manager.PendingRedactions.First();
        pending.PageNumber.Should().Be(2);
        pending.Area.Should().Be(area);
        pending.PreviewText.Should().Be(previewText);
        pending.RenderDpi.Should().Be(120);
        pending.Id.Should().NotBe(Guid.Empty);
        pending.MarkedTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MarkArea_StoresExplicitRenderDpi()
    {
        var area = new Rect(100, 100, 200, 50);

        _manager.MarkArea(2, area, "Secret data", renderDpi: 120);

        _manager.PendingRedactions.Single().RenderDpi.Should().Be(120);
        _manager.PendingRedactions.Single().PageArea.Space.Should().Be(Excise.Core.Document.PdfCoordinateSpace.ViewerDips);
    }

    [Fact]
    public void MarkArea_WithPageRect_PreservesCoordinateSpace()
    {
        var area = Excise.Core.Document.PdfPageRect.FromContentPoints(
            2,
            new Excise.Core.Document.PdfRectangle(100, 700, 200, 720));

        _manager.MarkArea(area, "Secret data");

        var pending = _manager.PendingRedactions.Single();
        pending.PageNumber.Should().Be(2);
        pending.PageArea.Should().Be(area);
        pending.PageArea.Space.Should().Be(Excise.Core.Document.PdfCoordinateSpace.ContentPoints);
    }

    [Fact]
    public void MarkArea_MultipleAreas_AllAdded()
    {
        // Arrange & Act
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Text 1");
        _manager.MarkArea(1, new Rect(0, 100, 100, 50), "Text 2");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "Text 3");

        // Assert
        _manager.PendingRedactions.Should().HaveCount(3);
        _manager.PendingCount.Should().Be(3);
    }

    [Fact]
    public void MarkArea_RaisesPropertyChanged()
    {
        // Arrange
        var changedProperties = new List<string>();
        _manager.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        // Assert
        changedProperties.Should().Contain("PendingCount");
        changedProperties.Should().Contain("HasPendingRedactions");
        changedProperties.Should().Contain("PendingRedactions");
    }

    #endregion

    #region RemovePending Tests

    [Fact]
    public void RemovePending_ExistingId_ReturnsTrue()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");
        var id = _manager.PendingRedactions.First().Id;

        // Act
        var result = _manager.RemovePending(id);

        // Assert
        result.Should().BeTrue();
        _manager.PendingRedactions.Should().BeEmpty();
        _manager.PendingCount.Should().Be(0);
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void RemovePending_NonExistingId_ReturnsFalse()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");
        var nonExistingId = Guid.NewGuid();

        // Act
        var result = _manager.RemovePending(nonExistingId);

        // Assert
        result.Should().BeFalse();
        _manager.PendingRedactions.Should().HaveCount(1);
    }

    [Fact]
    public void RemovePending_EmptyList_ReturnsFalse()
    {
        // Act
        var result = _manager.RemovePending(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void RemovePending_RaisesPropertyChanged()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");
        var id = _manager.PendingRedactions.First().Id;

        var changedProperties = new List<string>();
        _manager.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _manager.RemovePending(id);

        // Assert
        changedProperties.Should().Contain("PendingCount");
        changedProperties.Should().Contain("HasPendingRedactions");
    }

    #endregion

    #region ClearPending Tests

    [Fact]
    public void ClearPending_RemovesAllPending()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Text 1");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "Text 2");

        // Act
        _manager.ClearPending();

        // Assert
        _manager.PendingRedactions.Should().BeEmpty();
        _manager.PendingCount.Should().Be(0);
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void ClearPending_EmptyList_NoException()
    {
        // Act
        Action act = () => _manager.ClearPending();

        // Assert
        act.Should().NotThrow();
        _manager.PendingCount.Should().Be(0);
    }

    [Fact]
    public void ClearPending_RaisesPropertyChanged()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        var changedProperties = new List<string>();
        _manager.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _manager.ClearPending();

        // Assert
        changedProperties.Should().Contain("PendingCount");
        changedProperties.Should().Contain("HasPendingRedactions");
    }

    #endregion

    #region MoveToApplied Tests

    [Fact]
    public void MoveToApplied_MovesPendingToApplied()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Text 1");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "Text 2");
        var originalIds = _manager.PendingRedactions.Select(p => p.Id).ToList();

        // Act
        _manager.MoveToApplied();

        // Assert
        _manager.PendingRedactions.Should().BeEmpty();
        _manager.PendingCount.Should().Be(0);
        _manager.HasPendingRedactions.Should().BeFalse();

        _manager.AppliedRedactions.Should().HaveCount(2);
        _manager.AppliedCount.Should().Be(2);
        _manager.AppliedRedactions.Select(a => a.Id).Should().BeEquivalentTo(originalIds);
    }

    [Fact]
    public void MoveToApplied_EmptyPending_NoException()
    {
        // Act
        Action act = () => _manager.MoveToApplied();

        // Assert
        act.Should().NotThrow();
        _manager.AppliedCount.Should().Be(0);
    }

    [Fact]
    public void MoveToApplied_RaisesPropertyChanged()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        var changedProperties = new List<string>();
        _manager.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _manager.MoveToApplied();

        // Assert
        changedProperties.Should().Contain("PendingCount");
        changedProperties.Should().Contain("HasPendingRedactions");
        changedProperties.Should().Contain("AppliedCount");
    }

    #endregion

    #region GetPendingForPage Tests

    [Fact]
    public void GetPendingForPage_ReturnsCorrectItems()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Page 1 Text 1");
        _manager.MarkArea(1, new Rect(0, 100, 100, 50), "Page 1 Text 2");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "Page 2 Text");
        _manager.MarkArea(3, new Rect(0, 0, 100, 50), "Page 3 Text");

        // Act
        var page1Items = _manager.GetPendingForPage(1).ToList();
        var page2Items = _manager.GetPendingForPage(2).ToList();
        var page4Items = _manager.GetPendingForPage(4).ToList();

        // Assert
        page1Items.Should().HaveCount(2);
        page1Items.All(p => p.PageNumber == 1).Should().BeTrue();

        page2Items.Should().HaveCount(1);
        page2Items.First().PreviewText.Should().Be("Page 2 Text");

        page4Items.Should().BeEmpty();
    }

    [Fact]
    public void GetPendingForPage_EmptyList_ReturnsEmpty()
    {
        // Act
        var result = _manager.GetPendingForPage(1);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetAppliedForPage Tests

    [Fact]
    public void GetAppliedForPage_ReturnsCorrectItems()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Page 1 Text");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "Page 2 Text");
        _manager.MoveToApplied();

        // Act
        var page1Items = _manager.GetAppliedForPage(1).ToList();
        var page2Items = _manager.GetAppliedForPage(2).ToList();

        // Assert
        page1Items.Should().HaveCount(1);
        page1Items.First().PageNumber.Should().Be(1);

        page2Items.Should().HaveCount(1);
        page2Items.First().PageNumber.Should().Be(2);
    }

    [Fact]
    public void GetAppliedForPage_EmptyList_ReturnsEmpty()
    {
        // Act
        var result = _manager.GetAppliedForPage(1);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Pending");
        _manager.MarkArea(2, new Rect(0, 0, 100, 50), "To be applied");
        _manager.MoveToApplied();
        _manager.MarkArea(3, new Rect(0, 0, 100, 50), "New pending");

        // Pre-condition check
        _manager.AppliedCount.Should().Be(2);
        _manager.PendingCount.Should().Be(1);

        // Act
        _manager.Reset();

        // Assert
        _manager.PendingRedactions.Should().BeEmpty();
        _manager.AppliedRedactions.Should().BeEmpty();
        _manager.PendingCount.Should().Be(0);
        _manager.AppliedCount.Should().Be(0);
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void Reset_EmptyState_NoException()
    {
        // Act
        Action act = () => _manager.Reset();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Reset_RaisesPropertyChanged()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        var changedProperties = new List<string>();
        _manager.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _manager.Reset();

        // Assert
        changedProperties.Should().Contain("PendingCount");
        changedProperties.Should().Contain("HasPendingRedactions");
        changedProperties.Should().Contain("AppliedCount");
    }

    #endregion

    #region HasPendingRedactions Tests

    [Fact]
    public void HasPendingRedactions_InitiallyFalse()
    {
        // Assert
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void HasPendingRedactions_TrueAfterMark()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        // Assert
        _manager.HasPendingRedactions.Should().BeTrue();
    }

    [Fact]
    public void HasPendingRedactions_FalseAfterRemove()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");
        var id = _manager.PendingRedactions.First().Id;

        // Act
        _manager.RemovePending(id);

        // Assert
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void HasPendingRedactions_FalseAfterMoveToApplied()
    {
        // Arrange
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        // Act
        _manager.MoveToApplied();

        // Assert
        _manager.HasPendingRedactions.Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void MarkArea_ZeroSizeArea_StillAdded()
    {
        // Arrange
        var zeroArea = new Rect(100, 100, 0, 0);

        // Act
        _manager.MarkArea(1, zeroArea, "Zero size");

        // Assert
        _manager.PendingCount.Should().Be(1);
    }

    [Fact]
    public void MarkArea_NegativeCoordinates_StillAdded()
    {
        // Arrange
        var negativeArea = new Rect(-100, -50, 200, 100);

        // Act
        _manager.MarkArea(1, negativeArea, "Negative coords");

        // Assert
        _manager.PendingCount.Should().Be(1);
    }

    [Fact]
    public void MarkArea_EmptyPreviewText_StillAdded()
    {
        // Act
        _manager.MarkArea(1, new Rect(0, 0, 100, 50), "");

        // Assert
        _manager.PendingCount.Should().Be(1);
        _manager.PendingRedactions.First().PreviewText.Should().BeEmpty();
    }

    [Fact]
    public void MarkArea_PageZero_Throws()
    {
        var act = () => _manager.MarkArea(0, new Rect(0, 0, 100, 50), "Page 0");

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Page number is 1-based*");
    }

    #endregion
}
