using SkiaSharp;

namespace Excise.Rendering.Shadings;

internal static class PdfMeshPatchBuilder
{
    public static IReadOnlyList<SKPoint> ResolveCanonicalPatchPoints(
        IReadOnlyList<SKPoint> newPoints,
        MeshPatch? previous,
        int flag,
        bool tensorPatch)
        => tensorPatch
            ? ResolveCanonicalTensorPatchPoints(newPoints, previous, flag)
            : ResolveCanonicalCoonsPatchPoints(newPoints, previous, flag);

    public static SKColor[] ResolveCanonicalPatchColors(
        IReadOnlyList<SKColor> newColors,
        MeshPatch? previous,
        int flag)
    {
        // PDF Type 6/7 patch streams store corner colours as c00, c03, c33,
        // c30. The renderer's bilinear helper expects c00, c30, c33, c03.
        if (newColors.Count >= 4)
            return new[] { newColors[0], newColors[3], newColors[2], newColors[1] };

        if (previous == null)
            return new[] { newColors[0], newColors[^1], newColors[^1], newColors[0] };

        return flag switch
        {
            1 => new[] { previous.Colors[3], newColors[^1], newColors[0], previous.Colors[2] },
            2 => new[] { previous.Colors[2], newColors[^1], newColors[0], previous.Colors[1] },
            3 => new[] { previous.Colors[1], newColors[^1], newColors[0], previous.Colors[0] },
            _ => new[] { newColors[0], newColors[^1], newColors[^1], newColors[0] }
        };
    }

    private static IReadOnlyList<SKPoint> ResolveCanonicalTensorPatchPoints(
        IReadOnlyList<SKPoint> newPoints,
        MeshPatch? previous,
        int flag)
    {
        var points = new SKPoint[16];
        switch (flag)
        {
            case 0 when newPoints.Count >= 16:
                FillTensorFlag0(points, newPoints);
                break;
            case 1 when previous != null && newPoints.Count >= 12:
                FillPatchFlag1(points, previous.Points, newPoints, hasInteriorControls: true);
                break;
            case 2 when previous != null && newPoints.Count >= 12:
                FillPatchFlag2(points, previous.Points, newPoints, hasInteriorControls: true);
                break;
            case 3 when previous != null && newPoints.Count >= 12:
                FillPatchFlag3(points, previous.Points, newPoints, hasInteriorControls: true);
                break;
            default:
                return newPoints;
        }

        return points;
    }

    private static IReadOnlyList<SKPoint> ResolveCanonicalCoonsPatchPoints(
        IReadOnlyList<SKPoint> newPoints,
        MeshPatch? previous,
        int flag)
    {
        var points = new SKPoint[16];
        switch (flag)
        {
            case 0 when newPoints.Count >= 12:
                FillCoonsFlag0(points, newPoints);
                break;
            case 1 when previous != null && newPoints.Count >= 8:
                FillPatchFlag1(points, previous.Points, newPoints, hasInteriorControls: false);
                break;
            case 2 when previous != null && newPoints.Count >= 8:
                FillPatchFlag2(points, previous.Points, newPoints, hasInteriorControls: false);
                break;
            case 3 when previous != null && newPoints.Count >= 8:
                FillPatchFlag3(points, previous.Points, newPoints, hasInteriorControls: false);
                break;
            default:
                return newPoints;
        }

        FillCoonsInteriorControls(points);
        return points;
    }

    private static void FillTensorFlag0(SKPoint[] points, IReadOnlyList<SKPoint> p)
    {
        points[0] = p[0];
        points[1] = p[11];
        points[2] = p[10];
        points[3] = p[9];
        points[4] = p[1];
        points[5] = p[12];
        points[6] = p[15];
        points[7] = p[8];
        points[8] = p[2];
        points[9] = p[13];
        points[10] = p[14];
        points[11] = p[7];
        points[12] = p[3];
        points[13] = p[4];
        points[14] = p[5];
        points[15] = p[6];
    }

    private static void FillCoonsFlag0(SKPoint[] points, IReadOnlyList<SKPoint> p)
    {
        points[0] = p[0];
        points[1] = p[11];
        points[2] = p[10];
        points[3] = p[9];
        points[4] = p[1];
        points[7] = p[8];
        points[8] = p[2];
        points[11] = p[7];
        points[12] = p[3];
        points[13] = p[4];
        points[14] = p[5];
        points[15] = p[6];
    }

    private static void FillPatchFlag1(
        SKPoint[] points,
        IReadOnlyList<SKPoint> previous,
        IReadOnlyList<SKPoint> p,
        bool hasInteriorControls)
    {
        points[0] = previous[12];
        points[4] = previous[13];
        points[8] = previous[14];
        points[12] = previous[15];
        FillPatchContinuationPoints(points, p, hasInteriorControls);
    }

    private static void FillPatchFlag2(
        SKPoint[] points,
        IReadOnlyList<SKPoint> previous,
        IReadOnlyList<SKPoint> p,
        bool hasInteriorControls)
    {
        points[0] = previous[15];
        points[4] = previous[11];
        points[8] = previous[7];
        points[12] = previous[3];
        FillPatchContinuationPoints(points, p, hasInteriorControls);
    }

    private static void FillPatchFlag3(
        SKPoint[] points,
        IReadOnlyList<SKPoint> previous,
        IReadOnlyList<SKPoint> p,
        bool hasInteriorControls)
    {
        points[0] = previous[3];
        points[4] = previous[2];
        points[8] = previous[1];
        points[12] = previous[0];
        FillPatchContinuationPoints(points, p, hasInteriorControls);
    }

    private static void FillPatchContinuationPoints(
        SKPoint[] points,
        IReadOnlyList<SKPoint> p,
        bool hasInteriorControls)
    {
        points[1] = p[7];
        points[2] = p[6];
        points[3] = p[5];
        points[7] = p[4];
        points[11] = p[3];
        points[13] = p[0];
        points[14] = p[1];
        points[15] = p[2];

        if (!hasInteriorControls)
            return;

        points[5] = p[8];
        points[6] = p[11];
        points[9] = p[9];
        points[10] = p[10];
    }

    private static void FillCoonsInteriorControls(SKPoint[] points)
    {
        points[5] = new SKPoint(
            (float)(((-4 * points[0].X) - points[15].X +
                (6 * (points[4].X + points[1].X)) -
                (2 * (points[12].X + points[3].X)) +
                (3 * (points[13].X + points[7].X))) / 9),
            (float)(((-4 * points[0].Y) - points[15].Y +
                (6 * (points[4].Y + points[1].Y)) -
                (2 * (points[12].Y + points[3].Y)) +
                (3 * (points[13].Y + points[7].Y))) / 9));
        points[6] = new SKPoint(
            (float)(((-4 * points[3].X) - points[12].X +
                (6 * (points[2].X + points[7].X)) -
                (2 * (points[0].X + points[15].X)) +
                (3 * (points[4].X + points[14].X))) / 9),
            (float)(((-4 * points[3].Y) - points[12].Y +
                (6 * (points[2].Y + points[7].Y)) -
                (2 * (points[0].Y + points[15].Y)) +
                (3 * (points[4].Y + points[14].Y))) / 9));
        points[9] = new SKPoint(
            (float)(((-4 * points[12].X) - points[3].X +
                (6 * (points[8].X + points[13].X)) -
                (2 * (points[0].X + points[15].X)) +
                (3 * (points[11].X + points[1].X))) / 9),
            (float)(((-4 * points[12].Y) - points[3].Y +
                (6 * (points[8].Y + points[13].Y)) -
                (2 * (points[0].Y + points[15].Y)) +
                (3 * (points[11].Y + points[1].Y))) / 9));
        points[10] = new SKPoint(
            (float)(((-4 * points[15].X) - points[0].X +
                (6 * (points[11].X + points[14].X)) -
                (2 * (points[12].X + points[3].X)) +
                (3 * (points[2].X + points[8].X))) / 9),
            (float)(((-4 * points[15].Y) - points[0].Y +
                (6 * (points[11].Y + points[14].Y)) -
                (2 * (points[12].Y + points[3].Y)) +
                (3 * (points[2].Y + points[8].Y))) / 9));
    }
}
