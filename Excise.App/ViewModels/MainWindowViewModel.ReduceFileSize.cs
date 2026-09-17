using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Excise.App.Services.Host;
using Excise.Core.Writing;
using Excise.Rendering;
using Microsoft.Extensions.Logging;

namespace Excise.App.ViewModels;

/// <summary>
/// #1550 — Document ▸ Reduce File Size…
/// </summary>
/// <remarks>
/// <para>Always writes a NEW file the user picks; the open document and its
/// source file are never changed, and the new file is not opened in their
/// place. The lossy presets are not reversible, so the original has to stay
/// where it is.</para>
/// <para>Runs only on saved content: a document with unsaved edits is refused,
/// so the copy is exactly "the file on disk, smaller" and the size comparison
/// the user sees is against that file. The optimizer itself works on a copy
/// reopened from an ordinary save (<see cref="PdfDocumentOptimizer.SaveOptimizedCopy"/>),
/// so it cannot see anything a save would not write — including text a
/// redaction removed.</para>
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Test seam: supply the preset directly instead of showing the dialog.
    /// Returns <c>null</c> to simulate the user cancelling.
    /// </summary>
    internal Func<PdfOptimizationPreset?>? ReduceFileSizePresetOverride { get; set; }

    private async Task ReduceFileSizeAsync()
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
        {
            await _dialogService.ShowMessageAsync("Reduce File Size", "Open a PDF first.");
            return;
        }

        if (FileState.HasUnsavedChanges)
        {
            await _dialogService.ShowMessageAsync(
                "Reduce File Size",
                "Save your changes first. Reduce File Size makes a smaller copy of the saved file, " +
                "so the edits on screen would not be in it.");
            return;
        }

        var preset = ReduceFileSizePresetOverride != null
            ? ReduceFileSizePresetOverride()
            : await PromptForReduceFileSizePresetAsync();
        if (preset is not { } chosen)
        {
            _logger.LogInformation("Reduce File Size cancelled");
            return;
        }

        var outputPath = await _filePicker.SaveFileAsync(new SaveFileRequest
        {
            Title = "Save Smaller Copy",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestReducedFilename(_currentFilePath),
            Filters = new[] { FilePickerFilters.Pdf },
        });
        if (outputPath is not { Length: > 0 })
            return;

        if (!string.IsNullOrWhiteSpace(_currentFilePath)
            && string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(_currentFilePath),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            await _dialogService.ShowMessageAsync(
                "Reduce File Size",
                "Choose a different file name. Reduce File Size never replaces the original.");
            return;
        }

        await ReduceFileSizeToAsync(outputPath, chosen);
    }

    private async Task<PdfOptimizationPreset?> PromptForReduceFileSizePresetAsync()
    {
        var owner = GetMainWindow();
        if (owner == null)
        {
            _logger.LogWarning("Could not get main window for the Reduce File Size dialog");
            return null;
        }

        var dialogViewModel = new ReduceFileSizeDialogViewModel();
        var window = new Views.ReduceFileSizeDialog { DataContext = dialogViewModel };
        await window.ShowDialog(owner);
        return dialogViewModel.Confirmed ? dialogViewModel.Selected.Preset : null;
    }

    /// <summary>
    /// Write a smaller copy of the open, saved document to
    /// <paramref name="outputPath"/> and tell the user the before/after size.
    /// Returns null when it failed (the user has been told why).
    /// </summary>
    internal async Task<PdfOptimizationResult?> ReduceFileSizeToAsync(string outputPath, PdfOptimizationPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return null;

        // The in-memory save happens here, on the UI thread, like every other
        // save of the live document; only the copy is optimized off-thread.
        var saved = document.SaveToBytes();
        var encryption = _documentService.GetReEncryptionOptions();
        var beforeBytes = !string.IsNullOrWhiteSpace(_currentFilePath) && File.Exists(_currentFilePath)
            ? new FileInfo(_currentFilePath).Length
            : saved.LongLength;

        OperationStatus = "Reducing file size…";
        PdfOptimizationResult result;
        try
        {
            var options = PdfOptimizationOptions.ForPreset(preset, PdfImageCodecs.CreateJpegCodec());
            result = await Task.Run(() =>
                PdfDocumentOptimizer.SaveOptimizedCopy(saved, outputPath, options, encryption));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Reduce File Size failed");
            OperationStatus = string.Empty;
            await _dialogService.ShowMessageAsync("Reduce File Size", $"Could not write the smaller copy: {ex.Message}");
            return null;
        }

        OperationStatus = string.Empty;
        _logger.LogInformation(
            "Reduce File Size ({Preset}): {Before} -> {After} bytes",
            preset, beforeBytes, result.OutputSizeBytes);
        await _dialogService.ShowMessageAsync(
            "Reduce File Size",
            DescribeReduceFileSizeResult(outputPath, beforeBytes, result));
        return result;
    }

    internal static string DescribeReduceFileSizeResult(string outputPath, long beforeBytes, PdfOptimizationResult result)
    {
        var text = new StringBuilder();
        var ratio = beforeBytes > 0 ? (double)result.OutputSizeBytes / beforeBytes : 1.0;
        text.Append(CultureInfo.CurrentCulture,
            $"{FormatFileSize(beforeBytes)} → {FormatFileSize(result.OutputSizeBytes)}");
        text.Append(ratio < 1.0
            ? string.Format(CultureInfo.CurrentCulture, " ({0:0}% smaller)", (1.0 - ratio) * 100.0)
            : " (not smaller)");
        text.AppendLine();
        text.AppendLine();
        text.AppendLine(CultureInfo.CurrentCulture, $"Saved to {outputPath}");
        if (result.ImagesDownsampled > 0)
            text.AppendLine(CultureInfo.CurrentCulture, $"{result.ImagesDownsampled} image(s) downsampled.");
        var skipped = result.ImagesSkipped.Values.Sum();
        if (skipped > 0)
            text.AppendLine(CultureInfo.CurrentCulture, $"{skipped} high-resolution image(s) left unchanged.");
        if (ratio >= 1.0)
            text.AppendLine("This document is already stored compactly.");
        foreach (var warning in result.Warnings)
            text.AppendLine(warning);
        return text.ToString().TrimEnd();
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024.0)),
        >= 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", bytes / 1024.0),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} bytes", bytes),
    };

    private static string SuggestReducedFilename(string currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return "document_reduced.pdf";

        var directory = Path.GetDirectoryName(currentFilePath);
        var name = Path.GetFileNameWithoutExtension(currentFilePath);
        var fileName = $"{name}_reduced.pdf";
        return string.IsNullOrWhiteSpace(directory) ? fileName : Path.Combine(directory, fileName);
    }
}
