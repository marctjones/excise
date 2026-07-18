using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Result from an external reference renderer subprocess.
/// </summary>
public sealed record ReferenceRenderResult(
    SKBitmap? Bitmap,
    string Status,
    string? ErrorMessage,
    long ElapsedMs);
