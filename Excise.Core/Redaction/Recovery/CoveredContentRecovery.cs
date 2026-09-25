using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — non-text content that survives UNDER a redaction mark: image pixels
/// and vector drawing painted before an opaque object covered them.
///
/// <para><b>Why this channel exists at all.</b> Every other channel reads text.
/// A redaction that draws a box over a photograph, a signature, a chart or a
/// map leaks no text, so a text-only audit reports the page clean — and the
/// original pixels are sitting in the file, one <c>mutool draw</c> away. The
/// leak is complete; only our ability to phrase it as a string is missing.</para>
///
/// <para><b>It reports <see cref="RecoveryConfidence.PresentOnly"/>, and that is
/// the honest class.</b> The channel establishes that content is there and
/// where; it does not decode it into a value. Calling that "certain recovery"
/// would overstate (nothing was read); calling it a candidate would understate
/// (nothing is being guessed — the bytes are present). Turning it back into
/// visible pixels is <c>--restore</c>'s job (#1588), which this channel's
/// locations feed.</para>
///
/// <para><b>Gating.</b> The covered object must be MAJORITY covered by the
/// obstruction. That single rule does the necessary work: a small box over a
/// full-page background image covers a few percent of it and is correctly
/// ignored, while a box sized to the signature it hides covers nearly all of
/// it and is reported. Stroke-only paths count as content too (a signature is
/// strokes, not fills), but the obstruction itself must be a FILL — a stroked
/// outline does not hide anything.</para>
/// </summary>
public static class CoveredContentRecovery
{
    private const double DarkLuminance = 0.45;
    private const double CoveredFraction = 0.5;
    private const double MinSidePt = RecoveryGeometry.MinSidePt;   // #1625: one threshold, not three

    /// <param name="Kind">"image" or "vector" — which <c>Channel</c> the finding gets.</param>
    /// <param name="Covered">The covered content's box, page space.</param>
    /// <param name="Obstruction">The mark that covers it — the link hint for the report.</param>
    public readonly record struct CoveredContent(
        int PageNumber, string Kind, string Description,
        PdfRectangle Covered, PdfRectangle Obstruction);

    public static IReadOnlyList<CoveredContent> Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var found = new List<CoveredContent>();
        for (var p = 1; p <= document.PageCount; p++)
            ScanPage(document.GetPage(p), p, found);
        return found;
    }

    private static void ScanPage(PdfPage page, int pageNumber, List<CoveredContent> found)
    {
        IReadOnlyList<ContentOperator> ops;
        try { ops = page.GetContentStream().Operators; }
        catch { return; }

        var pageArea = Math.Max(1e-6, page.CropBox.Width * page.CropBox.Height);
        var content = new List<(int Index, string Kind, string Description, PdfRectangle Box)>();
        var obstructions = new List<(int Index, PdfRectangle Box)>();

        // #1624: §8.4.2 q/Q save and restore the fill colour. These were bare
        // locals with no q/Q handling at all, so a colour set inside a block
        // applied to every later fill on the page.
        var fillState = new FillColourState(1, 1, 1);

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];

            // q/Q and every colour operator, in one place (#1624).
            if (fillState.Apply(op)) continue;

            switch (op.Name)
            {
                case "Do":
                {
                    if (op.Operands.Count == 0) break;
                    var name = op.GetName(0);
                    if (page.GetXObject(name) is not PdfStream stream) break;
                    if (stream.GetNameOrNull("Subtype") != "Image") break;
                    if (UnitSquare(op) is not { } imageBox) break;
                    content.Add((i, "image", $"image XObject /{name}", imageBox));
                    break;
                }

                case "BI":
                {
                    if (UnitSquare(op) is not { } inlineBox) break;
                    content.Add((i, "image", "inline image", inlineBox));
                    break;
                }

                // Painting operators. A fill dark enough to hide is an
                // obstruction; everything else painted is content that could be
                // hidden. A fill can be both — a dark chart bar covered by a
                // later box is content in its own right — so it is recorded as
                // both and the majority-coverage rule decides.
                case "S":
                case "s":
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                {
                    if (op.BoundingBox is not { } raw) break;
                    var box = raw.Normalize();
                    if (box.Width < MinSidePt || box.Height < MinSidePt) break;

                    var isFill = op.Name is not ("S" or "s");
                    var fill = new Rgb(fillState.Current.R, fillState.Current.G, fillState.Current.B);
                    if (isFill && fillState.Set && Luminance(fill) <= DarkLuminance &&
                        box.Width * box.Height <= 0.9 * pageArea)
                    {
                        obstructions.Add((i, box));
                    }
                    content.Add((i, "vector", $"vector path ({op.Name})", box));
                    break;
                }
            }
        }

        foreach (var (index, kind, description, box) in content)
        {
            foreach (var (obstructionIndex, obstruction) in obstructions)
            {
                // Painted first: not covering. This is also what stops an
                // obstruction being its own victim -- it is recorded as both an
                // obstruction and content at the SAME operator index, and an
                // index cannot precede itself. Comparing rectangles instead
                // would be wrong: a box drawn exactly over the image it hides
                // is the normal shape of an image redaction, not self-overlap.
                if (obstructionIndex <= index) continue;
                if (RecoveryReportBuilder.OverlapFraction(box, obstruction) < CoveredFraction) continue;
                found.Add(new CoveredContent(pageNumber, kind, description, box, obstruction));
                break;   // one report per covered object: the first mark that hides it
            }
        }
    }

    /// <summary>
    /// The page-space box an image fills: §8.9.5 maps every image onto the unit
    /// square, so the CTM in force at the <c>Do</c> is the image's geometry.
    /// </summary>
    private static PdfRectangle? UnitSquare(ContentOperator op)
    {
        if (op.GraphicsTransform is not { } m) return op.BoundingBox?.Normalize();
        var box = m.UnitSquareBounds();
        return box.Width < MinSidePt || box.Height < MinSidePt ? null : box;
    }

    private static double Luminance(Rgb c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private readonly record struct Rgb(double R, double G, double B);
}
