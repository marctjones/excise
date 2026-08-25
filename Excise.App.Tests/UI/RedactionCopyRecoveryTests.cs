using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Excise.TestSupport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Copy is a named redaction-recovery channel (#1165): the exact thing an
/// ordinary user does to "check" a redaction is open the document, select over
/// where the secret was, and Ctrl+C. This drives that with REAL synthetic
/// pointer + keyboard input and pins that the redacted term cannot come back
/// through <see cref="MainWindowViewModel.ClipboardHistory"/>.
///
/// <para>The test is two-phase so it cannot pass vacuously: FIRST it drags over
/// the secret in the UNREDACTED document and asserts the copy path DOES recover
/// it (proving the selection→copy mechanism actually works and the geometry is
/// right), THEN it opens the redacted-and-saved file, drags the SAME screen
/// region, and asserts the term is absent. Glyph-level removal means there are
/// no glyphs left to select — this makes that guarantee a gate rather than an
/// assumption.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class RedactionCopyRecoveryTests
{
    private const string Secret = "TOPSECRET";
    private const double RenderDpi = 120.0;

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DragSelectAndCopyOverRedactedRegion_CannotRecoverTheTerm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-copy-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var originalPath = Path.Combine(dir, "original.pdf");
        var redactedPath = Path.Combine(dir, "redacted.pdf");
        CreateSecretPdf(originalPath);

        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
        window.Show();

        try
        {
            // ── Phase 1: the copy path RECOVERS the secret from the original ──
            await vm.LoadDocumentAsync(originalPath);
            await SettleLayout(window);
            vm.ZoomActualSizeCommand.Execute().Subscribe();
            vm.IsTextSelectionMode = true;
            await SettleLayout(window);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            var overlay = FindNamedDescendant<Canvas>(viewer, "OverlayCanvas")!;
            var page = vm.PdfCoreDocument!.GetPage(1);

            var (anchorWindow, focusWindow) = SecretDragPoints(page, overlay, window);

            await DragSelect(window, anchorWindow, focusWindow);
            await CopyAndSettle(window, vm);

            vm.SelectedText.Should().Contain(Secret,
                "precondition: a real drag over the secret in the unredacted document must select it");
            vm.ClipboardHistory.Should().Contain(e => e.Text.Contains(Secret),
                "precondition: Ctrl+C over the selected secret must place it in clipboard history — " +
                "otherwise phase 2 proves nothing");

            // ── Redact the term and save a fresh redacted file ──
            RedactAndSave(originalPath, redactedPath);
            // Independent check that the term really left the saved structure —
            // a decompressing byte scan (#1049), so "copy can't recover it" below
            // means the term is GONE, not merely that the copy path is broken.
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(redactedPath), Secret).Should().BeEmpty(
                "the saved redacted file must not carry the term in any carrier, incl. compressed streams");

            // ── Phase 2: the copy path CANNOT recover it from the redacted file ──
            await vm.LoadDocumentAsync(redactedPath);
            await SettleLayout(window);
            vm.ZoomActualSizeCommand.Execute().Subscribe();
            vm.IsTextSelectionMode = true;
            await SettleLayout(window);

            vm.ClipboardHistory.Clear();
            vm.SelectedText = string.Empty;

            // Same screen region the secret occupied — same window, same page
            // box, same zoom, so the pixels under the drag are identical.
            await DragSelect(window, anchorWindow, focusWindow);
            await CopyAndSettle(window, vm);

            vm.SelectedText.Should().NotContain(Secret,
                "a drag over the redacted region must not select the removed term — there are no glyphs left");
            vm.ClipboardHistory.Should().NotContain(e => e.Text.Contains(Secret),
                "the copy path must not be able to recover a redacted term from the saved document");
        }
        finally
        {
            window.Close();
        }
    }

    private static (Point Anchor, Point Focus) SecretDragPoints(PdfPage page, Canvas overlay, Window window)
    {
        var letters = (page.Letters?.ToList() ?? new List<Letter>());
        letters.Should().NotBeEmpty("the page must expose extractable letters");
        var ordered = TextSelectionEngine.SortReadingOrder(letters);

        // Find the contiguous run of letters that spells the secret.
        var joined = string.Concat(ordered.Select(l => l.Value));
        var start = joined.IndexOf(Secret, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the secret must be present in reading order before redaction");
        var anchor = ordered[start];
        var focus = ordered[start + Secret.Length - 1];

        return (ToWindowPoint(anchor, page, overlay, window),
                ToWindowPoint(focus, page, overlay, window));
    }

    private static async Task DragSelect(Window window, Point anchor, Point focus)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(anchor, MouseButton.Left));
        await Task.Delay(40);
        var mid = new Point((anchor.X + focus.X) / 2, (anchor.Y + focus.Y) / 2);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(mid));
        await Task.Delay(40);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(focus));
        await Task.Delay(40);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(focus, MouseButton.Left));
        for (var i = 0; i < 6; i++) { await Task.Delay(60); window.UpdateLayout(); }
    }

    private static async Task CopyAndSettle(Window window, MainWindowViewModel vm)
    {
        await window.PressKeyAsync(Key.C, RawInputModifiers.Control);
        for (var i = 0; i < 20; i++)
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await Task.Delay(30);
        }
    }

    private static void RedactAndSave(string sourcePath, string outPath)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        var report = doc.RedactText(Secret);
        report.MatchesLocated.Should().BeGreaterThan(0, "the redaction engine must locate and remove the term");
        doc.Save(outPath);
    }

    private static void CreateSecretPdf(string path)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        // Secret flanked by survivors on the same line so a drag stays on one line.
        graphics.DrawString($"PUBLIC {Secret} PUBLIC", PdfFont.Helvetica(28), PdfBrush.Black, 72, 600);
        graphics.Flush();
        doc.Save(path);
    }

    private static async Task SettleLayout(Window window)
    {
        for (var i = 0; i < 14; i++) { await Task.Delay(80); window.UpdateLayout(); }
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static Point ToWindowPoint(Letter l, PdfPage page, Canvas overlay, Window window)
    {
        var r = l.GlyphRectangle;
        var cx = (r.Left + r.Right) * 0.5;
        var cy = (r.Bottom + r.Top) * 0.5;
        var center = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(page.PageNumber, new PdfRectangle(cx, cy, cx, cy)),
            RenderDpi);
        return overlay.TranslatePoint(new Point(center.X, center.Y), window) ?? default;
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
            foreach (var child in p.Children)
                if (child is Control c && FindNamedDescendant<T>(c, name) is { } hit) return hit;
        if (root is Decorator d && d.Child is Control dc && FindNamedDescendant<T>(dc, name) is { } dh) return dh;
        if (root is ContentControl cc && cc.Content is Control ccc && FindNamedDescendant<T>(ccc, name) is { } ch) return ch;
        return root.FindControl<T>(name);
    }
}
