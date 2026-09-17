using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1608 — images that are still in the file but not shown: the original
/// object left behind by a replace-rather-than-remove edit, and an original
/// hidden behind a fully transparent soft mask.
///
/// <para><b>Why the covered-image channel does not see these.</b> That one
/// finds an image that IS drawn, with something painted over it. Neither of
/// these is drawn at all — one is unreferenced, the other is drawn and then
/// masked to nothing — so nothing in the content stream reveals them. Both are
/// recovered whole by <c>mutool extract</c>, <c>qpdf --qdf</c>, or any object
/// walker.</para>
///
/// <para><b>Gating, because both shapes occur innocently.</b> Unreferenced
/// objects accumulate benignly in incrementally-updated files, and transparent
/// images are a real design idiom. So an orphan is reported only when it is
/// non-trivial in size AND a same-dimensions image IS referenced — the
/// signature of an edit that swapped one image for another rather than
/// removing it. Without that second condition this would fire on every
/// incremental update in existence, which is the prior-revision channel's
/// subject, not this one.</para>
///
/// <para>Both are <see cref="RecoveryConfidence.PresentOnly"/>: the channel
/// establishes that image data survives and which object holds it. It does not
/// decode it.</para>
/// </summary>
public static class ImageLayerRecovery
{
    /// <summary>Smaller than this in either dimension is an icon or a rule, not content.</summary>
    private const int MinPixelDimension = 16;

    /// <param name="Kind">"orphaned-image" or "masked-image".</param>
    /// <param name="ObjectNumber">The object still holding the pixels.</param>
    public readonly record struct ImageLayerLeak(
        string Kind, int ObjectNumber, int Width, int Height, string Description);

    public static IReadOnlyList<ImageLayerLeak> Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var found = new List<ImageLayerLeak>();
        try
        {
            ScanOrphans(document, found);
            ScanFullyMasked(document, found);
        }
        catch { /* a document whose object graph will not walk reports nothing */ }
        return found;
    }

    private static void ScanOrphans(PdfDocument document, List<ImageLayerLeak> found)
    {
        HashSet<int> reachable;
        List<(int Number, PdfStream Image)> images;
        try
        {
            reachable = document.ComputeReachableObjects();
            images = document.GetAllObjects()
                .Where(o => o.Object is PdfStream s && s.GetNameOrNull("Subtype") == "Image")
                .Select(o => (o.ObjectNumber, (PdfStream)o.Object))
                .ToList();
        }
        catch { return; }

        var referenced = images.Where(i => reachable.Contains(i.Number)).ToList();
        if (referenced.Count == 0) return;   // nothing to have been swapped FOR

        foreach (var (number, image) in images)
        {
            if (reachable.Contains(number)) continue;

            var (width, height) = Dimensions(image);
            if (width < MinPixelDimension || height < MinPixelDimension) continue;

            // The swap signature: a referenced image of the same pixel size.
            // Without it, an orphan is ordinary incremental-update debris and
            // belongs to the prior-revision channel, not here.
            if (!referenced.Any(r => Dimensions(r.Image) == (width, height))) continue;

            found.Add(new ImageLayerLeak(
                "orphaned-image", number, width, height,
                $"image object {number} ({width}x{height}) is in the file but referenced by " +
                "no page — the shape of an edit that swapped an image rather than removing it"));
        }
    }

    private static void ScanFullyMasked(PdfDocument document, List<ImageLayerLeak> found)
    {
        foreach (var (number, _, obj) in document.GetAllObjects())
        {
            if (obj is not PdfStream image || image.GetNameOrNull("Subtype") != "Image") continue;

            var maskObj = image.GetOptional("SMask");
            if (maskObj == null) continue;
            if (document.Resolve(maskObj) is not PdfStream mask) continue;
            if (!IsFullyTransparent(mask)) continue;

            var (width, height) = Dimensions(image);
            if (width < MinPixelDimension || height < MinPixelDimension) continue;

            found.Add(new ImageLayerLeak(
                "masked-image", number, width, height,
                $"image object {number} ({width}x{height}) is drawn under a fully transparent " +
                "/SMask — invisible, and recovered whole by removing the mask"));
        }
    }

    /// <summary>
    /// A soft mask whose every sample is zero makes its image completely
    /// transparent (§11.6.5.3). Decoding is bounded: an undecodable mask is
    /// treated as NOT fully transparent, so the channel stays quiet rather than
    /// guessing — a false positive here would accuse an ordinary design idiom.
    /// </summary>
    private static bool IsFullyTransparent(PdfStream mask)
    {
        byte[] data;
        try { data = mask.DecodedData; }
        catch { return false; }
        if (data.Length == 0) return false;

        // Only the simple 8-bit grey case is judged; anything else is left
        // alone rather than approximated.
        if (mask.GetOptional("BitsPerComponent") is not PdfInteger { Value: 8 }) return false;
        return data.All(b => b == 0);
    }

    private static (int Width, int Height) Dimensions(PdfStream image)
    {
        var w = image.GetOptional("Width") is PdfInteger width ? (int)width.Value : 0;
        var h = image.GetOptional("Height") is PdfInteger height ? (int)height.Value : 0;
        return (w, h);
    }
}
