using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
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
/// #695 Phase 1 — universal click-safety sweep. Enumerates EVERY command-backed
/// leaf Button/MenuItem in a real MainWindow with a document loaded and INVOKES
/// each one (a command-bound control's click == executing its Command with its
/// CommandParameter; the audit for #695 confirmed MainWindow has no Click-handler
/// buttons). The bar is deliberately low and universal — the two things a click
/// must never do:
///
///   1. throw (a menu item whose handler explodes is a dead, dangerous affordance);
///   2. permanently break rendering (after poking everything, the app must still
///      load and display a document).
///
/// Per-command "the RIGHT region changed and the others didn't" is Phase 2 (the
/// expected-effect registry) — not asserted here, because many commands legitimately
/// change the raster (navigation, zoom, remove-page, mode overlays), so a per-command
/// ink-diff would be a false-positive machine without per-command expectations.
///
/// Commands that open a native file picker, present/await a modal dialog, shell out
/// to the OS, quit the app, or deliberately clear the document are SKIPPED by name
/// (see <see cref="SkipCommands"/>): under the default headless VM their dialog seams
/// no-op (null StorageProvider / null MainWindowResolver / NullUserDialogService), so
/// they would not hang — but they have nothing to assert and clearing the document
/// would blank the page by design. The skip set is LOGGED every run so the coverage
/// it removes is never silent (#619's lesson).
///
/// Set EXCISE_DUMP_CLICK_CAPTURES=dir to archive an after-click PNG per command.
/// </summary>
[Collection("AvaloniaTests")]
public class GuiClickSafetySweepTests
{
    private readonly ITestOutputHelper _out;
    public GuiClickSafetySweepTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// Command property names skipped from execution (the #695 command-safety
    /// audit). File pickers / modal dialogs / OS-shell / lifecycle. They still
    /// have their CanExecute poked by CommandBindingSweepTests; this sweep is
    /// about executing the safe remainder.
    /// </summary>
    private static readonly HashSet<string> SkipCommands = new(StringComparer.Ordinal)
    {
        // Kept deliberately SHORT. This list was 24 entries covering every
        // picker- and dialog-backed command, on the reasoning that they "have
        // nothing to assert". That conflates two things: a command with no
        // interesting POST-CONDITION is still a command that must not THROW, and
        // a command nothing ever clicks is one nothing would notice exploding.
        //
        // Those seams are already inert headlessly — null StorageProvider, null
        // MainWindowResolver, NullUserDialogService whose ShowConfirmAsync
        // returns false — so they return having done nothing rather than
        // hanging. Removing them took this sweep from 40 invoked to 61, and
        // mutation-checking a newly-included one (PrintCommand made to throw)
        // confirms the extra 21 are genuinely covered rather than merely counted.
        //
        // What remains is what the TEST HOST cannot survive, not what is
        // uninteresting:
        // Shell out to the OS / external.
        "ShowDocumentationCommand", "OpenExternalLinkCommand", "ShowDangerousLinkRefusalCommand",
        // Lifecycle: quit, or deliberately clear/replace the document (blanks the page by design).
        "ExitCommand", "CloseDocumentCommand", "LoadRecentFileCommand",
    };

    [FixedAvaloniaFact]
    public async Task EveryEnabledLeafCommand_Invoked_DoesNotThrow_AndAppStillRenders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-click-sweep-{Guid.NewGuid():N}.pdf");
        // Dense text (so the page is visibly inked > gate) AND multiple pages (so
        // remove-page / navigation commands can't empty the document mid-sweep).
        File.WriteAllBytes(path, BuildDenseMultiPagePdf(pageCount: 3));

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;

            var baseline = await CaptureWhenInkedAsync(window, viewer);
            InkFraction(baseline).Should().BeGreaterThan(0.002,
                "fixture sanity — the document must be visibly rendered before the sweep");

            var nameOf = BuildCommandNameMap(vm);

            var invoked = new List<string>();
            var skipped = new List<string>();
            var threw = new List<string>();
            var seen = new HashSet<ICommand>();

            foreach (var (host, label) in CollectCommandHosts(window))
            {
                ICommand? cmd = host switch
                {
                    Button b => b.Command,
                    MenuItem m => m.Command,
                    _ => null,
                };
                if (cmd == null) continue;               // container / toggle / flyout host
                if (!seen.Add(cmd)) continue;            // same command bound to several controls

                object? param = host switch
                {
                    Button b => b.CommandParameter,
                    MenuItem m => m.CommandParameter,
                    _ => null,
                };

                var name = nameOf.TryGetValue(cmd, out var n) ? n : label;

                if (SkipCommands.Contains(name)) { skipped.Add($"{name} (dialog/file/lifecycle)"); continue; }

                bool can;
                try { can = cmd.CanExecute(param); }
                catch (Exception ex) { threw.Add($"{name}: CanExecute threw {ex.GetType().Name}: {ex.Message}"); continue; }
                if (!can) { skipped.Add($"{name} (CanExecute=false)"); continue; }

                try
                {
                    cmd.Execute(param);
                    await PumpBrieflyAsync(window);
                    invoked.Add(name);
                    DumpCaptureIfRequested(Capture(viewer), $"after-{name}");
                }
                catch (Exception ex)
                {
                    threw.Add($"{name}: Execute threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Publish what command EXECUTION reached, so the interaction-coverage
            // report can separate "covered only by executing its command" (B) from
            // "no automation at all" (C). Without this split the gap list reads far
            // worse than the truth: most menu items are B, not C.
            // See Excise.App.Tests/UI/InteractionCoverage/GuiInteractionRecorder.cs.
            var executedPath = System.IO.Path.Combine(
                InteractionCoverage.GuiInteractionRecorder.RepoArtifactsDirectory(), "gui-command-executed.tsv");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(executedPath)!);
            System.IO.File.WriteAllLines(executedPath, invoked.OrderBy(x => x, StringComparer.Ordinal));

            _out.WriteLine($"Invoked {invoked.Count}: {string.Join(", ", invoked)}");
            _out.WriteLine($"Skipped {skipped.Count}: {string.Join(", ", skipped)}");
            if (threw.Count > 0) _out.WriteLine("THREW:\n  " + string.Join("\n  ", threw));

            threw.Should().BeEmpty(
                "clicking any enabled button/menu item must not throw — a command that explodes on click " +
                "is a dead, potentially dangerous affordance");

            invoked.Count.Should().BeGreaterThan(15,
                "sanity: the sweep must exercise a broad set of commands, not skip nearly everything " +
                "(a too-aggressive skip set or a collapsed logical tree would make this pass vacuously)");

            // Durable no-blank-out: after invoking every safe command, the app must
            // still load and render a document. A command that permanently broke the
            // render pipeline (disposed a shared resource, wedged the viewer) trips here.
            await vm.LoadDocumentAsync(path);
            var final = await CaptureWhenInkedAsync(window, viewer,
                "after invoking every safe command, the app must still load and display a document");
            InkFraction(final).Should().BeGreaterThan(0.002,
                "the viewer must still render page ink after the full command sweep — a blank here means " +
                "some command left the render pipeline broken");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    /// <summary>Map each live ICommand instance on the VM to its property name, so
    /// the skip set can match by a stable identifier rather than a UI label.</summary>
    private static Dictionary<ICommand, string> BuildCommandNameMap(MainWindowViewModel vm)
    {
        var map = new Dictionary<ICommand, string>();
        foreach (var p in vm.GetType().GetProperties())
        {
            if (!typeof(ICommand).IsAssignableFrom(p.PropertyType)) continue;
            object? value;
            try { value = p.GetValue(vm); }
            catch { continue; }
            if (value is ICommand cmd) map[cmd] = p.Name;
        }
        return map;
    }

    private static IEnumerable<(Control Host, string Label)> CollectCommandHosts(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case MenuItem mi:
                    // Submenu containers and toggle-type items don't carry a click Command.
                    if (!(mi.Items.Count > 0 || mi.ItemsSource != null) && mi.ToggleType == MenuItemToggleType.None)
                        yield return (mi, $"MenuItem '{HeaderText(mi)}'");
                    break;
                case ToggleButton:
                    break; // activates via IsChecked, not Command
                case Button btn:
                    if (btn.Flyout == null)
                        yield return (btn, $"Button '{ButtonLabel(btn)}'");
                    break;
            }
            foreach (var child in node.LogicalChildren)
                stack.Push(child);
        }
    }

    private static string HeaderText(MenuItem mi) => mi.Header switch
    {
        string s => s,
        TextBlock tb => tb.Text ?? "<TextBlock>",
        _ => mi.Header?.ToString() ?? "<null>",
    };

    private static string ButtonLabel(Button b)
    {
        if (!string.IsNullOrEmpty(b.Name)) return b.Name!;
        if (b.Content is string s) return s;
        if (b.Content is TextBlock tb) return tb.Text ?? "<TextBlock>";
        return b.Content?.ToString() ?? "<unnamed>";
    }

    /// <summary>A dense N-page PDF: each page carries several lines of 20pt text
    /// so the rendered ink clears the sweep's visibility gate, and there are
    /// enough pages that remove/navigate commands never empty the document.</summary>
    private static byte[] BuildDenseMultiPagePdf(int pageCount)
    {
        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");

        // #1019: was 1 + 1 + pageCount * 2, which collides with the LAST page's
        // content stream. Per-page objects run 3..(2 + pageCount*2), so the last
        // content stream IS 2 + pageCount*2 — the same number. At pageCount 3 the
        // font and page 3's content were both object 8, and page 3 extracted empty.
        int fontObj = 3 + pageCount * 2; // catalog(1) pages(2) then per page: page+content
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"));
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            int pageId = 3 + i * 2;
            int contentId = pageId + 1;
            var content =
                $"BT /F1 20 Tf 60 740 Td (EXCISE CLICK SWEEP PAGE {i + 1} LINE ONE) Tj " +
                "0 -26 Td (THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG) Tj " +
                "0 -26 Td (0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ) Tj " +
                "0 -26 Td (FOURTH LINE OF BODY TEXT FOR INK MASS) Tj " +
                "0 -26 Td (FIFTH LINE KEEPS THE PAGE WELL INKED) Tj ET";
            Obj($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {fontObj} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");
            Obj($"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        Obj($"{fontObj} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int total = fontObj + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {total}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── capture + ink (mirrors ModeSwitchVisualTests' helpers) ──────────────────

    private static void DumpCaptureIfRequested(SKBitmap bmp, string name)
    {
        var dir = Environment.GetEnvironmentVariable("EXCISE_DUMP_CLICK_CAPTURES");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        // Sanitize the command name for a filename.
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        using var fs = File.Create(Path.Combine(dir, $"{safe}.png"));
        data.SaveTo(fs);
    }

    private static async Task<SKBitmap> CaptureWhenInkedAsync(
        Window window, PdfViewerControl viewer, string? failureContext = null, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        SKBitmap? last = null;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            window.UpdateLayout();
            last?.Dispose();
            last = Capture(viewer);
            if (InkFraction(last) > 0.002)
                return last;
        }
        last.Should().NotBeNull(failureContext ?? "viewer produced no capture");
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
        return SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException("Could not decode captured viewer surface.");
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

    private static async Task PumpBrieflyAsync(Window window, int iterations = 3)
    {
        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(30);
            window.UpdateLayout();
        }
    }
}
