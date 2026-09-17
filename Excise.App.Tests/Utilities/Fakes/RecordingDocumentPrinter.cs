using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Excise.App.Services.Printing;

namespace Excise.App.Tests.Utilities.Fakes;

/// <summary>
/// In-memory <see cref="IDocumentPrinter"/> (#1545). Records every request and
/// SNAPSHOTS the print copy's bytes while the print is "running": the workflow
/// deletes the file as soon as the printer returns, so a test that looked at
/// the path afterwards would find nothing — which is the behaviour under test,
/// not a reason to lose the evidence.
/// </summary>
/// <remarks>
/// Defaults to a supported printer whose user cancels the sheet. That is what
/// the factory hands every view model, so a headless test that executes
/// PrintCommand exercises the real temp-copy path and never reaches AppKit.
/// </remarks>
internal sealed class RecordingDocumentPrinter : IDocumentPrinter
{
    public bool IsSupported { get; set; } = true;

    public string UnsupportedReason { get; set; } = UnsupportedDocumentPrinter.DefaultReason;

    /// <summary>What the next print returns. Ignored when <see cref="ThrowOnPrint"/> is set.</summary>
    public DocumentPrintResult NextResult { get; set; } = DocumentPrintResult.Cancelled;

    public Exception? ThrowOnPrint { get; set; }

    /// <summary>Runs against the copy's path while it exists.</summary>
    public Action<string>? OnPrint { get; set; }

    public List<DocumentPrintRequest> Requests { get; } = new();

    /// <summary>The print copy's bytes, one per request, captured during the print.</summary>
    public List<byte[]> CopyBytes { get; } = new();

    /// <summary>Whether the print copy existed when the printer was called.</summary>
    public List<bool> CopyExistedDuringPrint { get; } = new();

    public Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request)
    {
        Requests.Add(request);
        var exists = File.Exists(request.PdfPath);
        CopyExistedDuringPrint.Add(exists);
        CopyBytes.Add(exists ? File.ReadAllBytes(request.PdfPath) : Array.Empty<byte>());
        if (exists)
            OnPrint?.Invoke(request.PdfPath);
        if (ThrowOnPrint != null)
            throw ThrowOnPrint;
        return Task.FromResult(NextResult);
    }
}
