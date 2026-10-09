using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// ISO 32000-2 12.5.5 Algorithm 8.1, steps a and b: where an annotation's appearance stream lands
/// on the page. The appearance's <c>/BBox</c> is transformed by its <c>/Matrix</c> (identity when
/// absent, Table 93) and the bounding box of the result is mapped onto the annotation's
/// <c>/Rect</c> by a scale and a translation, the matrix A. The stream is then painted with
/// <c>Matrix × A</c> as its form-to-page transform. One copy of this arithmetic serves both the
/// flatten that stamps an appearance into the page (<see cref="AcroFormFlattener"/>) and the
/// extractor that reads its text where it is drawn (#2039).
/// </summary>
internal static class AppearanceMapping
{
    /// <summary>
    /// The appearance's <c>/Matrix</c> and the matrix A (as scale and offset) that maps its
    /// transformed <c>/BBox</c> onto <paramref name="rect"/>. False when the appearance has no
    /// readable <c>/BBox</c> (it is required, Table 93) or either box is degenerate: nothing is
    /// drawn then.
    /// </summary>
    public static bool TryMap(
        PdfDocument document, PdfStream appearance, PdfRectangle rect,
        out double[] formMatrix, out (double Sx, double Sy, double Ox, double Oy) a)
    {
        formMatrix = [1, 0, 0, 1, 0, 0];
        a = default;
        if (!TryGetNumbers(document, appearance.ResolveArray(document, "BBox"), 4, out var bbox))
            return false;
        if (TryGetNumbers(document, appearance.ResolveArray(document, "Matrix"), 6, out var m))
            formMatrix = m;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[2], bbox[3]), (bbox[0], bbox[3]) })
        {
            var tx = formMatrix[0] * x + formMatrix[2] * y + formMatrix[4];
            var ty = formMatrix[1] * x + formMatrix[3] * y + formMatrix[5];
            minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
            minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
        }

        var bw = maxX - minX;
        var bh = maxY - minY;
        rect = rect.Normalize();
        if (bw < 1e-6 || bh < 1e-6 || rect.Width < 1e-6 || rect.Height < 1e-6)
            return false;

        var sx = rect.Width / bw;
        var sy = rect.Height / bh;
        a = (sx, sy, rect.Left - minX * sx, rect.Bottom - minY * sy);
        return true;
    }

    /// <summary>
    /// The appearance's whole form-to-page transform, <c>Matrix × A</c> (row vectors, the order a
    /// <c>cm</c> of A followed by the form's own <c>/Matrix</c> produces), as the six numbers of a
    /// PDF matrix. False where <see cref="TryMap"/> is.
    /// </summary>
    public static bool TryFormToPage(PdfDocument document, PdfStream appearance, PdfRectangle rect, out double[] matrix)
    {
        matrix = [1, 0, 0, 1, 0, 0];
        if (!TryMap(document, appearance, rect, out var m, out var a))
            return false;
        matrix =
        [
            m[0] * a.Sx, m[1] * a.Sy,
            m[2] * a.Sx, m[3] * a.Sy,
            m[4] * a.Sx + a.Ox, m[5] * a.Sy + a.Oy,
        ];
        return true;
    }

    internal static bool TryGetNumbers(PdfDocument document, PdfArray? array, int count, out double[] values)
    {
        values = new double[count];
        if (array == null || array.Count < count)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (document.Resolve(array[i]) is not PdfObject obj || !obj.TryGetNumber(out var n))
                return false;
            values[i] = n;
        }
        return true;
    }
}
