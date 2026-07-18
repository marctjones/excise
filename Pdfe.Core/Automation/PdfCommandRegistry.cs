using System.Collections.ObjectModel;

namespace Pdfe.Core.Automation;

/// <summary>
/// Central metadata registry for pdfe semantic commands.
/// </summary>
public static class PdfCommandRegistry
{
    private static readonly IReadOnlyList<PdfCommandMetadata> Commands =
    [
        App(PdfCommandIds.Open, "Open PDF", "Open a PDF document.", "Ctrl+O"),
        App(PdfCommandIds.Save, "Save Document", "Save the current PDF document.", "Ctrl+S", requiresDocument: true),
        App(PdfCommandIds.SaveAs, "Save As", "Save the current document to a new PDF file.", "Ctrl+Shift+S", requiresDocument: true),
        App(PdfCommandIds.CloseDocument, "Close Document", "Close the current document.", "Ctrl+W", requiresDocument: true),
        App(PdfCommandIds.Preferences, "Preferences", "Open application preferences.", "Ctrl+,"),
        App(PdfCommandIds.KeyboardShortcuts, "Keyboard Shortcuts", "Show keyboard shortcuts.", "F1"),
        App(PdfCommandIds.Documentation, "Documentation", "Open pdfe documentation."),
        App(PdfCommandIds.About, "About pdfe", "Show pdfe version and license information."),

        Search(PdfCommandIds.SearchOpen, "Find Text", "Open the search bar to find text in the document.", "Ctrl+F"),
        Search(PdfCommandIds.SearchFind, "Find", "Search for the entered text.", "Enter"),
        Search(PdfCommandIds.SearchNext, "Find Next", "Move to the next search result.", "F3"),
        Search(PdfCommandIds.SearchPrevious, "Find Previous", "Move to the previous search result.", "Shift+F3"),
        Search(PdfCommandIds.SearchClose, "Close Search", "Close the search bar.", "Esc"),

        Edit(PdfCommandIds.SelectTextMode, "Text Selection Mode", "Toggle text selection mode.", "T"),
        Edit(PdfCommandIds.CopyText, "Copy Selected Text", "Copy selected text to the clipboard.", "Ctrl+C"),
        Edit(PdfCommandIds.TypewriterMode, "Typewriter Mode", "Place editable text that saves as page content."),
        Annotate(PdfCommandIds.AddHighlight, "Add Highlight From Selection", "Create a PDF highlight annotation from the current text selection."),
        Annotate(PdfCommandIds.AddStickyNote, "Add Sticky Note", "Create a sticky-note annotation."),

        Document(PdfCommandIds.AddPages, "Add Pages", "Append pages from another PDF."),
        Document(PdfCommandIds.InsertPagesBefore, "Insert Pages Before Current", "Insert pages before the current page."),
        Document(PdfCommandIds.InsertPagesAfter, "Insert Pages After Current", "Insert pages after the current page."),
        Document(PdfCommandIds.ExtractCurrentPage, "Extract Current Page", "Save the current page as a new PDF."),
        Document(PdfCommandIds.ExtractSelectedPages, "Extract Selected Pages", "Save selected pages as a new PDF."),
        Document(PdfCommandIds.MoveCurrentPageEarlier, "Move Page Earlier", "Move the current page one position earlier."),
        Document(PdfCommandIds.MoveCurrentPageLater, "Move Page Later", "Move the current page one position later."),
        Document(PdfCommandIds.MoveSelectedPagesEarlier, "Move Selected Pages Earlier", "Move selected pages one position earlier."),
        Document(PdfCommandIds.MoveSelectedPagesLater, "Move Selected Pages Later", "Move selected pages one position later."),
        Document(PdfCommandIds.RemoveCurrentPage, "Remove Current Page", "Remove the current page from the document.", isDestructive: true),
        Document(PdfCommandIds.RemoveSelectedPages, "Remove Selected Pages", "Remove selected pages from the document.", isDestructive: true),
        Document(PdfCommandIds.ClearPageSelection, "Clear Page Selection", "Clear all selected pages."),
        Document(PdfCommandIds.RotateLeft, "Rotate Page Left", "Rotate the current page 90 degrees counter-clockwise.", "Ctrl+L"),
        Document(PdfCommandIds.RotateRight, "Rotate Page Right", "Rotate the current page 90 degrees clockwise.", "Ctrl+R"),
        Document(PdfCommandIds.Rotate180, "Rotate Page 180 Degrees", "Rotate the current page 180 degrees."),
        Document(PdfCommandIds.ExportCurrentPage, "Export Current Page", "Export the current page as an image.", "Ctrl+E"),
        Document(PdfCommandIds.ExportAllPages, "Export All Pages as Images", "Export every page as images."),
        Document(PdfCommandIds.Print, "Print", "Print the current document.", "Ctrl+P"),
        Document(PdfCommandIds.Security, "Document Security", "Set, change, or remove password protection on the document (AES-256 or AES-128)."),
        Document(PdfCommandIds.CombineDocuments, "Combine Documents", "Merge pages from multiple PDFs into a new document, preserving links, bookmarks, and form fields.",
            requiresDocument: false, cliCommand: "merge",
            parameters: [Param("input", "Source PDF file path. Repeat for multiple sources.", "file[]", true), Param("output", "Output PDF path.", "file", true)],
            resultFields: ["pageCount", "outputPath"]),
        Document(PdfCommandIds.SplitDocument, "Split Document", "Split the current document into multiple PDFs by page count, boundaries, or bookmarks.",
            cliCommand: "split",
            parameters: [Param("input", "Source PDF file path.", "file", true), Param("output", "Output folder.", "file", true), Param("every", "Pages per output file.", "integer"), Param("boundaries", "Comma-separated 1-based page numbers where new files start.", "string"), Param("bookmarks", "Split at root-level bookmark destinations.", "boolean"), Param("single", "One page per output file.", "boolean")],
            resultFields: ["fileCount", "outputPaths"]),

        View(PdfCommandIds.PreviousPage, "Previous Page", "Navigate to the previous page.", "Page Up"),
        View(PdfCommandIds.NextPage, "Next Page", "Navigate to the next page.", "Page Down"),
        View(PdfCommandIds.GoToPage, "Go To Page", "Navigate to a specific page.", parameters: [Param("page", "1-based page number.", "integer", true)]),
        View(PdfCommandIds.ZoomIn, "Zoom In", "Increase the zoom level.", "Ctrl++"),
        View(PdfCommandIds.ZoomOut, "Zoom Out", "Decrease the zoom level.", "Ctrl+-"),
        View(PdfCommandIds.ZoomActualSize, "Actual Size", "Reset zoom to actual size.", "Ctrl+0"),
        View(PdfCommandIds.ZoomFitWidth, "Fit Width", "Fit the page to the window width.", "Ctrl+1"),
        View(PdfCommandIds.ZoomFitPage, "Fit Page", "Fit the whole page in the window.", "Ctrl+2"),
        View(PdfCommandIds.ToggleContinuousView, "Continuous Scroll View", "Toggle continuous scrolling.", "Ctrl+Shift+C"),
        View(PdfCommandIds.ToggleOutline, "Toggle Outline Sidebar", "Show or hide the outline sidebar.", "Ctrl+Shift+O"),
        View(PdfCommandIds.ToggleThumbnails, "Toggle Thumbnails Sidebar", "Show or hide page thumbnails.", "Ctrl+Shift+T"),
        View(PdfCommandIds.ToggleClipboardHistory, "Show Clipboard History", "Show or hide clipboard and redaction history."),

        Redaction(PdfCommandIds.ToggleRedactionMode, "Redaction Mode", "Toggle redaction mode.", "R"),
        Redaction(PdfCommandIds.ApplyRedaction, "Apply Redaction", "Apply the current redaction selection.", "Enter", isDestructive: true, isSecuritySensitive: true),
        Redaction(PdfCommandIds.ApplyAllRedactions, "Apply All Redactions", "Apply every pending redaction.", isDestructive: true, isSecuritySensitive: true),
        Redaction(PdfCommandIds.ClearAllRedactions, "Clear All Redactions", "Clear pending redactions without changing the PDF."),
        Redaction(PdfCommandIds.RemovePendingRedaction, "Remove Pending Redaction", "Remove one pending redaction from the queue."),

        Form(PdfCommandIds.ToggleFormAuthoring, "Form Authoring Mode", "Toggle form authoring mode."),
        Form(PdfCommandIds.AutoDetectFields, "Auto-detect Form Fields", "Add likely form fields from visible page placeholders."),
        Form(PdfCommandIds.SaveFlattenedFormCopy, "Save Flattened Form Copy", "Save a copy where form values are baked into page content.", cliCommand: "fill-form --flatten"),
        Form(PdfCommandIds.FillForm, "Fill Form", "Set AcroForm field values and save.", cliCommand: "fill-form",
            parameters: [Param("field", "Field assignment in FullName=Value form.", "string[]", true), Param("flatten", "Bake values into page content.", "boolean")],
            resultFields: ["updatedFieldCount", "outputPath"]),
        Form(PdfCommandIds.AddFormField, "Add Form Field", "Add a new AcroForm field.", cliCommand: "add-field",
            parameters: [Param("type", "Field type.", "string", true), Param("name", "Full field name.", "string", true), Param("rect", "Field rectangle in PDF points.", "rectangle", true)],
            resultFields: ["fieldName", "fieldType", "outputPath"]),

        Cli(PdfCommandIds.DocumentInfo, "Document Info", "Show PDF document information.", "info",
            parameters: [Param("file", "PDF file to inspect.", "file", true)], resultFields: ["version", "pageCount", "metadata"]),
        Cli(PdfCommandIds.ExtractText, "Extract Text", "Extract text from a PDF.", "text",
            parameters: [Param("file", "PDF file.", "file", true), Param("page", "Optional page number.", "integer")], resultFields: ["text"]),
        Cli(PdfCommandIds.ShowLetters, "Show Letters", "Show extracted letters with positions.", "letters",
            parameters: [Param("file", "PDF file.", "file", true), Param("page", "Page number.", "integer")], resultFields: ["letters"]),
        Cli(PdfCommandIds.RenderPage, "Render Page", "Render a PDF page to a PNG image.", "render", requiresDocument: true,
            parameters: [Param("file", "PDF file.", "file", true), Param("output", "Output PNG path.", "file", true), Param("page", "Page number.", "integer")],
            resultFields: ["outputPath", "width", "height"]),
        Cli(PdfCommandIds.BatchWorkflow, "Run Automation Batch", "Run a JSON automation workflow with structured progress and report output.", "batch",
            parameters: [Param("workflow", "Workflow JSON file.", "file", true), Param("output", "Optional report JSON path.", "file")],
            resultFields: ["overallStatus", "steps"]),
        Redaction(PdfCommandIds.AuditHiddenText, "Audit Hidden Text", "Find hidden or covered text that can indicate failed visual-only redaction.", cliCommand: "audit", isSecuritySensitive: true),
        Cli(PdfCommandIds.OcrPage, "OCR Page", "Run OCR on a rendered page.", "ocr",
            parameters: [Param("file", "PDF file.", "file", true), Param("page", "Page number.", "integer")], resultFields: ["text"]),

        Tool(PdfCommandIds.RevealHiddenText, "Reveal Hidden Text", "Show text that is present in the PDF but visually covered.", isSecuritySensitive: true),
        Tool(PdfCommandIds.RevealRasterizedHiddenText, "Reveal Rasterized Hidden Text", "Run differential OCR to find rasterized hidden text.", isSecuritySensitive: true),
        Tool(PdfCommandIds.VerifySignatures, "Verify Digital Signatures", "Verify digital signatures in the current document.", isSecuritySensitive: true),
        Tool(PdfCommandIds.MakeSearchable, "Make Searchable", "OCR a scanned PDF and write the recognized text back as an invisible, searchable text layer."),
    ];

    private static readonly IReadOnlyDictionary<string, PdfCommandMetadata> ById =
        new ReadOnlyDictionary<string, PdfCommandMetadata>(
            Commands.ToDictionary(command => command.Id, StringComparer.Ordinal));

    public static IReadOnlyList<PdfCommandMetadata> All => Commands;

    public static PdfCommandMetadata Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ById.TryGetValue(id, out var command)
            ? command
            : throw new KeyNotFoundException($"Unknown pdfe command id '{id}'.");
    }

    public static bool TryGet(string id, out PdfCommandMetadata command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ById.TryGetValue(id, out command!);
    }

    public static IReadOnlyList<PdfCommandMetadata> ForCliCommand(string cliCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliCommand);
        return Commands
            .Where(command => string.Equals(command.CliCommand, cliCommand, StringComparison.Ordinal))
            .ToArray();
    }

    private static PdfCommandMetadata App(string id, string label, string description, string? shortcut = null, bool requiresDocument = false)
        => New(id, label, description, "Application", shortcut, requiresDocument: requiresDocument);

    private static PdfCommandMetadata Search(string id, string label, string description, string? shortcut = null)
        => New(id, label, description, "Search", shortcut, requiresDocument: true, disabledReason: "Open a PDF before using search.");

    private static PdfCommandMetadata Edit(string id, string label, string description, string? shortcut = null)
        => New(id, label, description, "Edit", shortcut, requiresDocument: true);

    private static PdfCommandMetadata Annotate(string id, string label, string description)
        => New(id, label, description, "Annotation", requiresDocument: true);

    private static PdfCommandMetadata Document(
        string id,
        string label,
        string description,
        string? shortcut = null,
        bool isDestructive = false,
        bool requiresDocument = true,
        string? cliCommand = null,
        IReadOnlyList<PdfCommandParameterMetadata>? parameters = null,
        IReadOnlyList<string>? resultFields = null)
        => New(id, label, description, "Document", shortcut, cliCommand, requiresDocument: requiresDocument, isDestructive: isDestructive,
            parameters: parameters, resultFields: resultFields);

    private static PdfCommandMetadata View(string id, string label, string description, string? shortcut = null, IReadOnlyList<PdfCommandParameterMetadata>? parameters = null)
        => New(id, label, description, "View", shortcut, requiresDocument: true, parameters: parameters);

    private static PdfCommandMetadata Redaction(
        string id,
        string label,
        string description,
        string? shortcut = null,
        string? cliCommand = null,
        bool isDestructive = false,
        bool isSecuritySensitive = false)
        => New(id, label, description, "Redaction", shortcut, cliCommand, requiresDocument: true,
            isDestructive: isDestructive,
            isSecuritySensitive: isSecuritySensitive,
            disabledReason: "Open a PDF and enter redaction mode before applying redactions.");

    private static PdfCommandMetadata Form(
        string id,
        string label,
        string description,
        string? cliCommand = null,
        IReadOnlyList<PdfCommandParameterMetadata>? parameters = null,
        IReadOnlyList<string>? resultFields = null)
        => New(id, label, description, "Forms", cliCommand: cliCommand, requiresDocument: true,
            parameters: parameters, resultFields: resultFields);

    private static PdfCommandMetadata Tool(string id, string label, string description, bool isSecuritySensitive = false)
        => New(id, label, description, "Tools", requiresDocument: true, isSecuritySensitive: isSecuritySensitive);

    private static PdfCommandMetadata Cli(
        string id,
        string label,
        string description,
        string cliCommand,
        bool requiresDocument = false,
        IReadOnlyList<PdfCommandParameterMetadata>? parameters = null,
        IReadOnlyList<string>? resultFields = null)
        => New(id, label, description, "CLI", cliCommand: cliCommand, requiresDocument: requiresDocument,
            parameters: parameters, resultFields: resultFields);

    private static PdfCommandMetadata New(
        string id,
        string label,
        string description,
        string category,
        string? shortcut = null,
        string? cliCommand = null,
        bool requiresDocument = false,
        bool isDestructive = false,
        bool isSecuritySensitive = false,
        string? disabledReason = null,
        IReadOnlyList<PdfCommandParameterMetadata>? parameters = null,
        IReadOnlyList<string>? resultFields = null)
        => new(
            id,
            label,
            description,
            category,
            shortcut,
            cliCommand,
            requiresDocument,
            isDestructive,
            isSecuritySensitive,
            disabledReason,
            parameters,
            resultFields);

    private static PdfCommandParameterMetadata Param(string name, string description, string type, bool required = false)
        => new(name, description, type, required);
}
