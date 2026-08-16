using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #695 Phase 2 — expected-effect registry. Phase 1
/// (<see cref="GuiClickSafetySweepTests"/>) proves clicks don't crash or blank
/// the app; it cannot tell "Zoom In correctly redrew the page" from "some button
/// accidentally opened a sidebar or blanked a region it had no business touching",
/// because it has no per-command notion of what SHOULD change.
///
/// This battery declares, per command, which region may change and which must NOT,
/// and verifies with STRUCTURAL asserts — page-surface ink and panel
/// effective-visibility — not raw-pixel goldens (cross-OS font/AA variance makes
/// byte-level baselines brittle, as the existing visual-baseline tests already show).
///
/// The window has one content region — the page surface (<c>PdfViewerControl</c>) —
/// and four independently toggle-able panels (Outline, Thumbnails, Clipboard,
/// Search), each <c>IsVisible</c>-bound to a VM flag. The contract for every
/// command in <see cref="Registry"/>:
///
///   • the page surface keeps comparable ink (no command here may blank the page);
///   • the command's declared <c>Flips</c> panel toggles visibility;
///   • every OTHER panel's visibility is unchanged (the "wrong region changed" guard).
///
/// Run at devicePixelRatio 1 and 2 (headless defaults to 1.0).
///
/// #978 expanded this registry from 16 entries to 71 of the ~87 ReactiveCommands
/// on <see cref="MainWindowViewModel"/> — every command whose expected region
/// effect is declarable against a single freshly-loaded one-page document. The
/// remainder stay out, each with a reason recorded at its would-be entry point
/// in <see cref="Registry"/> rather than in this summary (so the reason travels
/// with the exclusion instead of going stale up here): commands that take a
/// non-<c>Unit</c> parameter (no value here to supply), lifecycle commands that
/// quit the process or deliberately blank the document, one command that shells
/// out to the OS, one whose effect depends on registry ORDER, and page-count-
/// mutating commands whose own guards make them no-ops on this registry's
/// one-page fixture (tracked in #1001 for a multi-page fixture). They remain
/// covered by Phase 1's universal safety net (<see cref="GuiClickSafetySweepTests"/>)
/// and by their own dedicated workflow tests.
/// </summary>
[Collection("AvaloniaTests")]
public class GuiExpectedEffectTests
{
    private readonly ITestOutputHelper _out;
    public GuiExpectedEffectTests(ITestOutputHelper o) { _out = o; }

    private enum Panel { Outline, Thumbnails, Clipboard, Search }

    /// <summary>A command and the one panel it is expected to toggle (null = pure
    /// page-surface command that must leave every panel alone).</summary>
    private sealed record Effect(string Command, Panel? Flips);

    private static readonly Effect[] Registry =
    {
        // Page-surface-only: must keep the page inked and touch no panel.
        new("ZoomInCommand", null),
        new("ZoomOutCommand", null),
        new("ZoomActualSizeCommand", null),
        new("ZoomFitWidthCommand", null),
        new("ZoomFitPageCommand", null),
        new("NextPageCommand", null),
        new("PreviousPageCommand", null),

        // Page-MUTATION commands (rotate). #846 — the "continuous view doesn't
        // repaint after a page mutation" exclusion this comment used to cite —
        // was closed 2026-07-29, and NOT on a repaint fix: it was closed after
        // confirming in the LIVE GUI that the headless 0-ink reading was a test
        // -harness artifact (the manual pump loop not driving the continuous
        // tile re-render after a Document swap), then fixing a real but
        // DIFFERENT bug (reading-view scroll-position instability on rotate/
        // remove/move). Once that landed, this suite's own experiment (adding
        // RotatePageLeftCommand here and running it) held on the FIRST try —
        // the page-ink assert passes at both dpr 1 and 2 — so the hole closes
        // for free. Rotate is included below.
        new("RotatePageLeftCommand", null),
        new("RotatePageRightCommand", null),
        new("RotatePage180Command", null),
        // RemoveCurrentPage / MoveCurrentPageEarlier / MoveCurrentPageLater are
        // NOT added here even though #846 is resolved: this registry loads a
        // ONE-page fixture (BuildDensePdf) and shares that single Document
        // instance across every entry executed in sequence, so a command that
        // actually changed page COUNT would leave a corrupted/empty document
        // for every later entry in the same run. Their own guards
        // (RemoveCurrentPageAsync / MoveCurrentPage{Earlier,Later}Async) make
        // them no-ops on a 1-page document, which would make an entry here
        // pass vacuously without ever exercising the mutation — see #1001 for
        // giving this registry (or a sibling one) a multi-page fixture so
        // these get a real, non-vacuous entry instead of staying silently
        // absent. Their click-safety is covered by GuiClickSafetySweepTests
        // (Phase 1, 3-page fixture); their document-state correctness by
        // PageOrganizationCommandTests / PageOrganizationSavePersistenceTests.
        new("ToggleRedactionModeCommand", null),
        new("ToggleTextSelectionModeCommand", null),
        new("ToggleTypewriterModeCommand", null),
        new("ToggleFormAuthoringModeCommand", null),
        new("ToggleContinuousViewCommand", null),
        new("ToggleFreehandModeCommand", null),
        new("ToggleLineModeCommand", null),
        new("ToggleArrowModeCommand", null),
        new("TogglePolygonModeCommand", null),
        new("TogglePolyLineModeCommand", null),
        new("ToggleRevealHiddenTextCommand", null),
        new("ToggleRevealRasterizedHiddenCommand", null),

        // File-dialog-backed commands. Under the default headless VM (no DI
        // container, no stubbed StorageProvider/MainWindowResolver) their
        // dialog seam is a null service that returns "nothing picked" /
        // "not confirmed" without showing UI or blocking — the same fact
        // GuiClickSafetySweepTests (Phase 1) relies on to invoke all of these
        // without hanging. They therefore no-op: no document/page change, no
        // panel change.
        new("OpenFileCommand", null),
        new("SaveFileCommand", null),
        new("SaveAsCommand", null),
        new("AddPagesCommand", null),
        new("InsertPagesBeforeCurrentCommand", null),
        new("InsertPagesAfterCurrentCommand", null),
        new("ExtractCurrentPageCommand", null),
        new("ExtractSelectedPagesCommand", null),
        new("CombineDocumentsCommand", null),
        new("SplitDocumentCommand", null),
        new("ExportCurrentPageCommand", null),
        new("ExportPagesCommand", null),
        new("SaveFlattenedFormCopyCommand", null),
        new("MakeSearchableCommand", null),
        new("SecurityCommand", null),
        new("PrintCommand", null),
        new("VerifySignaturesCommand", null),
        new("ShowPreferencesCommand", null),
        new("AboutCommand", null),
        new("ShowShortcutsCommand", null),

        // Page-selection / redaction-list / typewriter-queue commands that are
        // no-ops on a freshly loaded document with an empty selection/pending
        // list — included so a command that stopped being a safe no-op (started
        // touching a panel, or blanking the page) would be caught here.
        new("RemoveSelectedPagesCommand", null),
        new("MoveSelectedPagesEarlierCommand", null),
        new("MoveSelectedPagesLaterCommand", null),
        new("ClearSelectedPagesCommand", null),
        new("ApplyRedactionCommand", null),
        new("ClearAllRedactionsCommand", null),
        new("ApplyAllRedactionsCommand", null),
        new("DiscardPendingTypewriterEditsCommand", null),
        new("GoToNextPendingTypewriterEditCommand", null),
        new("UndoCommand", null),
        new("RedoCommand", null),

        // Selection-driven annotation commands — no-ops with no active text
        // selection / pending drag rect on a freshly loaded document.
        new("AddHighlightAnnotationFromSelectionCommand", null),
        new("AddUnderlineAnnotationFromSelectionCommand", null),
        new("AddStrikeOutAnnotationFromSelectionCommand", null),
        new("AddSquigglyAnnotationFromSelectionCommand", null),
        new("AddSquareAnnotationFromDragCommand", null),
        new("AddCircleAnnotationFromDragCommand", null),
        new("AddFreeTextAnnotationFromDragCommand", null),
        new("AddImageStampAnnotationFromDragCommand", null),
        new("AddStickyNoteAnnotationCommand", null),

        new("CopyTextCommand", null),
        new("AutoDetectFieldsCommand", null),
        new("FindCommand", null),
        new("FindNextCommand", null),
        new("FindPreviousCommand", null),

        // Panel toggles: flip exactly their own panel, leave the page + other panels.
        new("ToggleThumbnailsCommand", Panel.Thumbnails),
        new("ToggleOutlineCommand", Panel.Outline),
        new("ToggleClipboardSidebarCommand", Panel.Clipboard),
        new("ToggleSearchCommand", Panel.Search),

        // NOT in this registry, with a live reason each:
        //  - RemovePendingRedactionCommand (Guid), SetTypewriterColorCommand
        //    (string), AddStampAnnotationFromDragCommand (string), GoToPageCommand
        //    (int), JumpToOutlineCommand (OutlineNode), LoadRecentFileCommand
        //    (string), OpenExternalLinkCommand (string),
        //    ShowDangerousLinkRefusalCommand (string), JumpToSearchMatchCommand
        //    (SearchMatch): all take a non-Unit TParam. cmd.Execute(null) on a
        //    typed command is not the "click it" this registry models — the
        //    harness has no real Guid/string/int/OutlineNode/SearchMatch to
        //    supply, and passing null risks an invalid-cast throw unrelated to
        //    the region contract this file checks.
        //  - ExitCommand, CloseDocumentCommand: lifecycle commands that quit the
        //    process / deliberately clear the document. Excluded here for the
        //    same reason GuiClickSafetySweepTests.SkipCommands excludes them —
        //    ExitCommand cannot run inside a test process at all, and
        //    CloseDocumentCommand blanks the page BY DESIGN, which is exactly
        //    what this registry's ink-preserved contract exists to catch.
        //  - ShowDocumentationCommand: shells out to the OS (opens a browser) —
        //    same reason GuiClickSafetySweepTests skips it.
        //  - CloseSearchCommand: unconditionally sets IsSearchVisible = false,
        //    so its Search-panel effect only manifests when Search is already
        //    open. Whether that holds depends on registry ORDER (which entry
        //    ran last), which is exactly the kind of fragile, reorder-breaks-it
        //    assumption this registry should not encode. Covered directly by
        //    KeyboardShortcutEffectTests and RedactionAndSearchCommandTests.
    };

    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task EachCommand_ChangesOnlyItsDeclaredRegion(double dpr)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-effect-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildDensePdf());

        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            var panels = new Dictionary<Panel, Control>
            {
                [Panel.Outline] = window.FindControl<Control>("OutlinePanel")!,
                [Panel.Thumbnails] = window.FindControl<Control>("ThumbnailsPanel")!,
                [Panel.Clipboard] = window.FindControl<Control>("ClipboardSidebarHost")!,
                [Panel.Search] = window.FindControl<Control>("SearchTextBox")!,
            };
            panels.Values.Should().OnlyContain(c => c != null, "every tracked region control must exist by name");

            var nameToCommand = BuildCommandMap(vm);
            await CaptureWhenInkedAsync(window, viewer); // settle the initial render

            var failures = new List<string>();
            int checkedCount = 0;

            foreach (var effect in Registry)
            {
                if (!nameToCommand.TryGetValue(effect.Command, out var cmd))
                {
                    failures.Add($"{effect.Command}: no such command on the ViewModel (renamed?)");
                    continue;
                }
                if (!cmd.CanExecute(null))
                {
                    _out.WriteLine($"skip {effect.Command} (CanExecute=false at this point)");
                    continue;
                }

                double inkBefore = 0;
                var visBefore = new Dictionary<Panel, bool>();
                await Dispatcher_InvokeCapture(window, viewer, panels, ink => inkBefore = ink, visBefore);

                await Dispatcher_Execute(window, cmd);

                double inkAfter = 0;
                var visAfter = new Dictionary<Panel, bool>();
                await Dispatcher_InvokeCapture(window, viewer, panels, ink => inkAfter = ink, visAfter);

                checkedCount++;

                // 1. Page surface preserved.
                if (inkAfter < inkBefore * 0.4)
                    failures.Add($"{effect.Command}: page ink collapsed {inkBefore:P2}→{inkAfter:P2} (dpr={dpr}) — a command must not blank the page");

                // 2/3. Exactly the declared panel flipped; all others unchanged.
                foreach (var p in Enum.GetValues<Panel>())
                {
                    bool changed = visBefore[p] != visAfter[p];
                    if (effect.Flips == p)
                    {
                        if (!changed)
                            failures.Add($"{effect.Command}: expected to toggle {p} visibility but it stayed {visAfter[p]} (dpr={dpr})");
                    }
                    else if (changed)
                    {
                        failures.Add($"{effect.Command}: changed {p} visibility {visBefore[p]}→{visAfter[p]} (dpr={dpr}) — it must only affect {(effect.Flips?.ToString() ?? "the page surface")}");
                    }
                }
            }

            _out.WriteLine($"Verified {checkedCount}/{Registry.Length} registry commands at dpr={dpr}");
            if (failures.Count > 0) _out.WriteLine("FAILURES:\n  " + string.Join("\n  ", failures));

            checkedCount.Should().BeGreaterThan(12, "most registry commands should be executable with a doc loaded");
            failures.Should().BeEmpty("every command must change only its declared region");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    // ── per-step helpers ────────────────────────────────────────────────────────

    private static async Task Dispatcher_Execute(Window window, ICommand cmd)
    {
        cmd.Execute(null);
        for (int i = 0; i < 4; i++) { await Task.Delay(40); window.UpdateLayout(); }
    }

    /// <summary>Capture the page-surface ink (settled) and each panel's effective
    /// visibility in one settled sample. Waits for the viewer to finish loading
    /// (a page-mutating command such as rotate re-renders asynchronously) before
    /// sampling, so a mid-render blank frame is never mistaken for a blanked page.</summary>
    private async Task Dispatcher_InvokeCapture(
        Window window, PdfViewerControl viewer, Dictionary<Panel, Control> panels,
        Action<double> setInk, Dictionary<Panel, bool> vis)
    {
        double ink = 0;
        var deadline = Environment.TickCount64 + 20000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(80);
            window.UpdateLayout();
            if (viewer.IsLoading) continue;              // let the (re-)render finish
            using var bmp = Capture(viewer);
            ink = InkFraction(bmp);
            if (ink > 0.002) break;                       // settled into an inked frame
        }
        setInk(ink);
        foreach (var (p, c) in panels)
            vis[p] = c.IsEffectivelyVisible;
    }

    private static Dictionary<string, ICommand> BuildCommandMap(MainWindowViewModel vm)
    {
        var map = new Dictionary<string, ICommand>(StringComparer.Ordinal);
        foreach (var prop in vm.GetType().GetProperties())
        {
            if (!typeof(ICommand).IsAssignableFrom(prop.PropertyType)) continue;
            try { if (prop.GetValue(vm) is ICommand c) map[prop.Name] = c; }
            catch { /* skip */ }
        }
        return map;
    }

    // ── capture + ink (page surface) ────────────────────────────────────────────

    private static async Task<SKBitmap> CaptureWhenInkedAsync(Window window, PdfViewerControl viewer, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        SKBitmap? last = null;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            window.UpdateLayout();
            last?.Dispose();
            last = Capture(viewer);
            if (InkFraction(last) > 0.002) return last;
        }
        last.Should().NotBeNull("viewer produced no capture");
        return last!;
    }

    private static SKBitmap Capture(PdfViewerControl viewer)
    {
        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms) ?? throw new InvalidOperationException("Could not decode captured viewer surface.");
    }

    private const int ChromeMarginPx = 20;

    private static double InkFraction(SKBitmap bmp)
    {
        int count = 0;
        int right = bmp.Width - ChromeMarginPx;
        int bottom = bmp.Height - ChromeMarginPx;
        for (int y = 0; y < bottom; y++)
        for (int x = 0; x < right; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384) count++;
        }
        return (double)count / (bmp.Width * bmp.Height);
    }

    private static byte[] BuildDensePdf()
    {
        var content =
            "BT /F1 20 Tf 60 740 Td (EXCISE EXPECTED EFFECT CHECK LINE ONE) Tj " +
            "0 -26 Td (THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG) Tj " +
            "0 -26 Td (0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ) Tj " +
            "0 -26 Td (FOURTH LINE OF BODY TEXT FOR INK MASS) Tj " +
            "0 -26 Td (FIFTH LINE KEEPS THE PAGE WELL INKED) Tj ET";

        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
