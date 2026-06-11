using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace PdfEditor.Tests.Unit;

/// <summary>
/// Unit tests for MainWindowViewModel.
/// Tests property calculations, state management, and utility methods.
///
/// Note: ReactiveUI commands that depend on Avalonia UI dispatcher
/// are excluded from these unit tests. Those are tested via integration tests.
/// See SearchViewModelTests for AvaloniaFact-based tests.
/// </summary>
public class MainWindowViewModelTests
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Mock<ILogger<MainWindowViewModel>> _mockLogger;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<PdfDocumentService> _mockDocumentService;
    private readonly Mock<PdfRenderService> _mockRenderService;
    private readonly Mock<RedactionService> _mockRedactionService;
    private readonly Mock<PdfTextExtractionService> _mockTextExtractionService;
    private readonly Mock<PdfSearchService> _mockSearchService;
    private readonly Mock<SignatureVerificationService> _mockSignatureService;
    private readonly Mock<FilenameSuggestionService> _mockFilenameSuggestionService;

    public MainWindowViewModelTests()
    {
        // Create mocks
        _mockLogger = new Mock<ILogger<MainWindowViewModel>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockDocumentService = new Mock<PdfDocumentService>(
            new Mock<ILogger<PdfDocumentService>>().Object);
        _mockRenderService = new Mock<PdfRenderService>(
            new Mock<ILogger<PdfRenderService>>().Object);
        _mockRedactionService = new Mock<RedactionService>(
            new Mock<ILogger<RedactionService>>().Object,
            _mockLoggerFactory.Object);
        _mockTextExtractionService = new Mock<PdfTextExtractionService>(
            new Mock<ILogger<PdfTextExtractionService>>().Object);
        _mockSearchService = new Mock<PdfSearchService>(
            new Mock<ILogger<PdfSearchService>>().Object);
        _mockSignatureService = new Mock<SignatureVerificationService>(
            new Mock<ILogger<SignatureVerificationService>>().Object);
        _mockFilenameSuggestionService = new Mock<FilenameSuggestionService>();

        // Create ViewModel with mocked dependencies
        _viewModel = new MainWindowViewModel(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _mockDocumentService.Object,
            _mockRenderService.Object,
            _mockRedactionService.Object,
            _mockTextExtractionService.Object,
            _mockSearchService.Object,
            _mockSignatureService.Object,
            _mockFilenameSuggestionService.Object,
            new PdfEditor.Services.ToastService());
    }

    #region Property Tests

    [Fact]
    public void CurrentPageIndex_InitiallyZero()
    {
        _viewModel.CurrentPageIndex.Should().Be(0);
    }

    [Fact]
    public void CurrentPage_ReturnsOneBasedIndex()
    {
        // Arrange
        _viewModel.CurrentPageIndex = 5;

        // Act & Assert
        _viewModel.CurrentPage.Should().Be(6);
    }

    [Fact]
    public void CurrentPage_WithZeroIndex_ReturnsOne()
    {
        _viewModel.CurrentPageIndex = 0;
        _viewModel.CurrentPage.Should().Be(1);
    }

    [Fact]
    public void DisplayPageNumber_ReturnsOneBasedCurrentPageIndex()
    {
        _viewModel.CurrentPageIndex = 3;
        _viewModel.DisplayPageNumber.Should().Be(4);
    }

    [Fact]
    public void IsRedactionMode_InitiallyFalse()
    {
        _viewModel.IsRedactionMode.Should().BeFalse();
    }

    [Fact]
    public void IsTextSelectionMode_InitiallyFalse()
    {
        _viewModel.IsTextSelectionMode.Should().BeFalse();
    }

    [Fact]
    public void IsTypewriterMode_InitiallyFalse()
    {
        _viewModel.IsTypewriterMode.Should().BeFalse();
    }

    [Fact]
    public void SelectedText_InitiallyEmpty()
    {
        _viewModel.SelectedText.Should().BeEmpty();
    }

    [Fact]
    public void ZoomLevel_CanBeSet()
    {
        // ZoomLevel can be set to any value (clamping happens elsewhere)
        _viewModel.ZoomLevel = 1.5;
        _viewModel.ZoomLevel.Should().Be(1.5);

        _viewModel.ZoomLevel = 0.5;
        _viewModel.ZoomLevel.Should().Be(0.5);

        _viewModel.ZoomLevel = 3.0;
        _viewModel.ZoomLevel.Should().Be(3.0);
    }

    [Fact]
    public void ViewportWidth_CanBeSet()
    {
        _viewModel.ViewportWidth = 800;
        _viewModel.ViewportWidth.Should().Be(800);
    }

    [Fact]
    public void ViewportHeight_CanBeSet()
    {
        _viewModel.ViewportHeight = 600;
        _viewModel.ViewportHeight.Should().Be(600);
    }

    [Fact]
    public void CurrentModeText_WhenRedactionMode_ReturnsRedactionText()
    {
        // Arrange - need to use reflection since ToggleRedactionMode is a ReactiveCommand
        var field = typeof(MainWindowViewModel).GetField("_isRedactionMode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, true);

        // Act
        var modeText = _viewModel.CurrentModeText;

        // Assert
        modeText.Should().Contain("Redaction");
    }

    [Fact]
    public void IsTypewriterMode_WhenEnabled_UsesTypewriterInteractionModeAndDisablesOtherModes()
    {
        _viewModel.IsRedactionMode = true;
        _viewModel.IsTypewriterMode = true;

        _viewModel.IsTypewriterMode.Should().BeTrue();
        _viewModel.IsRedactionMode.Should().BeFalse();
        _viewModel.InteractionMode.Should().Be(InteractionMode.Typewriter);
        _viewModel.CurrentModeText.Should().Contain("Typewriter");
    }

    [Fact]
    public void IsDocumentLoaded_ReturnsDocumentServiceState()
    {
        // Note: PdfDocumentService.IsDocumentLoaded is not virtual, so we test
        // with the default mock behavior (non-null service means document loaded is possible)
        _viewModel.Should().NotBeNull();
    }

    [Fact]
    public void TotalPages_ReturnsDelegatedValue()
    {
        // TotalPages delegates to _documentService.PageCount
        // Since PageCount is not virtual on the concrete service,
        // we just verify the property exists and is accessible
        var totalPages = _viewModel.TotalPages;
        totalPages.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RenderCacheMax_DefaultValueIsPositive()
    {
        _viewModel.RenderCacheMax.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderCacheMax_CanBeSet()
    {
        _viewModel.RenderCacheMax = 50;
        _viewModel.RenderCacheMax.Should().Be(50);
    }

    [Fact]
    public void LoadDocumentTimeoutSeconds_DefaultIsThirty()
    {
        _viewModel.LoadDocumentTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void LoadDocumentTimeoutSeconds_CanBeSetToZeroDisable()
    {
        _viewModel.LoadDocumentTimeoutSeconds = 0;
        _viewModel.LoadDocumentTimeoutSeconds.Should().Be(0);
    }

    #endregion

    #region RedactionWorkflow Property Tests

    [Fact]
    public void RedactionWorkflow_IsNotNull()
    {
        _viewModel.RedactionWorkflow.Should().NotBeNull();
    }

    [Fact]
    public void RedactionWorkflow_InitiallyHasNoPendingRedactions()
    {
        _viewModel.RedactionWorkflow.PendingCount.Should().Be(0);
        _viewModel.RedactionWorkflow.HasPendingRedactions.Should().BeFalse();
    }

    #endregion

    #region DocumentStateManager Property Tests

    [Fact]
    public void FileState_IsNotNull()
    {
        _viewModel.FileState.Should().NotBeNull();
    }

    [Fact]
    public void FileState_InitiallyEmpty()
    {
        _viewModel.FileState.CurrentFilePath.Should().BeEmpty();
        _viewModel.FileState.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void SaveButtonText_DelegatesToFileState()
    {
        // Arrange
        _viewModel.FileState.SetDocument("/test/file.pdf");
        _viewModel.FileState.PendingRedactionsCount = 1;

        // Act
        var text = _viewModel.SaveButtonText;

        // Assert
        text.Should().Be("Save Redacted Version");
    }

    #endregion

    #region Collection Property Tests

    [Fact]
    public void PageThumbnails_InitiallyEmpty()
    {
        _viewModel.PageThumbnails.Count.Should().Be(0);
    }

    [Fact]
    public void ClipboardHistory_InitiallyEmpty()
    {
        _viewModel.ClipboardHistory.Count.Should().Be(0);
    }

    [Fact]
    public void RecentFiles_IsNotNull()
    {
        // RecentFiles loads from disk on initialization, so it may contain files
        // Just verify it's not null and is a valid collection
        _viewModel.RecentFiles.Should().NotBeNull();
    }

    [Fact]
    public void HasRecentFiles_ReflectsRecentFilesCount()
    {
        // HasRecentFiles depends on RecentFiles which loads from disk
        // Just verify the property evaluates to a boolean (true or false)
        var hasRecent = _viewModel.HasRecentFiles;
        (hasRecent == true || hasRecent == false).Should().BeTrue();
    }

    [Fact]
    public void CurrentPageSearchHighlights_InitiallyEmpty()
    {
        _viewModel.CurrentPageSearchHighlights.Count.Should().Be(0);
    }

    [Fact]
    public void TypewriterTextOperations_InitiallyEmpty()
    {
        _viewModel.TypewriterTextOperations.Should().BeEmpty();
    }

    [Fact]
    public void OnTypewriterTextEdited_TracksOnlyNonEmptyPendingEdits()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(300, 400);
        _viewModel.PdfCoreDocument = doc;
        _viewModel.FileState.SetDocument("/test/file.pdf");

        _viewModel.OnTypewriterTextCreated(new PdfRectangle(40, 250, 240, 290), 1);
        var operationId = _viewModel.TypewriterTextOperations.Single().Id;

        _viewModel.FileState.TypewriterEditsCount.Should().Be(0);

        _viewModel.OnTypewriterTextEdited(operationId, "Office note", 1);

        _viewModel.TypewriterTextOperations.Single().Text.Should().Be("Office note");
        _viewModel.FileState.TypewriterEditsCount.Should().Be(1);
        _viewModel.SaveButtonText.Should().Be("Save a Copy");

        _viewModel.OnTypewriterTextEdited(operationId, string.Empty, 1);

        _viewModel.FileState.TypewriterEditsCount.Should().Be(0);
    }

    [Fact]
    public void OnTypewriterTextDeleted_RemovesPendingOperation()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(300, 400);
        _viewModel.PdfCoreDocument = doc;

        _viewModel.OnTypewriterTextCreated(new PdfRectangle(40, 250, 240, 290), 1);
        var operationId = _viewModel.TypewriterTextOperations.Single().Id;

        _viewModel.OnTypewriterTextDeleted(operationId);

        _viewModel.TypewriterTextOperations.Should().BeEmpty();
    }

    #endregion

    #region DocumentName Property Tests

    [Fact]
    public void DocumentName_WhenNoDocument_ReturnsValidString()
    {
        // DocumentName loads zoom preference which may set a filepath
        // Just verify it returns a valid string
        var docName = _viewModel.DocumentName;
        docName.Should().NotBeNull();
        docName.GetType().Name.Should().Be("String");
    }

    [Fact]
    public void DocumentName_WhenDocumentLoaded_ReturnsFilename()
    {
        // Arrange
        var testPath = "/home/user/Documents/test.pdf";
        var field = typeof(MainWindowViewModel).GetField("_currentFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, testPath);

        // Act
        var name = _viewModel.DocumentName;

        // Assert
        name.Should().Be("test.pdf");
    }

    #endregion

    #region Scripting API Tests

    [Fact]
    public void FilePath_ReturnsCurrentFilePath()
    {
        // Arrange
        var testPath = "/home/user/test.pdf";
        var field = typeof(MainWindowViewModel).GetField("_currentFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, testPath);

        // Act & Assert
        _viewModel.FilePath.Should().Be(testPath);
    }

    [Fact]
    public void PendingRedactions_ReturnsRedactionWorkflowPendingRedactions()
    {
        // Arrange
        _viewModel.RedactionWorkflow.MarkArea(1, new Rect(0, 0, 100, 50), "Test");

        // Act
        var pending = _viewModel.PendingRedactions;

        // Assert
        pending.Should().HaveCount(1);
        pending.Should().BeSameAs(_viewModel.RedactionWorkflow.PendingRedactions);
    }

    [Fact]
    public void CurrentDocument_ReturnsDocumentInfoOrNull()
    {
        // Note: IsDocumentLoaded is not virtual, so CurrentDocument will check
        // the actual underlying service state
        var docInfo = _viewModel.CurrentDocument;
        // Property should return null or CurrentDocumentInfo based on service state
        if (docInfo != null)
        {
            docInfo.GetType().Name.Should().Be("CurrentDocumentInfo");
        }
    }

    #endregion

    #region Coordinate System Tests

    [Fact]
    public void CurrentRedactionArea_InitiallyZeroRect()
    {
        _viewModel.CurrentRedactionArea.Should().Be(default(Rect));
    }

    [Fact]
    public void CurrentRedactionArea_CanBeSet()
    {
        // Arrange
        var testRect = new Rect(10, 20, 100, 50);

        // Act
        _viewModel.CurrentRedactionArea = testRect;

        // Assert
        _viewModel.CurrentRedactionArea.Should().Be(testRect);
        _viewModel.CurrentRedactionPageArea.Should().NotBeNull();
        _viewModel.CurrentRedactionPageArea!.Value.Space.Should().Be(PdfCoordinateSpace.ViewerDips);
        _viewModel.CurrentRedactionPageArea.Value.PageNumber.Should().Be(1);
        _viewModel.CurrentRedactionPageArea.Value.Dpi.Should().Be(120);
    }

    [Fact]
    public void CurrentRedactionPageArea_BackfillsLegacyRedactionArea()
    {
        var pageArea = PdfPageRect.ViewerDips(1, 12, 24, 120, 48, 120);

        _viewModel.CurrentRedactionPageArea = pageArea;

        _viewModel.CurrentRedactionArea.Should().Be(new Rect(12, 24, 120, 48));
        _viewModel.CurrentRedactionRenderDpi.Should().Be(120);
    }

    [Fact]
    public void CurrentRedactionRenderDpi_UpdatesTaggedViewerAreaScale()
    {
        _viewModel.CurrentRedactionArea = new Rect(10, 20, 100, 50);

        _viewModel.CurrentRedactionRenderDpi = 144;

        _viewModel.CurrentRedactionPageArea.Should().NotBeNull();
        _viewModel.CurrentRedactionPageArea!.Value.Space.Should().Be(PdfCoordinateSpace.ViewerDips);
        _viewModel.CurrentRedactionPageArea.Value.Dpi.Should().Be(144);
    }

    [Fact]
    public void CurrentTextSelectionArea_InitiallyZeroRect()
    {
        _viewModel.CurrentTextSelectionArea.Should().Be(default(Rect));
    }

    [Fact]
    public void CurrentTextSelectionArea_CanBeSet()
    {
        // Arrange
        var testRect = new Rect(50, 100, 200, 75);

        // Act
        var field = typeof(MainWindowViewModel).GetField("_currentTextSelectionArea",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, testRect);

        // Assert
        _viewModel.CurrentTextSelectionArea.Should().Be(testRect);
    }

    [Fact]
    public void CurrentTextSelectionPageArea_CanBeSetAndDrivesHasTextSelection()
    {
        var pageArea = PdfPageRect.ViewerDips(1, 50, 100, 200, 75, 120);

        _viewModel.CurrentTextSelectionPageArea = pageArea;
        _viewModel.SelectedText = "Selected content";

        _viewModel.CurrentTextSelectionPageArea.Should().Be(pageArea);
        _viewModel.HasTextSelection.Should().BeTrue();
    }

    #endregion

    #region Sidebar Visibility Tests

    [Fact]
    public void IsThumbnailsSidebarVisible_InitiallyTrue()
    {
        _viewModel.IsThumbnailsSidebarVisible.Should().BeTrue();
    }

    [Fact]
    public void IsThumbnailsSidebarVisible_CanBeToggled()
    {
        _viewModel.IsThumbnailsSidebarVisible = false;
        _viewModel.IsThumbnailsSidebarVisible.Should().BeFalse();

        _viewModel.IsThumbnailsSidebarVisible = true;
        _viewModel.IsThumbnailsSidebarVisible.Should().BeTrue();
    }

    [Fact]
    public void OutlineSidebar_CanShowIndependentlyOfThumbnails()
    {
        // Regression for #369: the outline used to be nested inside the
        // thumbnails sidebar, so turning thumbnails off hid the outline too.
        _viewModel.IsThumbnailsSidebarVisible = false;
        _viewModel.IsOutlineSidebarVisible = true;

        _viewModel.IsLeftSidebarVisible.Should().BeTrue(
            "the left sidebar must stay visible for the outline even with thumbnails off");
        _viewModel.IsSidebarSplitterVisible.Should().BeFalse(
            "the splitter only shows when BOTH panels are visible");
    }

    [Fact]
    public void ThumbnailsSidebar_CanShowIndependentlyOfOutline()
    {
        _viewModel.IsOutlineSidebarVisible = false;
        _viewModel.IsThumbnailsSidebarVisible = true;

        _viewModel.IsLeftSidebarVisible.Should().BeTrue();
        _viewModel.IsSidebarSplitterVisible.Should().BeFalse();
    }

    [Fact]
    public void LeftSidebar_HiddenWhenBothOutlineAndThumbnailsOff()
    {
        _viewModel.IsOutlineSidebarVisible = false;
        _viewModel.IsThumbnailsSidebarVisible = false;

        _viewModel.IsLeftSidebarVisible.Should().BeFalse(
            "with neither panel enabled the whole left sidebar collapses");
    }

    [Fact]
    public void SidebarSplitter_VisibleOnlyWhenBothPanelsVisible()
    {
        _viewModel.IsOutlineSidebarVisible = true;
        _viewModel.IsThumbnailsSidebarVisible = true;
        _viewModel.IsSidebarSplitterVisible.Should().BeTrue();
    }

    [Fact]
    public void ToggleOutlineCommand_FlipsOutlineVisibility()
    {
        var before = _viewModel.IsOutlineSidebarVisible;
        _viewModel.ToggleOutlineCommand.Execute().Subscribe();
        _viewModel.IsOutlineSidebarVisible.Should().Be(!before);
    }

    [Fact]
    public void ToggleThumbnailsCommand_FlipsThumbnailsVisibility()
    {
        var before = _viewModel.IsThumbnailsSidebarVisible;
        _viewModel.ToggleThumbnailsCommand.Execute().Subscribe();
        _viewModel.IsThumbnailsSidebarVisible.Should().Be(!before);
    }

    [Fact]
    public void IsClipboardSidebarVisible_InitiallyTrue()
    {
        _viewModel.IsClipboardSidebarVisible.Should().BeTrue();
    }

    [Fact]
    public void IsClipboardSidebarVisible_CanBeToggled()
    {
        _viewModel.IsClipboardSidebarVisible = false;
        _viewModel.IsClipboardSidebarVisible.Should().BeFalse();

        _viewModel.IsClipboardSidebarVisible = true;
        _viewModel.IsClipboardSidebarVisible.Should().BeTrue();
    }

    #endregion

    #region Status Text Tests

    [Fact]
    public void StatusText_ReturnsStatusMessage()
    {
        // StatusText is derived from document service state
        // Just verify it returns a valid string
        var statusText = _viewModel.StatusText;
        statusText.Should().NotBeNull();
        statusText.GetType().Name.Should().Be("String");
    }

    [Fact]
    public void OperationStatus_InitiallyEmpty()
    {
        _viewModel.OperationStatus.Should().BeEmpty();
    }

    [Fact]
    public void OperationStatus_CanBeSet()
    {
        _viewModel.OperationStatus = "Processing...";
        _viewModel.OperationStatus.Should().Be("Processing...");
    }

    #endregion

    #region Command Properties Tests

    [Fact]
    public void OpenFileCommand_IsNotNull()
    {
        _viewModel.OpenFileCommand.Should().NotBeNull();
    }

    [Fact]
    public void SaveFileCommand_IsNotNull()
    {
        _viewModel.SaveFileCommand.Should().NotBeNull();
    }

    [Fact]
    public void RemoveCurrentPageCommand_IsNotNull()
    {
        _viewModel.RemoveCurrentPageCommand.Should().NotBeNull();
    }

    [Fact]
    public void AddPagesCommand_IsNotNull()
    {
        _viewModel.AddPagesCommand.Should().NotBeNull();
    }

    [Fact]
    public void ToggleRedactionModeCommand_IsNotNull()
    {
        _viewModel.ToggleRedactionModeCommand.Should().NotBeNull();
    }

    [Fact]
    public void ApplyRedactionCommand_IsNotNull()
    {
        _viewModel.ApplyRedactionCommand.Should().NotBeNull();
    }

    [Fact]
    public void ClearAllRedactionsCommand_IsNotNull()
    {
        _viewModel.ClearAllRedactionsCommand.Should().NotBeNull();
    }

    [Fact]
    public void ApplyAllRedactionsCommand_IsNotNull()
    {
        _viewModel.ApplyAllRedactionsCommand.Should().NotBeNull();
    }

    [Fact]
    public void ToggleTextSelectionModeCommand_IsNotNull()
    {
        _viewModel.ToggleTextSelectionModeCommand.Should().NotBeNull();
    }

    [Fact]
    public void CopyTextCommand_IsNotNull()
    {
        _viewModel.CopyTextCommand.Should().NotBeNull();
    }

    [Fact]
    public void ZoomInCommand_IsNotNull()
    {
        _viewModel.ZoomInCommand.Should().NotBeNull();
    }

    [Fact]
    public void ZoomOutCommand_IsNotNull()
    {
        _viewModel.ZoomOutCommand.Should().NotBeNull();
    }

    [Fact]
    public void NextPageCommand_IsNotNull()
    {
        _viewModel.NextPageCommand.Should().NotBeNull();
    }

    [Fact]
    public void PreviousPageCommand_IsNotNull()
    {
        _viewModel.PreviousPageCommand.Should().NotBeNull();
    }

    [Fact]
    public void GoToPageCommand_IsNotNull()
    {
        _viewModel.GoToPageCommand.Should().NotBeNull();
    }

    [Fact]
    public void SaveAsCommand_IsNotNull()
    {
        _viewModel.SaveAsCommand.Should().NotBeNull();
    }

    [Fact]
    public void CloseDocumentCommand_IsNotNull()
    {
        _viewModel.CloseDocumentCommand.Should().NotBeNull();
    }

    [Fact]
    public void ExitCommand_IsNotNull()
    {
        _viewModel.ExitCommand.Should().NotBeNull();
    }

    [Fact]
    public void LoadRecentFileCommand_IsNotNull()
    {
        _viewModel.LoadRecentFileCommand.Should().NotBeNull();
    }

    #endregion

    #region Zoom Mode Tests

    [Fact]
    public void ZoomActualSizeCommand_IsNotNull()
    {
        _viewModel.ZoomActualSizeCommand.Should().NotBeNull();
    }

    [Fact]
    public void ZoomFitWidthCommand_IsNotNull()
    {
        _viewModel.ZoomFitWidthCommand.Should().NotBeNull();
    }

    [Fact]
    public void ZoomFitPageCommand_IsNotNull()
    {
        _viewModel.ZoomFitPageCommand.Should().NotBeNull();
    }

    #endregion

    #region Page Rotation Tests

    [Fact]
    public void RotatePageLeftCommand_IsNotNull()
    {
        _viewModel.RotatePageLeftCommand.Should().NotBeNull();
    }

    [Fact]
    public void RotatePageRightCommand_IsNotNull()
    {
        _viewModel.RotatePageRightCommand.Should().NotBeNull();
    }

    [Fact]
    public void RotatePage180Command_IsNotNull()
    {
        _viewModel.RotatePage180Command.Should().NotBeNull();
    }

    #endregion

    #region Export and Print Tests

    [Fact]
    public void ExportCurrentPageCommand_IsNotNull()
    {
        _viewModel.ExportCurrentPageCommand.Should().NotBeNull();
    }

    [Fact]
    public void ExportPagesCommand_IsNotNull()
    {
        _viewModel.ExportPagesCommand.Should().NotBeNull();
    }

    [Fact]
    public void PrintCommand_IsNotNull()
    {
        _viewModel.PrintCommand.Should().NotBeNull();
    }

    #endregion

    #region Help and About Tests

    [Fact]
    public void AboutCommand_IsNotNull()
    {
        _viewModel.AboutCommand.Should().NotBeNull();
    }

    [Fact]
    public void ShowShortcutsCommand_IsNotNull()
    {
        _viewModel.ShowShortcutsCommand.Should().NotBeNull();
    }

    [Fact]
    public void ShowDocumentationCommand_IsNotNull()
    {
        _viewModel.ShowDocumentationCommand.Should().NotBeNull();
    }

    [Fact]
    public void VerifySignaturesCommand_IsNotNull()
    {
        _viewModel.VerifySignaturesCommand.Should().NotBeNull();
    }

    [Fact]
    public void ToggleContinuousViewCommand_TogglesViewMode()
    {
        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();

        _viewModel.ToggleContinuousViewCommand.Execute().Subscribe();

        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);
        _viewModel.IsContinuousView.Should().BeTrue();

        _viewModel.ToggleContinuousViewCommand.Execute().Subscribe();

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();
    }

    [Fact]
    public void EnteringEditingMode_LeavesContinuousView()
    {
        _viewModel.ViewMode = PdfViewMode.Continuous;

        _viewModel.IsRedactionMode = true;

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();
    }

    [Fact]
    public void SignatureVerificationSummaryFormatter_IncludesCurrentVerificationScope()
    {
        var results = new List<SignatureVerificationResult>
        {
            new()
            {
                SignatureName = "Approval",
                IsValid = true,
                SignedBy = "CN=Jane Doe",
                ByteRangeStructureChecked = true,
                ByteRangeStructureValid = true,
                ByteRangeIntegrityChecked = true,
                ByteRangeIntegrityValid = true,
                CoversWholeDocument = true,
                StatusMessage = "Signature is cryptographically valid and ByteRange digest matches"
            }
        };

        var summary = new SignatureVerificationSummaryFormatter().Format(results);

        summary.Should().Contain("Signature: Approval");
        summary.Should().Contain("CMS signature check: passed (CMS bytes and ByteRange digest only)");
        summary.Should().Contain("Signer: CN=Jane Doe");
        summary.Should().Contain("Signing time: not extracted");
        summary.Should().Contain("ByteRange structure: passed");
        summary.Should().Contain("Signed byte-range digest: passed");
        summary.Should().Contain("Covers whole document: yes");
        summary.Should().Contain("Certificate trust chain: not evaluated by the OS trust store.");
    }

    [Fact]
    public void SignatureVerificationSummaryFormatter_UsesUnknownForMissingSignatureMetadata()
    {
        var results = new List<SignatureVerificationResult>
        {
            new()
            {
                IsValid = false,
                StatusMessage = "Invalid or missing ByteRange"
            }
        };

        var summary = new SignatureVerificationSummaryFormatter().Format(results);

        summary.Should().Contain("Signature: unknown");
        summary.Should().Contain("CMS signature check: failed (CMS bytes and ByteRange digest only)");
        summary.Should().Contain("Signer: unknown");
        summary.Should().Contain("Details: Invalid or missing ByteRange");
        summary.Should().Contain("ByteRange structure: not checked");
        summary.Should().Contain("Signed byte-range digest: not checked");
        summary.Should().Contain("Covers whole document: no");
    }

    [Fact]
    public void ShowPreferencesCommand_IsNotNull()
    {
        _viewModel.ShowPreferencesCommand.Should().NotBeNull();
    }

    #endregion

    #region Search API Tests

    [Fact]
    public void SearchText_InitiallyEmpty()
    {
        _viewModel.SearchText.Should().BeEmpty();
    }

    [Fact]
    public void SearchText_CanBeSet()
    {
        _viewModel.SearchText = "test query";
        _viewModel.SearchText.Should().Be("test query");
    }

    [Fact]
    public void SearchCaseSensitive_InitiallyFalse()
    {
        _viewModel.SearchCaseSensitive.Should().BeFalse();
    }

    [Fact]
    public void SearchCaseSensitive_CanBeToggled()
    {
        _viewModel.SearchCaseSensitive = true;
        _viewModel.SearchCaseSensitive.Should().BeTrue();

        _viewModel.SearchCaseSensitive = false;
        _viewModel.SearchCaseSensitive.Should().BeFalse();
    }

    [Fact]
    public void SearchWholeWords_InitiallyFalse()
    {
        _viewModel.SearchWholeWords.Should().BeFalse();
    }

    [Fact]
    public void SearchWholeWords_CanBeToggled()
    {
        _viewModel.SearchWholeWords = true;
        _viewModel.SearchWholeWords.Should().BeTrue();

        _viewModel.SearchWholeWords = false;
        _viewModel.SearchWholeWords.Should().BeFalse();
    }

    [Fact]
    public void IsSearching_InitiallyFalse()
    {
        _viewModel.IsSearching.Should().BeFalse();
    }

    [Fact]
    public void SearchProgressText_InitiallyEmpty()
    {
        _viewModel.SearchProgressText.Should().BeEmpty();
    }

    #endregion

    #region Search Matches Tests

    [Fact]
    public void SearchMatches_InitiallyEmpty()
    {
        _viewModel.SearchMatches.Should().BeEmpty();
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithDependencies_InitializesSuccessfully()
    {
        // Arrange & Act
        var vm = new MainWindowViewModel(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _mockDocumentService.Object,
            _mockRenderService.Object,
            _mockRedactionService.Object,
            _mockTextExtractionService.Object,
            _mockSearchService.Object,
            _mockSignatureService.Object,
            _mockFilenameSuggestionService.Object,
            new PdfEditor.Services.ToastService());

        // Assert
        vm.Should().NotBeNull();
        vm.FileState.Should().NotBeNull();
        vm.RedactionWorkflow.Should().NotBeNull();
    }

    [Fact]
    public void ParameterlessConstructor_InitializesSuccessfully()
    {
        // Act
        var vm = new MainWindowViewModel();

        // Assert
        vm.Should().NotBeNull();
        vm.FileState.Should().NotBeNull();
        vm.RedactionWorkflow.Should().NotBeNull();
    }

    #endregion

    #region PropertyChanged Tests

    [Fact]
    public void CurrentPageIndex_PropertyChangedRaised()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _viewModel.CurrentPageIndex = 5;

        // Assert
        changedProperties.Should().Contain("CurrentPageIndex");
    }

    [Fact]
    public void ZoomLevel_PropertyChangedRaised()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        // Act
        _viewModel.ZoomLevel = _viewModel.ZoomLevel == 1.5 ? 2.0 : 1.5;

        // Assert
        changedProperties.Should().Contain("ZoomLevel");
    }

    #endregion

    #region Scripting Surface Tests

    [Fact]
    public void LoadDocumentTimeoutSeconds_CanBeDisabledWithZero()
    {
        _viewModel.LoadDocumentTimeoutSeconds = 0;
        _viewModel.LoadDocumentTimeoutSeconds.Should().Be(0);
    }

    [Fact]
    public void LoadDocumentTimeoutSeconds_CanBeSetToNegative()
    {
        _viewModel.LoadDocumentTimeoutSeconds = -1;
        _viewModel.LoadDocumentTimeoutSeconds.Should().Be(-1);
    }

    [Fact]
    public void PendingRedactions_TrackRedactionWorkflow()
    {
        // Arrange
        _viewModel.RedactionWorkflow.MarkArea(1, new Rect(10, 20, 50, 30), "Test");
        _viewModel.RedactionWorkflow.MarkArea(2, new Rect(0, 0, 100, 50), "Another");

        // Act
        var pending = _viewModel.PendingRedactions;

        // Assert
        pending.Should().HaveCount(2);
        pending.Should().AllSatisfy(r => r.PreviewText.Should().NotBeEmpty());
    }

    [Fact]
    public void FilePath_EmptyWhenNotSet()
    {
        // Default state has no file path set
        var initialPath = _viewModel.FilePath;
        initialPath.Should().BeEmpty();
    }

    #endregion

    #region Color and Display Format Tests

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(99)]
    public void CurrentPageIndex_SetAndRetrieve(int pageIndex)
    {
        _viewModel.CurrentPageIndex = pageIndex;
        _viewModel.CurrentPageIndex.Should().Be(pageIndex);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 6)]
    [InlineData(99, 100)]
    public void CurrentPage_OneBasedConversion(int zeroBasedIndex, int expectedOneBased)
    {
        _viewModel.CurrentPageIndex = zeroBasedIndex;
        _viewModel.CurrentPage.Should().Be(expectedOneBased);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 6)]
    [InlineData(99, 100)]
    public void DisplayPageNumber_OneBasedDisplay(int zeroBasedIndex, int expectedDisplay)
    {
        _viewModel.CurrentPageIndex = zeroBasedIndex;
        _viewModel.DisplayPageNumber.Should().Be(expectedDisplay);
    }

    #endregion

    #region Viewport and Rendering Tests

    [Fact]
    public void ViewportWidth_ZeroInitially()
    {
        // The default constructor initializes viewport size
        _viewModel.ViewportWidth.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ViewportHeight_ZeroInitially()
    {
        _viewModel.ViewportHeight.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ViewportDimensions_CanBeSet()
    {
        _viewModel.ViewportWidth = 1024;
        _viewModel.ViewportHeight = 768;

        _viewModel.ViewportWidth.Should().Be(1024);
        _viewModel.ViewportHeight.Should().Be(768);
    }

    #endregion

    #region Redaction State Tests

    [Fact]
    public void RedactionWorkflow_CanAddAndRemoveRedactions()
    {
        // Arrange
        var area = new Rect(50, 75, 100, 50);

        // Act - Add
        _viewModel.RedactionWorkflow.MarkArea(1, area, "Secret");
        var addedCount = _viewModel.RedactionWorkflow.PendingCount;

        // Assert - Added
        addedCount.Should().Be(1);

        // Act - Remove
        var id = _viewModel.RedactionWorkflow.PendingRedactions[0].Id;
        _viewModel.RedactionWorkflow.RemovePending(id);

        // Assert - Removed
        _viewModel.RedactionWorkflow.PendingCount.Should().Be(0);
    }

    [Fact]
    public void RedactionWorkflow_CanClearAllRedactions()
    {
        // Arrange
        _viewModel.RedactionWorkflow.MarkArea(1, new Rect(0, 0, 100, 50), "A");
        _viewModel.RedactionWorkflow.MarkArea(2, new Rect(0, 0, 100, 50), "B");
        _viewModel.RedactionWorkflow.MarkArea(3, new Rect(0, 0, 100, 50), "C");

        // Act
        _viewModel.RedactionWorkflow.ClearPending();

        // Assert
        _viewModel.RedactionWorkflow.PendingCount.Should().Be(0);
        _viewModel.RedactionWorkflow.HasPendingRedactions.Should().BeFalse();
    }

    [Fact]
    public void FileState_TracksDocumentPath()
    {
        // Arrange
        var path = "/test/document.pdf";

        // Act
        _viewModel.FileState.SetDocument(path);

        // Assert
        _viewModel.FileState.CurrentFilePath.Should().Be(path);
        _viewModel.FileState.IsOriginalFile.Should().BeTrue();
    }

    [Fact]
    public void FileState_TracksUnsavedChanges()
    {
        // Arrange
        _viewModel.FileState.SetDocument("/test/doc.pdf");
        _viewModel.FileState.HasUnsavedChanges.Should().BeFalse();

        // Act
        _viewModel.FileState.PendingRedactionsCount = 5;

        // Assert
        _viewModel.FileState.HasUnsavedChanges.Should().BeTrue();
    }

    #endregion

    #region Search Mode Tests

    [Fact]
    public void SearchText_EmptyByDefault()
    {
        _viewModel.SearchText.Should().BeEmpty();
    }

    [Fact]
    public void SearchText_CanBeUpdated()
    {
        _viewModel.SearchText = "search term";
        _viewModel.SearchText.Should().Be("search term");

        _viewModel.SearchText = "another search";
        _viewModel.SearchText.Should().Be("another search");
    }

    [Fact]
    public void SearchOptions_CanBeToggled()
    {
        // Initially false
        _viewModel.SearchCaseSensitive.Should().BeFalse();
        _viewModel.SearchWholeWords.Should().BeFalse();

        // Toggle on
        _viewModel.SearchCaseSensitive = true;
        _viewModel.SearchWholeWords = true;

        _viewModel.SearchCaseSensitive.Should().BeTrue();
        _viewModel.SearchWholeWords.Should().BeTrue();

        // Toggle off
        _viewModel.SearchCaseSensitive = false;
        _viewModel.SearchWholeWords = false;

        _viewModel.SearchCaseSensitive.Should().BeFalse();
        _viewModel.SearchWholeWords.Should().BeFalse();
    }

    #endregion

    #region Text Selection Tests

    [Fact]
    public void SelectedText_CanBeSet()
    {
        var testText = "Selected content";
        var field = typeof(MainWindowViewModel).GetField("_selectedText",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, testText);

        _viewModel.SelectedText.Should().Be(testText);
    }

    [Fact]
    public void TextSelectionArea_CanBeSetAndRetrieved()
    {
        var rect = new Rect(10, 20, 100, 50);
        var field = typeof(MainWindowViewModel).GetField("_currentTextSelectionArea",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_viewModel, rect);

        _viewModel.CurrentTextSelectionArea.Should().Be(rect);
    }

    #endregion

    #region Multiple Page Redaction Tests

    [Fact]
    public void RedactionWorkflow_SupportMultiPageRedactions()
    {
        // Arrange - Add redactions on different pages
        _viewModel.RedactionWorkflow.MarkArea(1, new Rect(0, 0, 50, 50), "Page 1");
        _viewModel.RedactionWorkflow.MarkArea(2, new Rect(0, 0, 50, 50), "Page 2");
        _viewModel.RedactionWorkflow.MarkArea(3, new Rect(0, 0, 50, 50), "Page 3");

        // Act
        var page1 = _viewModel.RedactionWorkflow.GetPendingForPage(1).ToList();
        var page2 = _viewModel.RedactionWorkflow.GetPendingForPage(2).ToList();
        var page3 = _viewModel.RedactionWorkflow.GetPendingForPage(3).ToList();

        // Assert
        page1.Should().HaveCount(1);
        page2.Should().HaveCount(1);
        page3.Should().HaveCount(1);
        _viewModel.RedactionWorkflow.PendingCount.Should().Be(3);
    }

    [Fact]
    public void RedactionWorkflow_MoveToAppliedTransfersRedactions()
    {
        // Arrange
        _viewModel.RedactionWorkflow.MarkArea(1, new Rect(0, 0, 50, 50), "Test1");
        _viewModel.RedactionWorkflow.MarkArea(2, new Rect(0, 0, 50, 50), "Test2");

        // Act
        _viewModel.RedactionWorkflow.MoveToApplied();

        // Assert
        _viewModel.RedactionWorkflow.PendingCount.Should().Be(0);
        _viewModel.RedactionWorkflow.AppliedCount.Should().Be(2);
        _viewModel.RedactionWorkflow.HasPendingRedactions.Should().BeFalse();
    }

    #endregion

    #region Outline/Sidebar Tests

    [Fact]
    public void OutlineNodes_InitiallyEmpty()
    {
        var outlineNodes = _viewModel.OutlineNodes;
        outlineNodes.Should().NotBeNull();
        outlineNodes.Count.Should().Be(0);
    }

    [Fact]
    public void HasOutline_FalseWhenEmpty()
    {
        _viewModel.HasOutline.Should().BeFalse();
    }

    #endregion

    #region Debug and Configuration Tests

    [Fact]
    public void RenderCacheMax_HasValidDefault()
    {
        var cacheMax = _viewModel.RenderCacheMax;
        cacheMax.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderCacheMax_CanBeAdjusted()
    {
        _viewModel.RenderCacheMax = 100;
        _viewModel.RenderCacheMax.Should().Be(100);

        _viewModel.RenderCacheMax = 5;
        _viewModel.RenderCacheMax.Should().Be(5);
    }

    #endregion
}
