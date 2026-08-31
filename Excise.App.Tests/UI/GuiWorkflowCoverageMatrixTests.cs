using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Excise.App.Tests.Controls;
using Excise.App.Tests.Integration;
using Excise.App.Tests.Unit;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Coverage contract for major GUI workflows (#959).
///
/// Before this file, a row proved only that its named test class contained
/// >=1 runnable [Fact] SOMEWHERE — a class could lose every meaningful
/// assertion but one and the row stayed green. This deepens the contract
/// the way check-doc-claim-freshness.sh re-derives documented numbers from
/// code, and the way
/// Excise.Core.Tests.FormatCompatibility.FormatCompatibilitySuiteEvidenceGateTests
/// pins a design tracker's rows to tests: each row names its REQUIRED
/// CAPABILITIES, and each capability must resolve to a specific, real,
/// named, runnable test method — not just "the class has a test".
///
/// This gate stays reflection-based (unlike the FormatCompatibility one,
/// which goes textual because its evidence spans assemblies it does not
/// reference) because every capability's covering test lives in this same
/// assembly, Excise.App.Tests — see that gate's header for the tradeoff.
///
/// A capability with no covering test is not hidden to keep the row green —
/// it is declared a GAP with an explanatory note
/// (<see cref="DeclaredGaps_CarryAnExplanatoryNote"/> requires the note;
/// nothing here fails the build over a pre-existing gap). That is
/// deliberate: #959 asks this gate to SURFACE missing coverage — e.g.
/// "open via command line" and "open via drag-drop" below, neither of
/// which has an Excise.App.Tests method today — not to invent hollow tests
/// just to make rows read as covered.
///
/// Rows also declare their <see cref="Modality"/> — which input surfaces
/// (mouse, keyboard, menu, toolbar, command line, drag-drop, scripting) the
/// workflow is claimed to be reachable from — so "every workflow reachable
/// by every advertised modality" (#695's epic question) is checkable row by
/// row instead of needing one giant harness. A GAP capability is how a row
/// can claim a modality (e.g. command-line open) without yet having proof.
/// </summary>
public class GuiWorkflowCoverageMatrixTests
{
    [Fact]
    public void RequiredCapabilities_ResolveToNamedRunnableTestMethods()
    {
        var failures = new List<string>();

        foreach (var row in CoverageRows())
        {
            foreach (var capability in row.Capabilities)
            {
                if (capability.CoveringClass is null)
                {
                    continue; // Declared gap — checked by DeclaredGaps_CarryAnExplanatoryNote instead.
                }

                var problem = DescribeMissingCoverage(capability.CoveringClass, capability.CoveringMethod!);
                if (problem != null)
                {
                    failures.Add(
                        $"{row.Workflow} / {capability.Name}: " +
                        $"{capability.CoveringClass.FullName}.{capability.CoveringMethod} {problem}");
                }
            }
        }

        failures.Should().BeEmpty(
            "every required capability on a GUI workflow row must resolve to a real, named, runnable test method");
    }

    [Fact]
    public void DeclaredGaps_CarryAnExplanatoryNote()
    {
        var undocumented = new List<string>();

        foreach (var row in CoverageRows())
        {
            foreach (var capability in row.Capabilities.Where(c => c.CoveringClass is null))
            {
                if (string.IsNullOrWhiteSpace(capability.GapNote))
                {
                    undocumented.Add($"{row.Workflow} / {capability.Name}");
                }
            }
        }

        undocumented.Should().BeEmpty(
            "a capability with no covering test is a real gap and must say so in GapNote, " +
            "not read as silently covered");
    }

    [Fact]
    public void EveryRow_DeclaresAtLeastOneCapabilityAndItsModality()
    {
        var incomplete = new List<string>();

        foreach (var row in CoverageRows())
        {
            if (row.Capabilities.Count == 0)
            {
                incomplete.Add($"{row.Workflow}: declares no required capabilities");
            }

            if (row.Modalities == Modality.None)
            {
                incomplete.Add($"{row.Workflow}: declares no input modality");
            }
        }

        incomplete.Should().BeEmpty(
            "every workflow row must name at least one required capability and the modality " +
            "(mouse/keyboard/menu/toolbar/command line/drag-drop/scripting) it is reachable from");
    }

    /// <summary>
    /// The live gap list — capabilities this matrix declares as required but
    /// for which no Excise.App.Tests method exists yet. Exposed as a static
    /// helper (rather than only asserted inline) so other tooling — or a
    /// future gate that wants to ratchet the gap count down — can read it
    /// without re-parsing this file.
    /// </summary>
    public static IReadOnlyList<string> KnownGaps() =>
        CoverageRows()
            .SelectMany(row => row.Capabilities
                .Where(c => c.CoveringClass is null)
                .Select(c => $"{row.Workflow} / {c.Name}: {c.GapNote}"))
            .ToList();

    private static string? DescribeMissingCoverage(Type type, string methodName)
    {
        var method = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == methodName);

        if (method is null)
        {
            return "was not found";
        }

        var runnable = method.GetCustomAttributes(inherit: false)
            .Any(attr => attr is Attribute attribute && IsRunnableFact(attribute));

        return runnable
            ? null
            : "exists but has no runnable [Fact]/[Theory]-family attribute (or is Skip-only)";
    }

    private static IReadOnlyList<CoverageRow> CoverageRows() =>
    [
        new("Open PDFs from the app and command/open-with entry points",
            Modality.Mouse | Modality.Keyboard | Modality.Menu | Modality.CommandLine | Modality.DragDrop,
            [
                Capability.Covered("open via file dialog", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.OpenFileCommand_Execute_StubbedDialog_LoadsDocument)),
                Capability.Covered("open via keyboard shortcut (Ctrl+O)", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.CtrlO_OpensFileDialog)),
                Capability.Covered("open via recent files list", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.LoadRecentFileCommand_Execute_LoadsTheGivenRecentPath)),
                Capability.Covered("open via command-line args resolves and loads (#979)", typeof(StartupActivationWorkflowTests), nameof(StartupActivationWorkflowTests.CommandLineArgs_ResolvedPath_ActuallyLoadsIntoTheViewModel)),
                Capability.Covered("open via macOS file-association activation resolves and loads (#979)", typeof(StartupActivationWorkflowTests), nameof(StartupActivationWorkflowTests.FileAssociationActivation_ResolvedPath_ActuallyLoadsIntoTheViewModel)),
                // #979 closed the "resolved path never proven to reach LoadDocumentAsync"
                // half of this gap: App.OpenPathAsync and App.ResolveActivatedPdfPath
                // (the "path -> loaded document" glue for both the command-line and
                // macOS file-activation launch paths in
                // App.OnFrameworkInitializationCompleted) went from private to internal
                // and are now called directly by StartupActivationWorkflowTests, composed
                // with the already-tested StartupDocumentResolver arg-parsing
                // (StartupDocumentResolverTests). What remains a genuine, narrower gap —
                // deliberately still declared rather than claimed covered — is the OS/
                // process-launch mechanics those tests cannot reach headlessly: spawning
                // the actual excise process with argv, and macOS Launch Services actually
                // delivering an IActivatableLifetime.Activated event. That slice is a
                // packaging-smoke concern, not a unit-test one, and stays covered by
                // scripts/run-packaged-gui-smoke.sh --mode direct-exec.
                Capability.Gap("open via command-line/file-association launch: the OS/process-launch step itself",
                    "The resolve+load glue this launch path depends on (StartupDocumentResolver.Resolve, " +
                    "App.OpenPathAsync, App.ResolveActivatedPdfPath) is now driven directly by " +
                    "StartupActivationWorkflowTests and StartupDocumentResolverTests (#979). What is NOT, and " +
                    "cannot be cheaply be made to be, driven by Excise.App.Tests: spawning the real OS process " +
                    "with command-line argv, and macOS Launch Services actually delivering an " +
                    "IActivatableLifetime.Activated event — both require a running desktop lifetime outside " +
                    "Avalonia.Headless's SetupWithoutStarting harness this suite uses. Covered instead by " +
                    "scripts/run-packaged-gui-smoke.sh --mode direct-exec, which spawns the real " +
                    "published binary. See #959, #979."),
                Capability.Gap("open via drag-drop",
                    "Not just untested — the FEATURE does not exist. Zero references to DragEventArgs / OnDrop " +
                    "/ DragDrop. / AllowDrop anywhere in Excise.App or Excise.Avalonia (#959, re-checked by " +
                    "#979). There is no Drop handler to drive, so this is a feature gap prior to being a test " +
                    "gap — implementing drag-drop-to-open is out of scope for a coverage-depth pass. Filed as " +
                    "#1002 to track the feature; once a Drop handler exists, Avalonia routed events "  +
                    "(DragDrop.DropEvent) can very likely be raised directly against a control in the existing " +
                    "headless harness the same way pointer/keyboard events already are in this suite — headless " +
                    "drag-drop synthesis is expected to be possible, just currently moot."),
            ]),
        new("Navigate long PDFs, thumbnails, zoom, fit width/page",
            Modality.Mouse | Modality.Keyboard | Modality.Toolbar,
            [
                Capability.Covered("zoom in", typeof(PdfViewerControlTests), nameof(PdfViewerControlTests.PdfViewerControl_ZoomIn_IncreasesZoomLevel)),
                Capability.Covered("zoom out", typeof(PdfViewerControlTests), nameof(PdfViewerControlTests.PdfViewerControl_ZoomOut_DecreasesZoomLevel)),
                Capability.Covered("keyboard zoom shortcuts", typeof(PdfViewerControlTests), nameof(PdfViewerControlTests.PdfViewerControl_KeyboardZoomShortcuts_UpdateZoom)),
                Capability.Covered("keyboard page-navigation shortcuts", typeof(PdfViewerControlTests), nameof(PdfViewerControlTests.PdfViewerControl_KeyboardPageShortcuts_UpdateCurrentPage)),
                Capability.Covered("fit page width", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.Ctrl1_FitsPageWidth)),
                Capability.Covered("fit entire page", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.Ctrl2_FitsEntirePage)),
            ]),
        new("Thumbnail cache and page preview workflow",
            Modality.Mouse,
            [
                Capability.Covered("render then reuse from disk cache", typeof(ThumbnailCacheTests), nameof(ThumbnailCacheTests.FirstCallRenders_SecondCallLoadsFromDisk)),
                Capability.Covered("concurrent requests for the same page coalesce", typeof(ThumbnailCacheTests), nameof(ThumbnailCacheTests.ConcurrentRequestsForSamePage_CoalesceOnSingleTask)),
            ]),
        new("Export the live page or document as images",
            Modality.Mouse | Modality.Menu,
            [
                Capability.Covered("export current page to a non-empty PNG", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.ExportCurrentPageCommand_Execute_StubbedDialog_WritesNonEmptyPng)),
                Capability.Covered("export reflects an unsaved live-document rotation", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.ExportCurrentPageCommand_UsesUnsavedLiveDocumentRotation)),
                Capability.Covered("export one image per document page", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.ExportPagesCommand_Execute_StubbedDialog_WritesOnePngPerPage)),
            ]),
        new("Search, select text, copy text",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("mouse-drag text selection", typeof(TextSelectionDragTests), nameof(TextSelectionDragTests.DragOverFirstLine_SelectsExpectedReadingOrderText)),
                Capability.Covered("copy selection via Ctrl+C", typeof(TextSelectionDragTests), nameof(TextSelectionDragTests.CtrlC_AfterPhraseSelection_CopiesExactlyTheSelectedPhrase)),
                Capability.Covered("selection populates clipboard history", typeof(TextSelectionDragTests), nameof(TextSelectionDragTests.AfterSelection_ClipboardHistoryGetsTheSelectedText)),
                Capability.Covered("search via Find command", typeof(RedactionAndSearchCommandTests), nameof(RedactionAndSearchCommandTests.FindCommand_PopulatesMatches)),
            ]),
        new("Search workflow and highlight overlays",
            Modality.Mouse | Modality.Keyboard | Modality.Menu,
            [
                Capability.Covered("matches draw highlight rectangles", typeof(SearchHighlightOverlayTests), nameof(SearchHighlightOverlayTests.SearchInPragmaticBook_DrawsHighlightRectangles)),
                Capability.Covered("F3 finds the next match", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.F3_FindsNextMatch)),
                Capability.Covered("next/previous commands advance and wrap", typeof(RedactionAndSearchCommandTests), nameof(RedactionAndSearchCommandTests.FindNextAndPreviousCommands_AdvanceAndWrapCurrentMatchIndex)),
            ]),
        new("Fill common forms, save filled copy, reopen",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("edit a text field via the overlay", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.EditingTextField_MutatesUnderlyingFieldAndMarksDirty)),
                Capability.Covered("select a radio-button-group choice", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.RadioButtonGroup_RendersChoiceSelectorAndCommitsValue)),
                Capability.Covered("save-as preserves the filled value on reopen", typeof(FormWorkflowTests), nameof(FormWorkflowTests.SaveFileAsAsync_PreservesFilledInteractiveFormValue)),
            ]),
        new("Flatten form copy, reopen, verify static output",
            Modality.Mouse | Modality.Menu,
            [
                Capability.Covered("flatten via save-flattened-copy workflow", typeof(FormWorkflowTests), nameof(FormWorkflowTests.SaveFlattenedFormCopyAsAsync_BakesFormValueAndRemovesAcroForm)),
                Capability.Covered("flatten via the menu/toolbar command", typeof(FileOpsCommandTests), nameof(FileOpsCommandTests.SaveFlattenedFormCopyCommand_Execute_StubbedDialog_BakesValueAndDropsAcroForm)),
            ]),
        new("Add typewriter text to flat PDF, save copy, reopen",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("mouse click places a typewriter box", typeof(TypewriterWorkflowTests), nameof(TypewriterWorkflowTests.RealClickInTypewriterMode_PlacesABox_NoDragRequired)),
                Capability.Covered("save flattens pending typewriter text", typeof(TypewriterWorkflowTests), nameof(TypewriterWorkflowTests.SaveFileAsAsync_FlattensPendingTypewriterTextIntoSavedPdf)),
                Capability.Covered("typed text is read back by an independent extractor", typeof(TypewriterWorkflowTests), nameof(TypewriterWorkflowTests.SaveFileAsAsync_TypedText_IsReadBackByAnIndependentExtractor)),
                Capability.Covered("discarding pending edits clears state before save", typeof(TypewriterWorkflowTests), nameof(TypewriterWorkflowTests.DiscardPendingTypewriterEdits_ClearsState_AndTextIsAbsentFromSavedPdf)),
            ]),
        new("Highlight selected text and add sticky notes, save, reopen",
            Modality.Mouse | Modality.Menu | Modality.Toolbar,
            [
                Capability.Covered("highlight from a text selection", typeof(AnnotationAuthoringWorkflowTests), nameof(AnnotationAuthoringWorkflowTests.AddHighlightAnnotationFromSelectionAsync_CreatesPersistableHighlightAndRefreshesViewerDocument)),
                Capability.Covered("add a sticky note", typeof(AnnotationAuthoringWorkflowTests), nameof(AnnotationAuthoringWorkflowTests.AddStickyNoteAnnotationAsync_CreatesPersistableStickyNote)),
                Capability.Covered("annotation commands are available for toolbar/menu", typeof(AnnotationAuthoringWorkflowTests), nameof(AnnotationAuthoringWorkflowTests.AnnotationCommands_AreAvailableForToolbarAndMenuCoverage)),
            ]),
        new("Reorder, rotate, extract, remove, and combine pages",
            Modality.Mouse | Modality.Keyboard | Modality.Menu,
            [
                Capability.Covered("reorder via the workflow service marks the document dirty", typeof(PageOrganizationWorkflowTests), nameof(PageOrganizationWorkflowTests.MoveCurrentPageAsync_ReordersDocumentAndMarksDirty)),
                Capability.Covered("reorder via move-earlier command", typeof(PageOrganizationCommandTests), nameof(PageOrganizationCommandTests.MoveCurrentPageEarlierCommand_MovesCurrentPageToPriorIndex)),
                Capability.Covered("rotate via command", typeof(PageOrganizationCommandTests), nameof(PageOrganizationCommandTests.RotatePageLeftCommand_RotatesCurrentPageMinus90)),
                Capability.Covered("rotate via keyboard shortcut", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.CtrlL_RotatesPageLeft)),
                Capability.Covered("extract selected pages", typeof(PageOrganizationCommandTests), nameof(PageOrganizationCommandTests.ExtractSelectedPagesCommand_WritesExactlyTheMarkedPages)),
                Capability.Covered("remove selected pages", typeof(PageOrganizationCommandTests), nameof(PageOrganizationCommandTests.RemoveSelectedPagesCommand_RemovesTheMarkedPages)),
                Capability.Covered("combine documents", typeof(PageOrganizationCommandTests), nameof(PageOrganizationCommandTests.CombineDocumentsCommand_MergesBothSourcesIntoOneFile)),
                Capability.Covered("organization changes survive save + reopen", typeof(PageOrganizationSavePersistenceTests), nameof(PageOrganizationSavePersistenceTests.RotatePage180Command_ThenSaveAs_RotationSurvivesReopen)),
            ]),
        new("Redact text/area, save copy, verify structural removal",
            Modality.Mouse | Modality.Keyboard | Modality.Menu,
            [
                Capability.Covered("mouse-drag area redaction across corpus scenarios", typeof(RedactionMouseWorkflowTests), nameof(RedactionMouseWorkflowTests.MouseDragRedaction_CorpusScenarios_RedactExpectedContentAndSave)),
                Capability.Covered("apply pending redactions — saved-bytes oracle", typeof(RedactionAndSearchCommandTests), nameof(RedactionAndSearchCommandTests.ApplyAllRedactionsCommand_RemovesSecretFromSavedFile_SavedBytesOracle)),
                Capability.Covered("apply pending redactions — independent-extractor oracle", typeof(RedactionAndSearchCommandTests), nameof(RedactionAndSearchCommandTests.ApplyAllRedactionsCommand_RedactedSecret_NotReadableByIndependentExtractor)),
                Capability.Covered("apply via keyboard (Enter)", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.Enter_AppliesRedaction)),
            ]),
        new("Metadata and attachment scrub status for redacted copies",
            Modality.Menu,
            [
                Capability.Covered("scrubs Info/XMP/embedded files before save", typeof(RedactedCopySafetyPolicyTests), nameof(RedactedCopySafetyPolicyTests.PrepareRedactedCopy_ScrubsInfoXmpAndEmbeddedFilesBeforeSave)),
                Capability.Covered("surfaces unexamined bookmark/off-box carriers rather than guessing", typeof(RedactedCopySafetyPolicyTests), nameof(RedactedCopySafetyPolicyTests.PrepareRedactedCopy_WithBookmarksAndOffBoxAnnotations_SaysTheyWereNotExamined)),
                Capability.Covered("warns when raster still overlaps the redaction area", typeof(RedactedCopySafetyPolicyTests), nameof(RedactedCopySafetyPolicyTests.PrepareRedactedCopy_WhenRasterStillOverlapsRedactionArea_WarnsForManualReview)),
            ]),
        new("Audit hidden text with clear user-facing states",
            Modality.Mouse | Modality.Menu,
            [
                Capability.Covered("reveal toggle flushes highlights for hidden text", typeof(RevealHiddenTextTests), nameof(RevealHiddenTextTests.RevealToggle_FlushesHighlightsForHiddenText)),
                Capability.Covered("reveal text hidden by a raster overlay inside an image", typeof(RevealHiddenTextTests), nameof(RevealHiddenTextTests.RevealRasterizedHidden_FindsTextHiddenByOverlayInsideImage)),
            ]),
        new("Audit signatures with clear user-facing states",
            Modality.Menu,
            [
                Capability.Covered("no-document-loaded state", typeof(SignatureVerificationWorkflowServiceTests), nameof(SignatureVerificationWorkflowServiceTests.VerifyAsync_WhenNoDocumentLoaded_ShowsOpenDocumentMessage)),
                Capability.Covered("no-signatures-present state", typeof(SignatureVerificationWorkflowServiceTests), nameof(SignatureVerificationWorkflowServiceTests.VerifyAsync_WhenDocumentHasNoSignatures_ShowsNoSignaturesMessage)),
                Capability.Covered("verification-error state", typeof(SignatureVerificationWorkflowServiceTests), nameof(SignatureVerificationWorkflowServiceTests.VerifyAsync_WhenVerificationReturnsError_ShowsFormattedFailureSummary)),
            ]),
        new("Toolbar and menu command bindings",
            Modality.Mouse | Modality.Menu | Modality.Toolbar,
            [
                Capability.Covered("every button/menu-item command resolves", typeof(CommandBindingSweepTests), nameof(CommandBindingSweepTests.EveryButtonAndMenuItemCommand_ResolvesToNonNullCommand)),
                Capability.Covered("macOS native menu command items resolve", typeof(CommandBindingSweepTests), nameof(CommandBindingSweepTests.MacNativeMenuCommandItems_ResolveToNonNullCommands)),
            ]),
        new("Interaction-mode toolbar buttons: display invariants across modes and device pixel ratios",
            Modality.Mouse | Modality.Toolbar,
            [
                Capability.Covered("mode switch shows a single page with a size-invariant image", typeof(ModeSwitchDisplayTests), nameof(ModeSwitchDisplayTests.ModeSwitch_ShowsSinglePage_WithSizeInvariantImage)),
                Capability.Covered("zoom inside a mode keeps layout size and sharpens pixels", typeof(ModeSwitchDisplayTests), nameof(ModeSwitchDisplayTests.ZoomInsideMode_KeepsLayoutSize_AndSharpensPixels)),
            ]),
        new("Interaction-mode toolbar buttons: pixel-level displayed-text verification",
            Modality.Mouse | Modality.Toolbar,
            [
                Capability.Covered("page text remains displayed after clicking a mode button", typeof(ModeSwitchVisualTests), nameof(ModeSwitchVisualTests.PageText_RemainsDisplayed_AfterClickingModeButton)),
                Capability.Covered("form-document mode click keeps text and places overlay ink at the field", typeof(ModeSwitchVisualTests), nameof(ModeSwitchVisualTests.FormDocument_ModeClick_KeepsTextAndPutsOverlayInkAtTheField)),
            ]),
        new("Keyboard shortcuts",
            Modality.Keyboard,
            [
                Capability.Covered("Ctrl+O opens the file dialog", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.CtrlO_OpensFileDialog)),
                Capability.Covered("Ctrl+S saves the file", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.CtrlS_SavesFile)),
                Capability.Covered("R toggles redaction mode", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.R_ToggleRedactionMode)),
                Capability.Covered("compound flow: search workflow", typeof(KeyboardShortcutTests), nameof(KeyboardShortcutTests.CompoundFlow_SearchWorkflow)),
            ]),
        new("Mouse link activation",
            Modality.Mouse,
            [
                Capability.Covered("clicking a TOC link navigates to its destination page", typeof(InPageLinkClickTests), nameof(InPageLinkClickTests.ClickOnTocLink_NavigatesToDestinationPage)),
            ]),
        new("Outline tree navigation",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("outline populates after document load", typeof(OutlineTreeNavigationTests), nameof(OutlineTreeNavigationTests.OutlineTree_PopulatesAfterDocumentLoad)),
                Capability.Covered("pointer click on a row triggers navigation", typeof(OutlineTreeNavigationTests), nameof(OutlineTreeNavigationTests.OutlineTree_PointerClickOnRow_TriggersNavigation)),
                Capability.Covered("setting the selected item navigates to its page", typeof(OutlineTreeNavigationTests), nameof(OutlineTreeNavigationTests.OutlineTree_SettingSelectedItem_NavigatesToPage)),
            ]),
        new("Page viewer render smoke and visual baseline",
            Modality.Mouse,
            [
                Capability.Covered("renders simple text matching the baseline", typeof(PdfViewerHeadlessRenderTests), nameof(PdfViewerHeadlessRenderTests.PdfViewer_RendersSimpleText_MatchesBaseline)),
                Capability.Covered("renders a real-world document matching the baseline", typeof(PdfViewerHeadlessRenderTests), nameof(PdfViewerHeadlessRenderTests.PdfViewer_RendersBirthCertificate_MatchesBaseline)),
                Capability.Covered("rendering-quality suite across displayed bitmaps", typeof(PdfViewerHeadlessRenderTests), nameof(PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer)),
            ]),
        new("Form field overlays and field editing",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("one overlay input is painted per field", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.FormFieldsLayer_PaintsOneInputPerField)),
                Capability.Covered("editing a text field mutates the value and marks the document dirty", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.EditingTextField_MutatesUnderlyingFieldAndMarksDirty)),
                Capability.Covered("multiline field: Escape reverts, Ctrl+Enter commits", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.MultilineTextField_EscapeRevertsAndCtrlEnterCommits)),
                Capability.Covered("radio-button group renders a selector and commits the value", typeof(FormFieldsOverlayTests), nameof(FormFieldsOverlayTests.RadioButtonGroup_RendersChoiceSelectorAndCommitsValue)),
            ]),
        new("Form authoring mouse workflow",
            Modality.Mouse | Modality.Menu,
            [
                Capability.Covered("toggle form-authoring mode", typeof(FormAuthoringTests), nameof(FormAuthoringTests.ToggleFormAuthoringMode_FlipsInteractionMode)),
                Capability.Covered("drawing a rect creates a text field", typeof(FormAuthoringTests), nameof(FormAuthoringTests.OnFormFieldRectDrawn_CreatesTextFieldWithUniqueName)),
                Capability.Covered("drawing a rect creates a checkbox field", typeof(FormAuthoringTests), nameof(FormAuthoringTests.OnFormFieldRectDrawn_CheckboxType_CreatesButtonField)),
                Capability.Covered("auto-detect-fields command scans and applies suggestions", typeof(FormAuthoringTests), nameof(FormAuthoringTests.AutoDetectFieldsCommand_ScansAndAppliesSuggestions)),
            ]),
        new("Open, search, redact, close golden paths",
            Modality.Mouse | Modality.Keyboard | Modality.Menu,
            [
                Capability.Covered("open, search, navigate, close", typeof(GoldenPathTests), nameof(GoldenPathTests.GoldenPath_OpenSearchNavigateClose)),
                Capability.Covered("open, redact, apply, verify text is gone", typeof(GoldenPathTests), nameof(GoldenPathTests.GoldenPath_OpenRedactApplyVerifyTextGone)),
                Capability.Covered("multi-page redaction", typeof(GoldenPathTests), nameof(GoldenPathTests.GoldenPath_MultiPageRedaction)),
                Capability.Covered("malformed PDF fails gracefully", typeof(GoldenPathTests), nameof(GoldenPathTests.GoldenPath_MalformedPdfGracefulFailure)),
            ]),
        new("GUI responsiveness budgets for open and direct input handlers",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("document open prioritizes the first page before background work", typeof(GuiResponsivenessBudgetTests), nameof(GuiResponsivenessBudgetTests.DocumentOpen_PrioritizesFirstPageBeforeBackgroundWork)),
                Capability.Covered("common interaction handlers return within the direct-input budget", typeof(GuiResponsivenessBudgetTests), nameof(GuiResponsivenessBudgetTests.CommonInteractionHandlers_ReturnWithinDirectInputBudget)),
            ]),
        new("Full GUI responsiveness under long documents and broad workflows",
            Modality.Mouse | Modality.Keyboard,
            [
                Capability.Covered("long-document continuous scroll stays responsive", typeof(GuiFullResponsivenessCoverageTests), nameof(GuiFullResponsivenessCoverageTests.LongDocumentContinuousScroll_StaysResponsiveAndWritesHotspotReport)),
                Capability.Covered("broad GUI workflows stay within responsiveness budgets", typeof(GuiFullResponsivenessCoverageTests), nameof(GuiFullResponsivenessCoverageTests.BroadGuiWorkflows_StayWithinResponsivenessBudgetsAndWriteHotspotReport)),
            ]),
        new("Accessibility metadata, keyboard-only reachability, and status announcements",
            Modality.Mouse | Modality.Keyboard | Modality.Menu,
            [
                Capability.Covered("command-backed controls expose accessible text", typeof(AccessibilityRegressionTests), nameof(AccessibilityRegressionTests.CommandBackedControls_UseSharedCommandMetadataForAccessibleText)),
                Capability.Covered("keyboard-only open/search/navigate/toggle-modes are reachable", typeof(AccessibilityRegressionTests), nameof(AccessibilityRegressionTests.KeyboardOnly_OpenSearchNavigateAndToggleModes_AreReachable)),
                Capability.Covered("dialogs expose accessible names and default cancel semantics", typeof(AccessibilityRegressionTests), nameof(AccessibilityRegressionTests.Dialogs_ExposeAccessibleNamesAndDefaultCancelSemantics)),
            ]),
        new("UX/icon polish screenshots and toolbar/menu affordance audit",
            Modality.Mouse | Modality.Toolbar | Modality.Menu,
            [
                Capability.Covered("toolbar icon buttons have tooltips and accessibility command ids", typeof(VisualPolishAuditTests), nameof(VisualPolishAuditTests.ToolbarIconButtons_HaveTooltipsAndAccessibilityCommandIds)),
                Capability.Covered("core workflow screenshots are captured for the audit", typeof(VisualPolishAuditTests), nameof(VisualPolishAuditTests.CoreWorkflowScreenshots_AreCapturedForUxIconAudit)),
            ]),
#if EXCISE_SCRIPTING
        // Scripting (and its tests) are compiled out of Release builds —
        // see Excise.App.Tests.csproj's EnableScripting mirror of #341/#342.
        new("Scripted GUI automation entry points",
            Modality.Scripting,
            [
                Capability.Covered("a script can access the view-model", typeof(ScriptedGuiTests), nameof(ScriptedGuiTests.Script_CanAccessViewModel_ReturnsExpectedValue)),
                Capability.Covered("Script.RedactText creates a redaction area", typeof(ScriptedGuiTests), nameof(ScriptedGuiTests.Script_RedactText_CreatesRedactionArea)),
                Capability.Covered("a script drives the complete redaction workflow end-to-end", typeof(ScriptedGuiTests), nameof(ScriptedGuiTests.Script_CompleteRedactionWorkflow_EndToEnd)),
            ]),
#endif
    ];

    private sealed record CoverageRow(string Workflow, Modality Modalities, IReadOnlyList<Capability> Capabilities);

    private sealed record Capability(string Name, Type? CoveringClass, string? CoveringMethod, string? GapNote)
    {
        public static Capability Covered(string name, Type coveringClass, string coveringMethod) =>
            new(name, coveringClass, coveringMethod, null);

        public static Capability Gap(string name, string note) =>
            new(name, null, null, note);
    }

    /// <summary>
    /// Which input surfaces a workflow row is claimed to be reachable from.
    /// Deliberately a row-level union rather than per-capability: the
    /// per-capability names already say which surface each covers (e.g.
    /// "open via keyboard shortcut"), and #695's question — is every
    /// workflow reachable by every advertised modality — is a row-level
    /// question this flag set answers directly.
    /// </summary>
    [Flags]
    private enum Modality
    {
        None = 0,
        Mouse = 1 << 0,
        Keyboard = 1 << 1,
        Menu = 1 << 2,
        Toolbar = 1 << 3,
        CommandLine = 1 << 4,
        DragDrop = 1 << 5,
        Scripting = 1 << 6,
    }

    private static bool IsRunnableFact(Attribute attr)
    {
        if (attr.GetType().Name is not (
            "FactAttribute" or "FixedAvaloniaFactAttribute" or
            "TheoryAttribute" or "FixedAvaloniaTheoryAttribute"))
        {
            return false;
        }

        var skip = attr.GetType().GetProperty("Skip")?.GetValue(attr) as string;
        return string.IsNullOrWhiteSpace(skip);
    }
}
