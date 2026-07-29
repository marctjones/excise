using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Maps a VISUAL (as-displayed, post-rotation) rectangle of a page to the CONTENT
/// (unrotated MediaBox/CropBox) clip rectangle that <see cref="SkiaRenderer"/>
/// expects in <see cref="RenderOptions.ClipRect"/> (#846 tile-clip).
///
/// The renderer's clip is applied in CONTENT space: its matrix maps content →
/// device *applying* /Rotate, so the caller must hand it a content-space rect.
/// The continuous-view tile pipeline, however, knows only the visible band in
/// VISUAL space (the viewport shows the rotated page). For 0°/180° visual and
/// content axes line up; for 90°/270° the x/y axes SWAP, so a band computed as if
/// visual==content clips the wrong region and the top of a rotated page is cut off.
///
/// This is the exact inverse of the per-rotation matrix in
/// <see cref="SkiaRenderer.RenderPage(PdfPage, RenderOptions, System.Threading.CancellationToken)"/>.
/// Visual space here is points, top-left origin, Y down, sized VisualWidth×VisualHeight.
/// Content space is PDF points, bottom-left origin, over the content box [L,B,R,T].
/// </summary>
public static class ContinuousTileClip
{
    /// <summary>
    /// Content-space clip for a visual band. <paramref name="contentBox"/> is the
    /// page's effective render box (MediaBox∩CropBox), already Normalize()d.
    /// </summary>
    public static SKRect VisualBandToContentClip(
        int rotation, PdfRectangle contentBox,
        double vx, double vy, double vw, double vh)
    {
        double L = contentBox.Left, B = contentBox.Bottom, R = contentBox.Right, T = contentBox.Top;

        // Content-space extents, derived by inverting each rotation's content->device map.
        double cxMin, cxMax, cyMin, cyMax;
        switch (((rotation % 360) + 360) % 360)
        {
            case 90:
                // visualX = cy - B ; visualY = cx - L   =>  cx = L + vy ; cy = B + vx
                cxMin = L + vy; cxMax = L + vy + vh;
                cyMin = B + vx; cyMax = B + vx + vw;
                break;
            case 180:
                // visualX = R - cx ; visualY = cy - B   =>  cx = R - vx ; cy = B + vy
                cxMin = R - vx - vw; cxMax = R - vx;
                cyMin = B + vy; cyMax = B + vy + vh;
                break;
            case 270:
                // visualX = T - cy ; visualY = R - cx   =>  cx = R - vy ; cy = T - vx
                cxMin = R - vy - vh; cxMax = R - vy;
                cyMin = T - vx - vw; cyMax = T - vx;
                break;
            default: // 0
                // visualX = cx - L ; visualY = T - cy   =>  cx = L + vx ; cy = T - vy
                cxMin = L + vx; cxMax = L + vx + vw;
                cyMin = T - vy - vh; cyMax = T - vy;
                break;
        }

        // Clamp to the content box and return with top<bottom (SKRect convention).
        cxMin = System.Math.Max(cxMin, L); cxMax = System.Math.Min(cxMax, R);
        cyMin = System.Math.Max(cyMin, B); cyMax = System.Math.Min(cyMax, T);
        return new SKRect((float)cxMin, (float)cyMin, (float)cxMax, (float)cyMax);
    }
}
