using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    internal const string DefaultStickyNoteText = "Review note";

    public event EventHandler? AnnotationsChanged;

    public async Task AddHighlightAnnotationFromSelectionAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a highlight annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentTextSelectionContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                "Add Highlight",
                "Select text before adding a highlight.");
            return;
        }

        var contents = string.IsNullOrWhiteSpace(SelectedText)
            ? "Highlight"
            : SelectedText.Trim();

        await CommitRectAnnotationAsync(
            new AnnotationRectRequest(
                AnnotationRectKind.Highlight,
                pageNumber,
                contentRect,
                contents),
            "highlight");
    }

    /// <summary>
    /// Underline, StrikeOut and Squiggly from the current text selection (#912).
    ///
    /// Core has been able to author all three since the annotation-authoring
    /// work landed; nothing in the app could reach them, so 13 of 15 authorable
    /// subtypes were unreachable. These three are the cheap ones: they reuse the
    /// SAME gesture Highlight already uses — select text, invoke — so no new
    /// input handling is involved.
    ///
    /// THE VIEWER MIRROR IS NOT OPTIONAL. A document is open TWICE — the
    /// save document (<c>_documentService</c>) and the viewer document
    /// (<c>_pdfCoreDocument</c>), loaded as two separate PdfDocument instances
    /// by LoadDocumentInstancesAsync. Authoring onto the save document alone
    /// puts the annotation in the saved FILE while leaving the screen
    /// unchanged: the user clicks "Add Underline", sees nothing happen, and
    /// only finds the underline after saving and reopening. So each of these
    /// takes a second delegate that repeats the authoring on the viewer
    /// document, exactly as the Highlight path has always done.
    ///
    /// A file-only test cannot see that gap — both halves save identically.
    /// TextMarkupAnnotationCommandTests therefore asserts on
    /// <c>PdfCoreDocument</c> BEFORE saving.
    /// </summary>
    private async Task AddTextMarkupFromSelectionAsync(
        string kind,
        AnnotationRectKind annotationKind)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations — same gate as
        // Highlight, and it must not be skipped just because this path is new.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            $"Adding a {kind} annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentTextSelectionContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                $"Add {kind}",
                $"Select text before adding a {kind.ToLowerInvariant()}.");
            return;
        }

        var contents = string.IsNullOrWhiteSpace(SelectedText) ? kind : SelectedText.Trim();

        await CommitRectAnnotationAsync(
            new AnnotationRectRequest(annotationKind, pageNumber, contentRect, contents),
            kind.ToLowerInvariant());
    }

    public Task AddUnderlineAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("Underline", AnnotationRectKind.Underline);

    public Task AddStrikeOutAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("StrikeOut", AnnotationRectKind.StrikeOut);

    public Task AddSquigglyAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("Squiggly", AnnotationRectKind.Squiggly);

    /// <summary>
    /// Square and Circle from the drag rectangle (#912's second row).
    ///
    /// Core has been able to author both since the annotation-authoring work
    /// landed, and `AnnotationWorkflowService` already exposed them — only the
    /// command wiring was missing, exactly as with the text-markup row.
    ///
    /// The gesture is the REDACTION BOX drag, reused. That is deliberate: it is
    /// the one rectangle gesture the app already implements, so no new input
    /// handling is involved. A user draws a box and chooses what it becomes.
    ///
    /// The viewer mirror is mandatory here for the same reason as the text
    /// markup — see AddTextMarkupFromSelectionAsync. Without it the shape lands
    /// in the saved file and never appears on screen.
    /// </summary>
    private async Task AddShapeFromDragAsync(
        string kind,
        AnnotationRectKind annotationKind)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            $"Adding a {kind} annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentShapeContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                $"Add {kind}",
                $"Drag a box on the page before adding a {kind.ToLowerInvariant()}.");
            return;
        }

        await CommitRectAnnotationAsync(
            new AnnotationRectRequest(annotationKind, pageNumber, contentRect, kind),
            kind.ToLowerInvariant());
    }

    public Task AddSquareAnnotationFromDragAsync() =>
        AddShapeFromDragAsync("Square", AnnotationRectKind.Square);

    public Task AddCircleAnnotationFromDragAsync() =>
        AddShapeFromDragAsync("Circle", AnnotationRectKind.Circle);

    /// <summary>
    /// FreeText from the drag rectangle plus a text prompt (#934 row A).
    ///
    /// The cheapest of the remaining subtypes: the rect gesture already exists
    /// (Square/Circle use it), <c>AddFreeText</c> already exists on the workflow
    /// service, and the prompt pattern is the one AddStickyNoteAnnotationAsync
    /// already uses. Unlike a sticky note — which is an ICON at a point and
    /// falls back to a default rect — a FreeText box IS the dragged region, so
    /// it requires the drag rather than defaulting.
    /// </summary>
    public async Task AddFreeTextAnnotationFromDragAsync(string? contentsOverride = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a free-text annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentShapeContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                "Add Text Box",
                "Drag a box on the page before adding a text box.");
            return;
        }

        var contents = contentsOverride
            ?? await _dialogService.PromptTextAsync("Add Text Box", "Enter text:", string.Empty);

        // A FreeText box with no text is not a useful annotation, and an empty
        // string is how a cancelled prompt arrives.
        if (string.IsNullOrWhiteSpace(contents))
            return;

        var text = contents.Trim();
        await CommitRectAnnotationAsync(
            new AnnotationRectRequest(
                AnnotationRectKind.FreeText,
                pageNumber,
                contentRect,
                text),
            "text box");
    }

    /// <summary>
    /// Standard rubber stamp from the drag rectangle (#934 row B).
    ///
    /// Takes the stamp NAME as a parameter rather than having fifteen commands:
    /// the menu offers the fixed set, and one command id serves automation.
    /// Core validates the name against ISO 32000-1 Table 181 and throws on
    /// anything else, so an invalid name is reported rather than silently
    /// producing a nameless stamp.
    /// </summary>
    public async Task AddStampAnnotationFromDragAsync(string stampName)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a stamp annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stampName))
            return;

        if (!TryGetCurrentShapeContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                "Add Stamp",
                "Drag a box on the page before adding a stamp.");
            return;
        }

        var name = stampName.Trim();
        await CommitRectAnnotationAsync(
            new AnnotationRectRequest(
                AnnotationRectKind.Stamp,
                pageNumber,
                contentRect,
                name),
            "stamp");
    }

    /// <summary>
    /// Called by MainWindow when the viewer raises AnnotationPathDrawn — the
    /// user has drawn a free-form stroke on the page (#934 D).
    ///
    /// The points arrive already in PDF content coordinates: the control
    /// converts them through the same mapper the redaction drag uses, so there
    /// is no second DIP→content transform here to drift out of step with it.
    /// </summary>
    public async Task OnAnnotationPathDrawnAsync(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, int pageNumber)
    {
        if (!_documentService.IsDocumentLoaded || strokes == null || strokes.Count == 0)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding an ink annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        var kind = PathAnnotationKind;
        try
        {
            var request = new AnnotationPathRequest(
                ToWorkflowPathKind(kind),
                pageNumber,
                strokes);
            var result = _annotationWorkflow.AddPath(request, _pdfCoreDocument);
            if (!result.WasAdded)
            {
                if (result.ValidationMessage is not null)
                {
                    await _dialogService.ShowMessageAsync(
                        "Add Polygon",
                        result.ValidationMessage);
                }
                return;
            }

            await MarkAnnotationChangedAsync(result.SuccessMessage);
            RecordAnnotationAdd(
                result.HistoryDescription,
                result.Request.PageNumber,
                result.Annotation!,
                () => _annotationWorkflow.ReplayPath(result.Request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding {Kind} annotation", kind);
            _toastService.ShowError($"Failed to add {kind.ToString().ToLowerInvariant()}", ex.Message);
        }
    }

    private static AnnotationPathKind ToWorkflowPathKind(PathAnnotationKind kind) => kind switch
    {
        PathAnnotationKind.Line => AnnotationPathKind.Line,
        PathAnnotationKind.Arrow => AnnotationPathKind.Arrow,
        PathAnnotationKind.Polygon => AnnotationPathKind.Polygon,
        PathAnnotationKind.PolyLine => AnnotationPathKind.PolyLine,
        _ => AnnotationPathKind.Ink
    };

    /// <summary>The stamp names the menu offers — Core's standard set (#934).</summary>
    public static IReadOnlyList<string> StandardStampNames =>
        PdfAnnotationAuthoring.StandardStampNames;

    // Test seam mirroring SetRedactedSavePathProviderForTests: headless UI tests
    // have no desktop lifetime and no interactive picker, so the real command
    // would bail before the authoring path runs. Every step after path
    // resolution — decode, author, mirror, record — runs unchanged.
    private Func<Task<string?>>? _imageStampPathProviderForTests;

    internal void SetImageStampPathProviderForTests(Func<Task<string?>>? provider)
        => _imageStampPathProviderForTests = provider;

    /// <summary>
    /// Image stamp from the drag rectangle plus a file picker (#934 row C) —
    /// signature placement, in practice.
    ///
    /// Core takes raw RGB, so the image is decoded here rather than in the
    /// service: SkiaSharp already ships with the app for rendering, and keeping
    /// the decode at the UI boundary means Core never grows an image-format
    /// dependency for an annotation feature.
    ///
    /// One-shot picker by design. A persisted stamp library is the obvious next
    /// want for a signature placed repeatedly, and is deliberately not built
    /// here — it is storage surface, not wiring.
    /// </summary>
    public async Task AddImageStampAnnotationFromDragAsync(string? imagePathOverride = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding an image stamp", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentShapeContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                "Add Image Stamp",
                "Drag a box on the page before adding an image stamp.");
            return;
        }

        var path = imagePathOverride ?? await ResolveImageStampPathAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;                                  // cancelled picker

        try
        {
            if (!TryDecodeRgb(path!, out var rgb, out var w, out var h))
            {
                await _dialogService.ShowMessageAsync(
                    "Add Image Stamp",
                    $"Could not read an image from '{System.IO.Path.GetFileName(path)}'. " +
                    "Supported formats are the ones the viewer can decode (PNG, JPEG, and similar).");
                return;
            }

            var annotation = _annotationWorkflow.AddImageStamp(
                pageNumber,
                contentRect,
                rgb,
                w,
                h,
                viewerDocument: _pdfCoreDocument);
            await MarkAnnotationChangedAsync("Image stamp added");
            RecordAnnotationAdd("Add image stamp", pageNumber, annotation,
                () => _annotationWorkflow.AddImageStamp(pageNumber, contentRect, rgb, w, h));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding image stamp annotation");
            _toastService.ShowError("Failed to add image stamp", ex.Message);
        }
    }

    private async Task<string?> ResolveImageStampPathAsync()
    {
        if (_imageStampPathProviderForTests != null)
            return await _imageStampPathProviderForTests();

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show the image picker");
            return null;
        }

        var files = await storageProvider.OpenFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a stamp image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new global::Avalonia.Platform.Storage.FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" }
                }
            }
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    /// <summary>
    /// Decode to the tightly-packed 24-bit RGB Core requires. Core validates
    /// that the buffer is exactly width*height*3 and throws otherwise, so any
    /// stride or channel-count mistake here surfaces immediately rather than
    /// producing a corrupt stamp.
    /// </summary>
    private static bool TryDecodeRgb(string path, out byte[] rgb, out int width, out int height)
    {
        rgb = Array.Empty<byte>();
        width = height = 0;

        using var bitmap = SkiaSharp.SKBitmap.Decode(path);
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return false;

        width = bitmap.Width;
        height = bitmap.Height;
        rgb = new byte[(long)width * height * 3];

        var i = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                rgb[i++] = c.Red;
                rgb[i++] = c.Green;
                rgb[i++] = c.Blue;
            }
        }
        return true;
    }

    public async Task AddStickyNoteAnnotationAsync(string? contentsOverride = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a sticky note", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        var contents = contentsOverride;
        if (contents == null)
        {
            var defaultText = string.IsNullOrWhiteSpace(SelectedText)
                ? DefaultStickyNoteText
                : SelectedText.Trim();

            contents = await _dialogService.PromptTextAsync(
                "Add Sticky Note",
                "Enter note text:",
                defaultText);
        }

        if (string.IsNullOrWhiteSpace(contents))
            return;

        try
        {
            var pageNumber = CurrentPageIndex + 1;
            var contentRect = TryGetCurrentTextSelectionContentRect(out var selectionPageNumber, out var selectionRect)
                ? selectionRect
                : GetDefaultStickyNoteRect(pageNumber);

            if (selectionPageNumber > 0)
                pageNumber = selectionPageNumber;

            var trimmedContents = contents.Trim();
            var notePageNumber = pageNumber;
            var noteRect = contentRect;
            await CommitRectAnnotationAsync(
                new AnnotationRectRequest(
                    AnnotationRectKind.TextNote,
                    notePageNumber,
                    noteRect,
                    trimmedContents),
                "sticky note");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding sticky-note annotation");
            _toastService.ShowError("Failed to add sticky note", ex.Message);
        }
    }

    /// <summary>
    /// Shape annotations (#912) come from a DRAG, not a text selection — the
    /// same gesture the redaction box already uses. Same conversion as the
    /// text-selection path, different source rectangle.
    /// </summary>
    private bool TryGetCurrentShapeContentRect(out int pageNumber, out PdfRectangle contentRect)
        => TryGetContentRect(CurrentRedactionPageArea, out pageNumber, out contentRect);

    private bool TryGetCurrentTextSelectionContentRect(out int pageNumber, out PdfRectangle contentRect)
        => TryGetContentRect(CurrentTextSelectionPageArea, out pageNumber, out contentRect);

    private bool TryGetContentRect(PdfPageRect? source, out int pageNumber, out PdfRectangle contentRect)
    {
        pageNumber = 0;
        contentRect = default;

        if (source is not { Width: > 0, Height: > 0 } selectionArea)
            return false;

        var document = _documentService.GetCurrentDocument();
        if (document == null ||
            selectionArea.PageNumber < 1 ||
            selectionArea.PageNumber > document.PageCount)
        {
            return false;
        }

        var page = document.GetPage(selectionArea.PageNumber);
        var normalized = PdfCoordinateMapper
            .ToContentPoints(page, selectionArea)
            .ToPdfRectangle()
            .Normalize();

        if (normalized.Width <= 0 || normalized.Height <= 0)
            return false;

        pageNumber = selectionArea.PageNumber;
        contentRect = normalized;
        return true;
    }

    private PdfRectangle GetDefaultStickyNoteRect(int pageNumber)
    {
        var document = _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException("No document loaded");

        if (pageNumber < 1 || pageNumber > document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var page = document.GetPage(pageNumber);
        var left = Math.Max(18, page.MediaBox.Normalize().Left + 48);
        var top = page.MediaBox.Normalize().Top - 48;
        return new PdfRectangle(left, top - 36, left + 36, top).Normalize();
    }

    private async Task CommitRectAnnotationAsync(
        AnnotationRectRequest request,
        string failureDescription)
    {
        try
        {
            var result = _annotationWorkflow.AddRect(request, _pdfCoreDocument);
            await MarkAnnotationChangedAsync(result.SuccessMessage);
            RecordAnnotationAdd(
                result.HistoryDescription,
                result.Request.PageNumber,
                result.Annotation,
                () => _annotationWorkflow.ReplayRect(result.Request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding {Kind} annotation", request.Kind);
            _toastService.ShowError($"Failed to add {failureDescription}", ex.Message);
        }
    }

    private Task MarkAnnotationChangedAsync(string toastMessage)
    {
        FileState.AnnotationEditsCount++;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        RequestViewerRenderRefresh();

        _toastService.ShowSuccess(toastMessage);
        return Task.CompletedTask;
    }

    private void ClearCurrentTextSelection()
    {
        CurrentTextSelectionArea = new global::Avalonia.Rect();
        CurrentTextSelectionPageArea = null;
        SelectedText = string.Empty;
    }
}
