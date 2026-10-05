namespace Excise.Rendering;

internal partial class RenderContext
{
    // Shared legacy clamp for compressed images and soft masks only (#1969).
    // Raw-sample device sizing deliberately bypasses this policy (#1403).
    internal static (int Width, int Height) ClampBudgetedImageTargetSize(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= MaxExpandedSoftMaskPixels)
            return (width, height);

        var scale = Math.Sqrt(MaxExpandedSoftMaskPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }
}
