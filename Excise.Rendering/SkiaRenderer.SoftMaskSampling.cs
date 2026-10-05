using System.Numerics;
using Excise.Core.Primitives;

namespace Excise.Rendering;

internal partial class RenderContext
{
    // One target-resolution sampling loop for byte and unpacked int sources (#1966).
    // Closed numeric specializations avoid per-pixel delegates/boxing and never copy the source.
    internal static byte[] CreateSoftMaskAlpha<TSample>(
        ReadOnlySpan<TSample> data, int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight, PdfStream maskStream)
        where TSample : struct, IBinaryInteger<TSample>
    {
        var alpha = new byte[targetWidth * targetHeight];
        var dst = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceY = MapTargetToSource(y, targetHeight, sourceHeight);
            var sourceRow = sourceY * sourceWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSource(x, targetWidth, sourceWidth);
                var sourceIndex = sourceRow + sourceX;
                var sample = sourceIndex < data.Length ? int.CreateChecked(data[sourceIndex]) : 0;
                alpha[dst++] = DecodeSoftMaskSample(maskStream, sample, 8);
            }
        }
        return alpha;
    }
}
