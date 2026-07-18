namespace Excise.Core.ColorSpaces;

/// <summary>
/// Central color-conversion boundary for renderer-preview RGB values.
/// </summary>
internal static class PdfColorConverter
{
    internal enum CmykPolicy
    {
        ProcessScreenPreview,
        ReferenceFormula
    }

    public static (double R, double G, double B) CmykToRgb(
        double c,
        double m,
        double y,
        double k,
        CmykPolicy policy)
        => policy switch
        {
            CmykPolicy.ReferenceFormula => CmykReferenceFormulaToRgb(c, m, y, k),
            _ => DeviceCmykProcessScreenPreviewToRgb(c, m, y, k)
        };

    public static (double R, double G, double B) DeviceCmykProcessScreenPreviewToRgb(
        double c,
        double m,
        double y,
        double k)
    {
        c = Math.Clamp(c, 0, 1);
        m = Math.Clamp(m, 0, 1);
        y = Math.Clamp(y, 0, 1);
        k = Math.Clamp(k, 0, 1);

        // DeviceCMYK is an uncalibrated, output-device colour space. The
        // simple PDF reference conversion maps process magenta to electric RGB
        // magenta (#ff00ff), but common screen PDF renderers use process-print
        // preview anchors close to SWOP/Generic CMYK: C=(0,174,239),
        // M=(236,0,140), Y=(255,242,0), K=(35,31,32). Use that deterministic
        // preview model for raw DeviceCMYK. ICCBased/default-CMYK proxies use
        // the reference formula until real ICC transforms exist.
        var r = 1
            - c
            - 0.0745098039 * m
            - 0.8627450980 * k
            + 0.2000000000 * c * m
            + 0.2000000000 * c * y
            + 0.1500000000 * c * k;
        var g = 1
            - 0.3176470588 * c
            - m
            - 0.0509803922 * y
            - 0.8784313725 * k
            + 0.1500000000 * c * y
            + 0.1000000000 * c * k
            + 0.2500000000 * m * y
            + 0.1500000000 * m * k;
        var b = 1
            - 0.0627450980 * c
            - 0.4509803922 * m
            - y
            - 0.8745098039 * k
            + 0.4650000000 * c * y
            + 0.0800000000 * c * m
            + 0.6500000000 * m * y
            + 0.2000000000 * m * k
            + 0.4000000000 * y * k;

        return (
            Math.Clamp(r, 0, 1),
            Math.Clamp(g, 0, 1),
            Math.Clamp(b, 0, 1));
    }

    public static (double R, double G, double B) CmykReferenceFormulaToRgb(
        double c,
        double m,
        double y,
        double k)
    {
        c = Math.Clamp(c, 0, 1);
        m = Math.Clamp(m, 0, 1);
        y = Math.Clamp(y, 0, 1);
        k = Math.Clamp(k, 0, 1);

        return (
            1 - Math.Min(1, c + k),
            1 - Math.Min(1, m + k),
            1 - Math.Min(1, y + k));
    }
}
