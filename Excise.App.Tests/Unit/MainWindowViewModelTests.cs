using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.Services;
using Excise.App.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Excise.App.Tests.Unit;

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
        _viewModel = MainWindowViewModelTestFactory.Create(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            _mockDocumentService.Object,
            _mockRedactionService.Object,
            _mockTextExtractionService.Object,
            _mockSearchService.Object,
            _mockSignatureService.Object,
            _mockFilenameSuggestionService.Object,
            new Excise.App.Services.ToastService());
    }

    #region Property Tests

    [Fact]
    public void IsTextSelectionMode_InitiallyTrue()
    {
        // #831: text selection is the resting affordance — on by default so a
        // drag selects text like every other PDF reader, no mode toggle needed.
        _viewModel.IsTextSelectionMode.Should().BeTrue();
    }

    [Fact]
    public void CurrentModeText_WhenRedactionMode_ReturnsRedactionText()
    {
        _viewModel.IsRedactionMode = true;

        _viewModel.CurrentModeText.Should().Contain("Redaction");
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
    public void LoadDocumentTimeoutSeconds_DefaultIsThirty()
    {
        _viewModel.LoadDocumentTimeoutSeconds.Should().Be(30);
    }

    #endregion

    #region DocumentStateManager Property Tests

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

    #region DocumentName / FilePath

    [Fact]
    public async Task DocumentNameAndFilePath_FollowTheLoadedDocument()
    {
        _viewModel.DocumentName.Should().Be("No document open");
        _viewModel.FilePath.Should().BeEmpty();

        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-mwvm-{Guid.NewGuid():N}.pdf");
        try
        {
            TestPdfGenerator.CreateSimpleTextPdf(pdfPath);

            await _viewModel.LoadDocumentHeadlessAsync(pdfPath);

            _viewModel.FilePath.Should().Be(pdfPath);
            _viewModel.DocumentName.Should().Be(Path.GetFileName(pdfPath));
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    #endregion

    #region Scripting API Tests

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

    #endregion

    #region Coordinate System Tests

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
    public void LeftSidebar_HiddenWhenEveryPaneIsOff()
    {
        _viewModel.IsOutlineSidebarVisible = false;
        _viewModel.IsThumbnailsSidebarVisible = false;

        // #1641: Attachments moved to the RIGHT sidebar, so outline and
        // thumbnails alone decide the left one.
        _viewModel.IsLeftSidebarVisible.Should().BeFalse(
            "with outline and thumbnails off the left sidebar collapses, whatever attachments does");

        _viewModel.IsAttachmentsSidebarVisible.Should().BeTrue("still on by default (#1563)");
        _viewModel.IsRightSidebarVisible.Should().BeTrue(
            "and it alone keeps the RIGHT sidebar open");

        _viewModel.IsAttachmentsSidebarVisible = false;
        _viewModel.IsRightSidebarVisible.Should().BeFalse(
            "with no pane wanting to show, the right sidebar collapses too");
    }

    [Fact]
    public void AttachmentsSidebar_IsVisibleByDefault_AndRaisesTheSidebarFlag()
    {
        _viewModel.IsAttachmentsSidebarVisible.Should().BeTrue();

        var raised = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _viewModel.ToggleAttachmentsSidebar();

        _viewModel.IsAttachmentsSidebarVisible.Should().BeFalse();
        raised.Should().Contain(nameof(MainWindowViewModel.IsAttachmentsSidebarVisible));
        raised.Should().Contain(nameof(MainWindowViewModel.IsRightSidebarVisible),
            "#1641: the pane moved to the right, and that host's visibility is computed from this flag");
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
    public void IsClipboardSidebarVisible_InitiallyFalse()
    {
        // #1654: off by default. An empty Clipboard History panel cost 250 px
        // of every launch, and while #1645 was live it filled with fragments of
        // the document nobody had asked to copy.
        _viewModel.IsClipboardSidebarVisible.Should().BeFalse();
    }

    #endregion

    #region Command Properties Tests

    [Fact]
    public void AllPublicCommandProperties_AreInitialized()
    {
        var missingCommands = typeof(MainWindowViewModel)
            .GetProperties()
            .Where(property => property.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => property.GetValue(_viewModel) is null)
            .Select(property => property.Name)
            .ToArray();

        missingCommands.Should().BeEmpty(
            "every XAML and automation command binding must resolve after construction");
    }

    #endregion

    #region Help and About Tests

    [Fact]
    public void ToggleContinuousViewCommand_TogglesViewMode()
    {
        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);
        _viewModel.IsContinuousView.Should().BeTrue();
        _viewModel.ContinuousScrollPreference.Should().BeTrue();

        _viewModel.ToggleContinuousViewCommand.Execute().Subscribe();

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();
        _viewModel.ContinuousScrollPreference.Should().BeFalse();

        _viewModel.ToggleContinuousViewCommand.Execute().Subscribe();

        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);
        _viewModel.IsContinuousView.Should().BeTrue();
        _viewModel.ContinuousScrollPreference.Should().BeTrue();
    }

    [Fact]
    public void ApplyContinuousScrollPreference_SetsCurrentViewAndSavedPreference()
    {
        _viewModel.ApplyContinuousScrollPreference(false);

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();
        _viewModel.ContinuousScrollPreference.Should().BeFalse();

        _viewModel.ApplyContinuousScrollPreference(true);

        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);
        _viewModel.IsContinuousView.Should().BeTrue();
        _viewModel.ContinuousScrollPreference.Should().BeTrue();
    }

    [Fact]
    public void EnteringEditingMode_LeavesContinuousViewWithoutChangingPreference()
    {
        _viewModel.ViewMode = PdfViewMode.Continuous;
        _viewModel.ApplyContinuousScrollPreference(true);

        _viewModel.IsRedactionMode = true;

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse();
        _viewModel.ContinuousScrollPreference.Should().BeTrue();
    }

    [Fact]
    public void ReapplyingInactiveEditingModes_DoesNotOverrideExplicitSinglePageView()
    {
        _viewModel.ViewMode = PdfViewMode.SinglePage;

        _viewModel.IsRedactionMode = false;
        _viewModel.IsFormAuthoringMode = false;
        _viewModel.IsTypewriterMode = false;
        _viewModel.IsPathAnnotationMode = false;
        _viewModel.IsTextSelectionMode = true;

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage,
            "idempotent mode cleanup during document open must not restore continuous view");
        _viewModel.ContinuousScrollPreference.Should().BeTrue(
            "the explicit effective view and saved reading preference are separate state");
    }

    [Theory]
    [InlineData(EditingMode.Redaction)]
    [InlineData(EditingMode.FormAuthoring)]
    [InlineData(EditingMode.Typewriter)]
    public void ExitingEditingMode_RestoresSavedContinuousScrollPreference(EditingMode mode)
    {
        // Text selection is excluded (#815): it no longer forces single-page, so
        // it never "exits" back to continuous — see TextSelection_StaysInContinuous.
        _viewModel.ApplyContinuousScrollPreference(true);

        SetEditingMode(mode, true);
        _viewModel.IsContinuousView.Should().BeFalse("draw/edit modes are single-page only");

        SetEditingMode(mode, false);

        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);
        _viewModel.IsContinuousView.Should().BeTrue(
            "leaving an editing mode must re-apply the saved continuous-scroll preference, " +
            "otherwise the preference is a one-way valve and the session is stranded in single-page");
    }

    [Theory]
    [InlineData(EditingMode.Redaction)]
    [InlineData(EditingMode.FormAuthoring)]
    [InlineData(EditingMode.Typewriter)]
    public void ExitingEditingMode_DoesNotForceContinuousWhenPreferenceIsSinglePage(EditingMode mode)
    {
        _viewModel.ApplyContinuousScrollPreference(false);

        SetEditingMode(mode, true);
        SetEditingMode(mode, false);

        _viewModel.ViewMode.Should().Be(PdfViewMode.SinglePage);
        _viewModel.IsContinuousView.Should().BeFalse(
            "restoring must honour the saved preference, not unconditionally switch to continuous");
    }

    [Fact]
    public void EnteringTextSelection_KeepsContinuousReadingView()
    {
        // #815: text selection is a read affordance, not a draw/edit mode, so it
        // must NOT force single-page — that is what made selecting undiscoverable
        // in the default continuous view.
        _viewModel.ApplyContinuousScrollPreference(true);
        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous);

        _viewModel.IsTextSelectionMode = true;

        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous,
            "entering text selection stays in the continuous reading view");
        _viewModel.IsContinuousView.Should().BeTrue();

        _viewModel.IsTextSelectionMode = false;
        _viewModel.ViewMode.Should().Be(PdfViewMode.Continuous,
            "leaving text selection also leaves the reading view untouched");
    }

    public enum EditingMode
    {
        Redaction,
        TextSelection,
        FormAuthoring,
        Typewriter
    }

    private void SetEditingMode(EditingMode mode, bool active)
    {
        switch (mode)
        {
            case EditingMode.Redaction:
                _viewModel.IsRedactionMode = active;
                break;
            case EditingMode.TextSelection:
                _viewModel.IsTextSelectionMode = active;
                break;
            case EditingMode.FormAuthoring:
                _viewModel.IsFormAuthoringMode = active;
                break;
            case EditingMode.Typewriter:
                _viewModel.IsTypewriterMode = active;
                break;
        }
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
        // Trust chain is a separate, explicit line (#466); revocation stays a
        // stated limitation because CRL/OCSP would require network access.
        summary.Should().Contain("Certificate trust chain: not evaluated");
        summary.Should().Contain("certificate revocation (CRL/OCSP) is not checked");
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
    public void CurrentPageIndex_SamePageFeedbackDoesNotClearSelectionOrRaiseChanges()
    {
        _viewModel.CurrentPageIndex = 2;
        _viewModel.SelectedText = "keep this selection";
        _viewModel.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(
            pageNumber: 3,
            x: 10,
            y: 20,
            width: 30,
            height: 40,
            renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _viewModel.CurrentPageIndex = 2;

        _viewModel.SelectedText.Should().Be("keep this selection");
        _viewModel.CurrentTextSelectionPageArea.Should().NotBeNull();
        changedProperties.Should().BeEmpty(
            "viewer page feedback for the already-current page is not a navigation transition");
    }

    #endregion

    #region Color and Display Format Tests

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
}
