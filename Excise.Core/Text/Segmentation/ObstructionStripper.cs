using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Returns a copy of a page's content stream with opaque overlay
/// operations removed. Inverse of redaction: instead of suppressing
/// content, this exposes content that an overlay was hiding.
/// </summary>
/// <remarks>
/// <para>Used by differential-OCR auditing: render the page once
/// normally, render it again with obstructions stripped, OCR both,
/// and the diff is text the document was successfully hiding from
/// the rendered view.</para>
/// <para>What "opaque overlay" means here: a filled-path painting op
/// (<c>f</c>/<c>F</c>/<c>f*</c>/<c>B</c>/<c>B*</c>/<c>b</c>/<c>b*</c>)
/// preceded by a non-white fill color, plus the path-construction ops
/// that built its path. Image <c>Do</c> invocations also count when
/// the XObject's <c>/Subtype</c> is <c>/Image</c>.</para>
/// </remarks>
public static class ObstructionStripper
{
    /// <summary>
    /// Mutate <paramref name="page"/>'s content stream so that opaque
    /// overlays are removed. Original text and other non-obstructing
    /// drawing operations are preserved.
    /// </summary>
    public static void StripObstructions(PdfPage page)
    {
        if (page == null) throw new System.ArgumentNullException(nameof(page));
        var content = page.GetContentStream();
        if (content.Operators.Count == 0) return;

        var newOps = new List<ContentOperator>(content.Operators.Count);
        var pendingPath = new List<int>(); // indices into the input stream
        bool fillIsObstructive = false;

        for (int i = 0; i < content.Operators.Count; i++)
        {
            var op = content.Operators[i];
            switch (op.Name)
            {
                // Fill color setters — track whether the next fill is dark
                // enough to count as obstructive. White / near-white skipped.
                case "rg":
                    if (op.Operands.Count >= 3)
                        fillIsObstructive = !IsNearlyWhite(
                            op.GetNumber(0), op.GetNumber(1), op.GetNumber(2));
                    newOps.Add(op);
                    break;
                case "g":
                    if (op.Operands.Count >= 1)
                    {
                        var v = op.GetNumber(0);
                        fillIsObstructive = !IsNearlyWhite(v, v, v);
                    }
                    newOps.Add(op);
                    break;
                case "k":
                    if (op.Operands.Count >= 4)
                    {
                        double c = op.GetNumber(0), mg = op.GetNumber(1),
                               y = op.GetNumber(2), kk = op.GetNumber(3);
                        // Quick CMYK→RGB approx for the screening test only.
                        double r = (1 - c) * (1 - kk);
                        double gn = (1 - mg) * (1 - kk);
                        double b = (1 - y) * (1 - kk);
                        fillIsObstructive = !IsNearlyWhite(r, gn, b);
                    }
                    newOps.Add(op);
                    break;

                // Path construction — buffer until we see a paint op.
                case "m":
                case "l":
                case "c":
                case "v":
                case "y":
                case "h":
                case "re":
                    pendingPath.Add(newOps.Count);
                    newOps.Add(op);
                    break;

                // Fill (and fill+stroke) — strip if obstructive AND there is a path to paint.
                // A fill with no preceding path construction has no visible effect, so keep it.
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                    if (fillIsObstructive && pendingPath.Count > 0)
                    {
                        // Drop the buffered path ops + this paint op.
                        for (int k = pendingPath.Count - 1; k >= 0; k--)
                            newOps.RemoveAt(pendingPath[k]);
                        // ...and the paint op itself: simply don't emit it.
                    }
                    else
                    {
                        newOps.Add(op);
                    }
                    pendingPath.Clear();
                    break;

                // Stroke / no-op — don't hide text. Emit + reset.
                case "S":
                case "s":
                case "n":
                    newOps.Add(op);
                    pendingPath.Clear();
                    break;

                // Image XObjects pass through unchanged. In scanned PDFs
                // and similar pixels-as-content layouts, the image IS the
                // underlying content we want to OCR — stripping it would
                // leave nothing to compare against. Image-on-top-of-text
                // hiding is caught by the structural HiddenTextDetector
                // (which sees the text Tj op directly).
                default:
                    newOps.Add(op);
                    break;
            }
        }

        page.SetContentStream(new ContentStream(newOps));
    }

    private static bool IsNearlyWhite(double r, double g, double b)
        => r >= 0.95 && g >= 0.95 && b >= 0.95;
}
