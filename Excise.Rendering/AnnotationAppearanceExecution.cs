using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Builds the geometry contract for executing one selected annotation appearance.
/// Planning resolves PDF objects but never mutates a canvas.
/// </summary>
internal static class AnnotationAppearanceExecution
{
    public static AnnotationAppearancePlan Plan(
        PdfAnnotation annotation,
        PdfStream appearance,
        PdfDocument document,
        bool antiAlias)
    {
        var clipRect = Normalize(annotation.Rect);
        if (clipRect.Width <= 0 || clipRect.Height <= 0)
        {
            return AnnotationAppearancePlan.Skip(
                $"Annotation /{annotation.Subtype} has a degenerate /Rect; appearance not drawn.");
        }

        var bbox = ResolveArray(appearance, "BBox", document);
        if (!TryNumber(bbox, 0, document, out var x1)
            || !TryNumber(bbox, 1, document, out var y1)
            || !TryNumber(bbox, 2, document, out var x2)
            || !TryNumber(bbox, 3, document, out var y2))
        {
            return AnnotationAppearancePlan.Synthesize(
                $"Annotation /{annotation.Subtype} appearance has no usable /BBox " +
                "(required by §8.10.2); drawing the default appearance instead.");
        }

        var bboxRect = new SKRect(
            (float)Math.Min(x1, x2),
            (float)Math.Min(y1, y2),
            (float)Math.Max(x1, x2),
            (float)Math.Max(y1, y2));
        if (bboxRect.Width <= 0 || bboxRect.Height <= 0)
        {
            return AnnotationAppearancePlan.Synthesize(
                $"Annotation /{annotation.Subtype} appearance /BBox is degenerate; " +
                "drawing the default appearance instead.");
        }

        var formMatrix = ReadMatrix(ResolveArray(appearance, "Matrix", document), document);
        var transformedBounds = TransformBounds(formMatrix, bboxRect);
        if (transformedBounds.Width <= 0 || transformedBounds.Height <= 0)
        {
            return AnnotationAppearancePlan.Skip(
                $"Annotation /{annotation.Subtype} appearance /BBox collapses to zero area " +
                "once /Matrix is applied; nothing drawn.");
        }

        var scaleX = clipRect.Width / transformedBounds.Width;
        var scaleY = clipRect.Height / transformedBounds.Height;
        var fitMatrix = new SKMatrix(
            scaleX,
            0,
            clipRect.Left - transformedBounds.Left * scaleX,
            0,
            scaleY,
            clipRect.Top - transformedBounds.Top * scaleY,
            0,
            0,
            1);

        return AnnotationAppearancePlan.Execute(new AnnotationAppearanceExecutionRequest(
            appearance,
            clipRect,
            fitMatrix,
            antiAlias));
    }

    private static SKRect Normalize(PdfRectangle rect)
        => new(
            (float)Math.Min(rect.Left, rect.Right),
            (float)Math.Min(rect.Bottom, rect.Top),
            (float)Math.Max(rect.Left, rect.Right),
            (float)Math.Max(rect.Bottom, rect.Top));

    private static PdfArray? ResolveArray(
        PdfDictionary dictionary,
        string key,
        PdfDocument document)
    {
        try
        {
            return dictionary.GetOptional(key) is { } value
                ? document.Resolve(value) as PdfArray
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNumber(
        PdfArray? array,
        int index,
        PdfDocument document,
        out double value)
    {
        value = 0;
        if (array == null || index < 0 || index >= array.Count)
            return false;

        try
        {
            return document.Resolve(array[index]).TryGetNumber(out value);
        }
        catch
        {
            return false;
        }
    }

    private static double NumberOrDefault(
        PdfArray? array,
        int index,
        PdfDocument document,
        double defaultValue = 0)
        => TryNumber(array, index, document, out var value) ? value : defaultValue;

    private static SKMatrix ReadMatrix(PdfArray? array, PdfDocument document)
    {
        if (array == null || array.Count < 6)
            return SKMatrix.Identity;

        return new SKMatrix(
            (float)NumberOrDefault(array, 0, document, 1),
            (float)NumberOrDefault(array, 2, document),
            (float)NumberOrDefault(array, 4, document),
            (float)NumberOrDefault(array, 1, document),
            (float)NumberOrDefault(array, 3, document, 1),
            (float)NumberOrDefault(array, 5, document),
            0,
            0,
            1);
    }

    private static SKRect TransformBounds(SKMatrix matrix, SKRect rect)
    {
        var bottomLeft = matrix.MapPoint(new SKPoint(rect.Left, rect.Top));
        var bottomRight = matrix.MapPoint(new SKPoint(rect.Right, rect.Top));
        var topRight = matrix.MapPoint(new SKPoint(rect.Right, rect.Bottom));
        var topLeft = matrix.MapPoint(new SKPoint(rect.Left, rect.Bottom));
        return new SKRect(
            Math.Min(Math.Min(bottomLeft.X, bottomRight.X), Math.Min(topRight.X, topLeft.X)),
            Math.Min(Math.Min(bottomLeft.Y, bottomRight.Y), Math.Min(topRight.Y, topLeft.Y)),
            Math.Max(Math.Max(bottomLeft.X, bottomRight.X), Math.Max(topRight.X, topLeft.X)),
            Math.Max(Math.Max(bottomLeft.Y, bottomRight.Y), Math.Max(topRight.Y, topLeft.Y)));
    }
}

internal readonly record struct AnnotationAppearanceExecutionRequest(
    PdfStream Appearance,
    SKRect ClipRect,
    SKMatrix FitMatrix,
    bool AntiAlias);

internal readonly record struct AnnotationAppearancePlan(
    AnnotationAppearanceDisposition Disposition,
    AnnotationAppearanceExecutionRequest Request,
    string? Diagnostic)
{
    public static AnnotationAppearancePlan Execute(AnnotationAppearanceExecutionRequest request)
        => new(AnnotationAppearanceDisposition.Execute, request, null);

    public static AnnotationAppearancePlan Synthesize(string diagnostic)
        => new(AnnotationAppearanceDisposition.Synthesize, default, diagnostic);

    public static AnnotationAppearancePlan Skip(string diagnostic)
        => new(AnnotationAppearanceDisposition.Skip, default, diagnostic);
}

internal enum AnnotationAppearanceDisposition
{
    Execute,
    Synthesize,
    Skip,
}
