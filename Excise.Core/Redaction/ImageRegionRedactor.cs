using System;
using Excise.Core.Document;
using Excise.Core.Filters;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Region-level image redaction (#1195): destroy only the samples under the
/// redaction rectangle instead of dropping the whole image <c>Do</c>. The old
/// behaviour deleted an entire full-page scan because a small term overlapped
/// it (the #942 collateral class on the image path).
/// </summary>
/// <remarks>
/// <para><b>Fail-secure by construction.</b> The one gate that decides whether
/// this path runs is the raw-length invariant: <c>DecodedData.Length</c> must
/// equal <c>ceil(Width·components·bpc/8)·Height</c>. The filter decoders
/// (CCITT/JBIG2/JPX) silently return the still-encoded codestream when they
/// cannot decode a stream, so length is the ONLY reliable signal that
/// <c>DecodedData</c> is actually pixels. A codestream never matches, so it
/// falls back to whole-<c>Do</c> removal. Anything unexpected — an <c>/SMask</c>
/// or <c>/Mask</c> (a glyph-shaped alpha channel would show the term through a
/// blacked base), an unknown colour space, a decode throw — also falls back.
/// The caller drops the whole image whenever this returns false, so a wrong
/// answer here can only OVER-redact, never leak.</para>
/// <para>The blackout writes a uniform zero over the sample bytes (rounded
/// OUTWARD to byte boundaries — fail secure). A uniform overwrite carries no
/// information regardless of colour space, so it is security-sufficient; the
/// rendered colour of the patch is the drawn redaction box's job.</para>
/// <para>The redacted image is written as a NEW XObject under a fresh name and
/// this page's <c>Do</c> is remapped to it; the original is left untouched so a
/// shared image on another (un-redacted) page is unaffected. The existing
/// <see cref="ImageRedactor.PruneUnusedImageXObjects"/> then drops the original
/// if this page was its only use.</para>
/// </remarks>
internal static class ImageRegionRedactor
{
    /// <summary>
    /// Attempt to redact only the region of <paramref name="image"/> covered by
    /// <paramref name="area"/>. On success adds a redacted clone to the page's
    /// <c>/XObject</c> resources and returns its name in
    /// <paramref name="newName"/>. Returns false (caller must drop the whole
    /// <c>Do</c>) on any condition it does not fully handle.
    /// </summary>
    public static bool TryRegionRedact(
        PdfPage page,
        PdfStream image,
        double a, double b, double c, double d, double e, double f,
        PdfRectangle area,
        out string newName)
    {
        newName = string.Empty;

        // Leak-vector guard: a mask/soft-mask can carry glyph shapes that would
        // show the term through the blacked base. Region-editing the mask too is
        // a follow-up; for now such an image drops wholesale.
        if (image.GetOptional("SMask") != null || image.GetOptional("Mask") != null)
            return false;

        // Degenerate-area backstop: a near-zero-height (or -width) area would
        // zero only a 1-sample strip, leaving the term readable — a LEAK dressed
        // as a fix. RedactText now passes the full glyph bbox for the image pass
        // (not the thin glyph-match centreline, #1195), so this should not fire
        // on that path; it protects any other caller that hands over a thin rect.
        var guardArea = area.Normalize();
        if (guardArea.Top - guardArea.Bottom < 1.0 || guardArea.Right - guardArea.Left < 1.0)
            return false;

        // Region-edit only filters whose decode→Flate re-encode is
        // pixel-faithful. JBIG2's decoder normalizes its output to PDF 1-bit
        // samples, so it uses the same lossless Flate re-embed path as CCITT.
        // DCT/JPX do not Core-decode to samples at all (they fail the length
        // gate below, but deny explicitly so the boundary is a decision, not
        // an accident).
        var filters = image.Filters;
        if (filters.Count > 0
            && filters[filters.Count - 1] is "DCTDecode" or "JPXDecode")
            return false;

        int width = image.GetInt("Width", 0);
        int height = image.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return false;

        bool imageMask = image.GetBool("ImageMask");
        int bpc = imageMask ? 1 : image.GetInt("BitsPerComponent", 0);
        int components = imageMask ? 1 : ComponentCount(page, image);
        if (bpc <= 0 || components <= 0)
            return false;

        long bytesPerRow = ((long)width * components * bpc + 7) / 8;
        long expected = bytesPerRow * height;

        byte[] pixels;
        try
        {
            pixels = image.DecodedData;
        }
        catch
        {
            return false; // undecodable → caller drops the whole Do
        }
        // THE gate: a length match means DecodedData is pixels, not a codestream.
        if (pixels.LongLength != expected)
            return false;

        // Map the redaction rectangle into image sample space via the inverse of
        // the unit-square→page CTM, rounding OUTWARD (fail secure).
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-9)
            return false;

        var an = area.Normalize();
        double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
        foreach (var (px, py) in new[]
                 {
                     (an.Left, an.Bottom), (an.Right, an.Bottom),
                     (an.Left, an.Top), (an.Right, an.Top),
                 })
        {
            double u = (d * (px - e) - c * (py - f)) / det;
            double v = (-b * (px - e) + a * (py - f)) / det;
            uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
            vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
        }

        // unit (u,v): u is left→right, v is bottom→top. Image row 0 is the TOP.
        int x0 = (int)Math.Floor(Clamp01(uMin) * width);
        int x1 = (int)Math.Ceiling(Clamp01(uMax) * width);
        int y0 = (int)Math.Floor((1.0 - Clamp01(vMax)) * height);
        int y1 = (int)Math.Ceiling((1.0 - Clamp01(vMin)) * height);
        x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
        if (x0 >= x1 || y0 >= y1)
            return false; // no coverage — nothing to do here (caller keeps image)

        // Zero whole bytes spanning the column range (outward = fail secure).
        long byteStart = (long)x0 * components * bpc / 8;
        long byteEnd = ((long)x1 * components * bpc + 7) / 8;
        byteEnd = Math.Min(byteEnd, bytesPerRow);
        for (int y = y0; y < y1; y++)
        {
            long rowOffset = (long)y * bytesPerRow;
            for (long i = rowOffset + byteStart; i < rowOffset + byteEnd; i++)
                pixels[i] = 0;
        }

        // Re-encode the edited samples as Flate and register a fresh XObject.
        var flate = BasicStreamFilters.EncodeFlate(pixels);
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("XObject");
        dict["Subtype"] = new PdfName("Image");
        dict["Width"] = new PdfInteger(width);
        dict["Height"] = new PdfInteger(height);
        CopyIfPresent(image, dict, "ColorSpace");
        CopyIfPresent(image, dict, "Decode");     // sample polarity — NOT DecodeParms
        CopyIfPresent(image, dict, "Intent");
        if (imageMask)
            dict["ImageMask"] = PdfBoolean.True;
        else
            dict["BitsPerComponent"] = new PdfInteger(bpc);
        dict["Filter"] = new PdfName("FlateDecode");
        var redacted = new PdfStream(dict, flate);
        redacted["Length"] = new PdfInteger(flate.Length);
        // Cache the decoded samples so a SECOND redaction area on this same image
        // (a term that repeats on the scan — the common case) sees pixels, not a
        // "stream not decoded" throw that would drop the whole image and reinstate
        // the collateral this class removes. The writer still emits EncodedData
        // (the Flate bytes), so the saved file is unaffected.
        redacted.SetDecodedData(pixels);

        var xobjects = page.Resources?.ResolveDictionary(page.Document, "XObject");
        if (xobjects == null)
            return false;
        newName = FreshName(xobjects);
        // A stream object MUST be indirect (§7.3.8); registering it and storing
        // the reference is what makes it a real stream to other readers — an
        // inline stream in the dict reads as "object is not a stream".
        xobjects[newName] = page.Document.AddIndirectObject(redacted);
        return true;
    }

    private static int ComponentCount(PdfPage page, PdfStream image)
    {
        var csRef = image.GetOptional("ColorSpace");
        if (csRef == null) return -1;
        var cs = page.Document.Resolve(csRef);
        var name = cs switch
        {
            PdfName n => n.Value,
            PdfArray arr when arr.Count > 0 && arr[0] is PdfName n => n.Value,
            _ => null,
        };
        return name switch
        {
            "DeviceGray" or "CalGray" or "G" => 1,
            "DeviceRGB" or "CalRGB" or "RGB" or "Lab" => 3,
            "DeviceCMYK" or "CMYK" => 4,
            "Indexed" or "I" => 1,
            // ICCBased / Separation / DeviceN / Pattern and anything unknown:
            // do not guess. A wrong count fails the length gate anyway, but
            // returning -1 is the explicit fail-secure answer.
            _ => -1,
        };
    }

    private static void CopyIfPresent(PdfStream src, PdfDictionary dst, string key)
    {
        var v = src.GetOptional(key);
        if (v != null) dst[key] = v;
    }

    private static string FreshName(PdfDictionary xobjects)
    {
        for (int i = 0; ; i++)
        {
            var candidate = "ImRdct" + i;
            if (!xobjects.ContainsKey(candidate))
                return candidate;
        }
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
