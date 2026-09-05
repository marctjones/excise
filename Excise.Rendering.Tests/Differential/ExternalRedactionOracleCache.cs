using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Excise.Rendering.Differential;
using SkiaSharp;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Run-local cache for independent, subprocess-backed redaction oracles.
///
/// <para>This cache deliberately has no Excise entry points. In particular it
/// never caches <c>PdfDocument.Open</c>, <c>RedactText</c>, saving, structural
/// inventory, or any result derived from them. A benchmark case must therefore
/// always create and inspect the current Excise output; only repeated answers
/// from mutool, qpdf, and x-ray for byte-identical PDFs are reused.</para>
///
/// <para>Entries are process-local rather than persisted. That keeps an oracle
/// upgrade from silently reusing an old tool's verdict, while still eliminating
/// the dominant duplicate work within a multi-tool benchmark run.</para>
/// </summary>
internal sealed class ExternalRedactionOracleCache
{
    private readonly ConcurrentDictionary<string, Lazy<object?>> _entries = new(StringComparer.Ordinal);

    internal string[]? ExtractAllPages(string pdfPath, int pageCount)
        => Get("mutool-text", pdfPath, pageCount.ToString(),
            () => MutoolTextExtractor.ExtractAllPages(pdfPath, pageCount));

    /// <summary>
    /// The SECOND text oracle, on a different engine (#1372). mutool is MuPDF,
    /// and PyMuPDF — one of the redactors this bench measures — is that same
    /// engine through a Python binding, so a MuPDF-only leak verdict asks MuPDF
    /// to grade MuPDF's own redaction and inherits its blind spots. Poppler is a
    /// different codebase with a different text-merge implementation.
    /// </summary>
    internal string[]? ExtractAllPagesPoppler(string pdfPath, int pageCount)
        => Get("pdftotext", pdfPath, pageCount.ToString(),
            () => PdftotextTextExtractor.ExtractAllPages(pdfPath, pageCount));

    internal QpdfCheckResult? CheckWithQpdf(string pdfPath)
        => Get("qpdf-check", pdfPath, "--check", () =>
        {
            var result = QpdfReferenceTool.Check(pdfPath);
            return result is { } check ? new QpdfCheckResult(check.Success) : null;
        });

    internal XRayBadRedactionDetector.BadRedaction[]? InspectWithXray(string pdfPath)
        => Get("xray-inspect", pdfPath, "v1", () =>
        {
            var findings = XRayBadRedactionDetector.Inspect(pdfPath);
            return findings?.ToArray();
        });

    internal SKBitmap? RenderWithGhostscript(string pdfPath, int pageNumber, int dpi)
    {
        var rendered = Get("ghostscript-render", pdfPath, $"page={pageNumber};dpi={dpi}", () =>
        {
            using var bitmap = GhostscriptReferenceRenderer.RenderPage(pdfPath, pageNumber, dpi);
            if (bitmap == null) return new RenderedPage(null);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new RenderedPage(data.ToArray());
        });
        return rendered?.PngBytes == null ? null : SKBitmap.Decode(rendered.PngBytes);
    }

    /// <summary>
    /// The second RENDER engine (#1372). Ghostscript and Poppler are separate
    /// codebases, so a page both agree on is evidence and a page they disagree
    /// on is a finding rather than a silent single-oracle verdict.
    /// </summary>
    internal SKBitmap? RenderWithPdftocairo(string pdfPath, int pageNumber, int dpi)
    {
        var rendered = Get("pdftocairo-render", pdfPath, $"page={pageNumber};dpi={dpi}", () =>
        {
            using var bitmap = PdftocairoReferenceRenderer.RenderPage(pdfPath, pageNumber, dpi);
            if (bitmap == null) return new RenderedPage(null);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new RenderedPage(data.ToArray());
        });
        return rendered?.PngBytes == null ? null : SKBitmap.Decode(rendered.PngBytes);
    }

    private T? Get<T>(string oracle, string pdfPath, string options, Func<T?> query)
        where T : class
    {
        var key = $"{oracle}|{ContentHash(pdfPath)}|{options}";
        var lazy = _entries.GetOrAdd(key, _ => new Lazy<object?>(
            () => query(), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value as T;
    }

    private string ContentHash(string pdfPath)
        // Deliberately hash on every lookup rather than memoising by filename,
        // size, or mtime. A fast rewrite can preserve those metadata values;
        // the small hash cost is insignificant beside a renderer/OCR process
        // and makes a stale external verdict impossible.
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath)));

    internal sealed record QpdfCheckResult(bool Success);
    private sealed record RenderedPage(byte[]? PngBytes);
}
