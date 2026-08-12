namespace Excise.Core.Automation;

/// <summary>
/// Stable semantic command identifiers shared by GUI, CLI, automation, and
/// accessibility layers.
/// </summary>
public static class PdfCommandIds
{
    public const string Open = "app.open";
    public const string Save = "app.save";
    public const string SaveAs = "app.saveAs";
    public const string CloseDocument = "app.closeDocument";
    public const string Preferences = "app.preferences";
    public const string KeyboardShortcuts = "app.keyboardShortcuts";
    public const string Documentation = "app.documentation";
    public const string About = "app.about";

    public const string SearchOpen = "search.open";
    public const string SearchFind = "search.find";
    public const string SearchNext = "search.next";
    public const string SearchPrevious = "search.previous";
    public const string SearchClose = "search.close";

    public const string SelectTextMode = "edit.selectTextMode";
    public const string CopyText = "edit.copyText";
    public const string TypewriterMode = "edit.typewriterMode";
    public const string AddHighlight = "annotation.addHighlight";
    public const string AddUnderline = "annotation.addUnderline";
    public const string AddStrikeOut = "annotation.addStrikeOut";
    public const string AddSquiggly = "annotation.addSquiggly";

    /// <summary>Square annotation from the drag rectangle (#912).</summary>
    public const string AddSquare = "annotation.addSquare";

    /// <summary>Circle annotation from the drag rectangle (#912).</summary>
    public const string AddCircle = "annotation.addCircle";

    /// <summary>FreeText box from the drag rectangle plus a text prompt (#934).</summary>
    public const string AddFreeText = "annotation.addFreeText";

    /// <summary>Standard rubber stamp from the drag rectangle; parameter is the stamp name (#934).</summary>
    public const string AddStamp = "annotation.addStamp";

    /// <summary>Image stamp — a Stamp whose appearance is a chosen picture (#934).</summary>
    public const string AddImageStamp = "annotation.addImageStamp";

    /// <summary>Toggle free-form drawing; a finished stroke becomes an Ink annotation (#934).</summary>
    public const string ToggleDrawMode = "annotation.toggleDrawMode";

    /// <summary>Toggle line-drawing; a drag becomes a Line annotation (#934).</summary>
    public const string ToggleLineMode = "annotation.toggleLineMode";

    /// <summary>Toggle arrow-drawing; a drag becomes a Line with an arrowhead (#934).</summary>
    public const string ToggleArrowMode = "annotation.toggleArrowMode";

    /// <summary>Toggle polygon drawing; clicks plant vertices, double-click or Enter closes it (#934).</summary>
    public const string TogglePolygonMode = "annotation.togglePolygonMode";

    /// <summary>Toggle polyline drawing; the same vertex path, left open (#934).</summary>
    public const string TogglePolyLineMode = "annotation.togglePolyLineMode";
    public const string AddStickyNote = "annotation.addStickyNote";

    public const string AddPages = "document.addPages";
    public const string InsertPagesBefore = "document.insertPagesBefore";
    public const string InsertPagesAfter = "document.insertPagesAfter";
    public const string ExtractCurrentPage = "document.extractCurrentPage";
    public const string ExtractSelectedPages = "document.extractSelectedPages";
    public const string MoveCurrentPageEarlier = "document.moveCurrentPageEarlier";
    public const string MoveCurrentPageLater = "document.moveCurrentPageLater";
    public const string MoveSelectedPagesEarlier = "document.moveSelectedPagesEarlier";
    public const string MoveSelectedPagesLater = "document.moveSelectedPagesLater";
    public const string RemoveCurrentPage = "document.removeCurrentPage";
    public const string RemoveSelectedPages = "document.removeSelectedPages";
    public const string ClearPageSelection = "document.clearPageSelection";
    public const string RotateLeft = "document.rotateLeft";
    public const string RotateRight = "document.rotateRight";
    public const string Rotate180 = "document.rotate180";
    public const string ExportCurrentPage = "document.exportCurrentPage";
    public const string ExportAllPages = "document.exportAllPages";
    public const string Print = "document.print";
    public const string Security = "document.security";
    public const string CombineDocuments = "document.combine";
    public const string SplitDocument = "document.split";

    public const string PreviousPage = "view.previousPage";
    public const string NextPage = "view.nextPage";
    public const string GoToPage = "view.goToPage";
    public const string ZoomIn = "view.zoomIn";
    public const string ZoomOut = "view.zoomOut";
    public const string ZoomActualSize = "view.zoomActualSize";
    public const string ZoomFitWidth = "view.zoomFitWidth";
    public const string ZoomFitPage = "view.zoomFitPage";
    public const string ToggleContinuousView = "view.toggleContinuous";
    public const string ToggleOutline = "view.toggleOutline";
    public const string ToggleThumbnails = "view.toggleThumbnails";
    public const string ToggleClipboardHistory = "view.toggleClipboardHistory";

    public const string ToggleRedactionMode = "redaction.toggleMode";
    public const string ApplyRedaction = "redaction.apply";
    public const string ApplyAllRedactions = "redaction.applyAll";
    public const string ClearAllRedactions = "redaction.clearAll";
    public const string RemovePendingRedaction = "redaction.removePending";

    public const string ToggleFormAuthoring = "form.toggleAuthoring";
    public const string AutoDetectFields = "form.autoDetectFields";
    public const string SaveFlattenedFormCopy = "form.saveFlattenedCopy";
    public const string FillForm = "form.fillForm";
    public const string AddFormField = "form.addField";

    public const string RenderPage = "render.page";
    public const string ExtractText = "text.extract";
    public const string ShowLetters = "text.letters";
    public const string DocumentInfo = "document.info";
    public const string BatchWorkflow = "automation.batch";
    public const string AuditHiddenText = "audit.hiddenText";
    public const string OcrPage = "ocr.page";

    public const string RevealHiddenText = "tools.revealHiddenText";
    public const string RevealRasterizedHiddenText = "tools.revealRasterizedHiddenText";
    public const string VerifySignatures = "tools.verifySignatures";
    public const string MakeSearchable = "tools.makeSearchable";
}
