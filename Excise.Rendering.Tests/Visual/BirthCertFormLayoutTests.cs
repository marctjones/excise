using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Regression tests for the form-layout bug fixed in commit 30e1c9c.
/// Earlier the renderer used the system fallback font's MeasureText
/// for cursor advance, which compounded per-glyph drift into mid-word
/// gaps in PDFs (Word-derived government forms etc.) that lean on
/// TJ-array kerning and Tw to do column alignment. The fix routes
/// non-embedded fonts through the PDF's /Widths array.
/// </summary>
/// <remarks>
/// We assert a structural property — there should be no extremely
/// wide whitespace gaps inside the form's labeled text rows — rather
/// than pixel-comparing against a baseline. Pixel comparison would
/// be flaky across machines with different fallback fonts; the gap-
/// width property survives small metric variations between fallbacks.
/// </remarks>
public class BirthCertFormLayoutTests
{
    private const string BirthCertPath =
        "../../../../Excise.App.Tests/Resources/sample-pdfs/birth-certificate-request-scrambled.pdf";

    [Fact]
    public void BirthCertPage1_PleasePrintRow_KeepsDoNotMailCashTogether()
    {
        if (!File.Exists(BirthCertPath))
            return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(BirthCertPath));
        var page = doc.GetPage(1);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });
        VisualAssertions.SavePng(bmp, "/tmp/birth-cert-regression-render.png");

        // The "PLEASE PRINT … DO NOT MAIL CASH" line is a continuous
        // sentence — only one large legitimate gap (between "PRINT" and
        // "DO", filled by an underline rule). Pre-fix, the line had
        // multiple ~80px gaps from the broken kerning ("D"|"O NOT" and
        // similar). Post-fix, exactly one large gap remains.
        var blobs = FindTextBlobs(bmp, yStart: 85, yEnd: 105, maxIntraBlobGapPx: 20);

        // After "PRINT" + underline gap, "DO NOT MAIL CASH" should
        // present as a SINGLE blob (or two, with normal word spacing).
        // Pre-fix layouts produced 5+ blobs in this row; we set the
        // threshold at 4 to leave headroom while still catching the bug.
        blobs.Count.Should().BeLessThanOrEqualTo(
            4,
            $"row should split into PLEASE PRINT, the underline, DO NOT MAIL CASH " +
            $"and a trailing rule — got {blobs.Count} blobs at " +
            $"{string.Join(", ", blobs)}. Pre-fix layouts had 5+ blobs from broken kerning. " +
            "See /tmp/birth-cert-regression-render.png for the full render.");
    }

    /// <summary>
    /// Group columns that contain any dark pixel into "blobs" — adjacent
    /// dark columns separated by white runs no longer than
    /// <paramref name="maxIntraBlobGapPx"/> are treated as one blob.
    /// Returns the list of blob (start, end) ranges.
    /// </summary>
    private static System.Collections.Generic.List<(int Start, int End)> FindTextBlobs(
        SKBitmap bmp, int yStart, int yEnd, int maxIntraBlobGapPx)
    {
        var blobs = new System.Collections.Generic.List<(int Start, int End)>();
        int? blobStart = null;
        int? blobEnd = null;
        int whiteRun = 0;

        for (int x = 0; x < bmp.Width; x++)
        {
            bool dark = false;
            for (int y = yStart; y < yEnd && y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) { dark = true; break; }
            }

            if (dark)
            {
                if (blobStart == null) blobStart = x;
                blobEnd = x;
                whiteRun = 0;
            }
            else if (blobStart != null)
            {
                whiteRun++;
                if (whiteRun > maxIntraBlobGapPx)
                {
                    blobs.Add((blobStart!.Value, blobEnd!.Value));
                    blobStart = null;
                    blobEnd = null;
                    whiteRun = 0;
                }
            }
        }

        if (blobStart != null) blobs.Add((blobStart.Value, blobEnd!.Value));
        return blobs;
    }

}
