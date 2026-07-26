using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Core.Editing;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;
using PdfRectangle = Excise.Core.Document.PdfRectangle;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #780: wrap parity between what the user sees in the editor TextBox
/// (Avalonia's <see cref="TextWrapping.Wrap"/> at the box width) and the
/// flattened PDF output (Skia + base-14 metrics inside
/// <see cref="PdfTypewriterTextApplier"/> → DrawText). The two use DIFFERENT
/// layout engines and DIFFERENT fonts (the editor TextBox is the default UI
/// typeface; the flattened text is Helvetica AFM), so this is deliberately a
/// robust parity check (±1 line), not brittle pixel matching. It exists to
/// catch a GROSS divergence — text the user saw on 3 lines baking in on 1, or
/// words dropped — not sub-line differences.
/// </summary>
[Collection("AvaloniaTests")]
public class TypewriterWrapParityTests
{
    // A block wide enough to wrap several times at the chosen box width, and
    // distinct words so reading-order preservation is meaningfully asserted.
    private static readonly string[] Words =
    {
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
        "quebec", "romeo", "sierra", "tango",
    };

    private const double FontSizePt = 12.0;
    private const double LineSpacing = 1.2;
    private const double ViewerUnitsPerPoint = 120.0 / 72.0; // DefaultRenderDpi / 72
    private const double EditorPaddingDip = 8.0;             // TextBox Padding 4 + 4

    [FixedAvaloniaFact]
    public async Task EditorWrap_AndFlattenedWrap_AgreeWithinOneLine_AndPreserveWordsInOrder()
    {
        var text = string.Join(' ', Words);

        // A box 180pt wide, 400pt tall — tall enough that DrawText never clips a
        // wrapped line into overflow (its vertical-room guard drops lines that
        // fall below Bottom), so line counts reflect wrapping only.
        var bounds = new PdfRectangle(40, 60, 220, 460); // w=180, h=400, on a 300x500 page

        // ---- On-screen side: the editor TextBox layout the user sees ----
        // Same width/font-size/wrapping the editor applies (font size and box
        // width both scale by ViewerUnitsPerPoint; the TextBox loses 8 DIP to
        // padding). Measured with Typeface.Default — the typeface the TextBox
        // actually uses (no FontFamily is set on it).
        int screenLines = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var boxWidthDip = bounds.Width * ViewerUnitsPerPoint;
            var fontSizeDip = FontSizePt * ViewerUnitsPerPoint;
            var layout = new TextLayout(
                text,
                Typeface.Default,
                fontSize: fontSizeDip,
                foreground: Brushes.Black,
                textAlignment: TextAlignment.Left,
                textWrapping: TextWrapping.Wrap,
                maxWidth: boxWidthDip - EditorPaddingDip);
            screenLines = layout.TextLines.Count;
        });

        // ---- Flattened side: apply → save → reopen → extract ----
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.TypewriterWrap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "wrapped.pdf");

        try
        {
            using (var doc = PdfCoreDocument.CreateNew())
            {
                doc.Pages.AddBlank(300, 500);
                var op = PdfTypewriterTextOperation.Create(
                    1, bounds, text,
                    new PdfTypewriterTextStyle(fontSize: FontSizePt, lineSpacing: LineSpacing));
                PdfTypewriterTextApplier.Apply(doc, op);
                doc.Save(outputPath);
            }

            using var saved = PdfCoreDocument.Open(outputPath);
            var page = saved.GetPage(1);
            var letters = page.Letters;

            letters.Should().NotBeEmpty("the flattened typewriter text must be in the content stream");

            // Group letter baselines into lines (bands ~half a lineHeight apart;
            // lines are 14.4pt apart so 7pt cleanly separates them).
            var bands = new System.Collections.Generic.List<double>();
            foreach (var y in letters.Select(l => l.StartY).OrderByDescending(v => v))
            {
                if (!bands.Any(b => Math.Abs(b - y) < 7.0))
                    bands.Add(y);
            }
            int flattenedLines = bands.Count;

            // Words in reading order: top-of-page first (higher PDF Y), then L→R.
            var flattenedWords = page.GetWords()
                .Where(w => w.Letters.Count > 0)
                .OrderByDescending(w => Math.Round(w.Letters[0].StartY / 7.0))
                .ThenBy(w => w.Letters[0].StartX)
                .Select(w => w.Text)
                .ToList();

            // (a) Both engines actually wrapped.
            screenLines.Should().BeGreaterThan(1, "the on-screen editor must wrap this block");
            flattenedLines.Should().BeGreaterThan(1, "the flattened output must wrap this block");

            // (b) Every word survives, in reading order.
            flattenedWords.Should().Equal(Words,
                "flattening must preserve every word in reading order (screen lines={0}, flattened lines={1})",
                screenLines, flattenedLines);

            // (c) Line counts agree within one line. A larger divergence is a
            // genuine fidelity bug, not a tolerance to loosen.
            Math.Abs(screenLines - flattenedLines).Should().BeLessThanOrEqualTo(1,
                "editor wrap ({0} lines) and flattened wrap ({1} lines) must agree within ±1",
                screenLines, flattenedLines);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
