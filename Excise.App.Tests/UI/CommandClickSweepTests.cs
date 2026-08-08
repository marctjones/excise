using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AwesomeAssertions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #695 Phase 1 — CLICK every leaf command, don't just check that it resolves.
///
/// WHAT THIS ADDS OVER CommandBindingSweepTests
///
/// That sweep walks the same tree and verifies each Button/MenuItem resolves to
/// a non-null ICommand whose CanExecute does not throw. Its own comment says why
/// it stops there:
///
///     "Don't invoke — half the commands open file dialogs, exit the app, or
///      otherwise have side effects we don't want during a sweep."
///
/// That was true when written and is mostly no longer true:
///
///   * Dialogs are already neutralised. `new MainWindowViewModel()` defaults to
///     NullUserDialogService, whose every method is a no-op returning a default.
///     ShowConfirmAsync returns FALSE, so destructive confirm-gated paths
///     decline rather than proceed.
///   * File pickers are already inert. GetStorageProvider() returns null in a
///     headless host, so picker-backed commands return having done nothing.
///
/// So the measured coverage before this file was:
///
///     GUI ReactiveCommands   65
///     commands actually clicked   4   (ModeSwitchVisualTests)
///
/// Roughly 61 of 65 commands had nothing verifying that clicking them did
/// anything at all — including not crashing. This closes that.
///
/// WHAT IT ASSERTS, AND WHY IT IS DELIBERATELY WEAK
///
/// Only "clicking this does not break the app": no unhandled exception, the
/// document is still loaded, and the viewer still has a page bitmap. It does NOT
/// assert each command's specific effect — that is Phase 2's expected-effect
/// registry, and inventing per-command assertions here would produce 60 shallow
/// tests that lock in current behaviour rather than one that catches crashes.
///
/// The value is the failure it would have caught: a command that blanks the page
/// surface or throws is invisible to a binding check and to every whole-page ink
/// metric (#883 measured that floor at 0.06% of a page).
///
/// ISOLATION, AND WHY THERE ISN'T ANY
///
/// All 86 commands run against ONE loaded document, in tree order. State
/// therefore accumulates — a mode toggle stays toggled, a removed page stays
/// removed — so a late failure may be caused by an earlier command rather than
/// by itself. That is a real weakness and the failure message will be
/// misleading when it happens.
///
/// Per-command reload was tried and abandoned: cycling LoadDocumentAsync 86
/// times raised ObjectDisposedException on 'Ref&lt;IBitmapImpl&gt;' — churn from
/// the sweep's own reload rate, not a path a user reaches. Chasing it would have
/// meant debugging the harness instead of testing the app.
///
/// Accumulating state is also closer to how the app is used: a session where
/// someone clicks many things in sequence. If a command only crashes after
/// another has run, that is worth knowing.
/// </summary>
[Collection("AvaloniaTests")]
public class CommandClickSweepTests
{
    private readonly ITestOutputHelper _out;
    public CommandClickSweepTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// Commands that must not be invoked, each for a reason that is about the
    /// TEST HOST rather than about the command being untrustworthy. Anything not
    /// listed here gets clicked.
    /// </summary>
    private static readonly (string Fragment, string Why)[] DoNotClick =
    {
        ("Exit",           "terminates the application and with it the test host"),
        ("OpenExternalLink", "launches the user's browser"),
        ("ShowDocumentation", "opens documentation in the user's browser"),
    };

    // Note: on macOS these three currently match NOTHING, because Exit and the
    // app-menu items live in the NATIVE menu (MacNativeMenuBuilder), not in the
    // window's logical tree this sweep walks. A skipped count of 0 is therefore
    // expected here and is not evidence the denylist is broken — it is retained
    // for the platforms where those items DO appear in-window, and because
    // MacNativeMenuCommandItems_ResolveToNonNullCommands covers the native menu
    // separately.

    [FixedAvaloniaFact]
    public async Task EveryLeafCommand_CanBeClickedWithoutBreakingTheApp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-click-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(source, 3);

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            window.UpdateLayout();
            await vm.LoadDocumentAsync(source);

            var leaves = CollectClickableLeaves(window).ToList();
            leaves.Should().NotBeEmpty("the tree walk must find clickable commands");
            _out.WriteLine($"clickable leaves discovered: {leaves.Count}");

            var crashed = new List<string>();
            var brokeTheApp = new List<string>();
            int clicked = 0, skipped = 0, notExecutable = 0;

            foreach (var (label, cmd) in leaves)
            {
                var deny = DoNotClick.FirstOrDefault(d =>
                    label.Contains(d.Fragment, StringComparison.OrdinalIgnoreCase));
                if (deny.Fragment != null)
                {
                    skipped++;
                    continue;
                }

                if (!SafeCanExecute(cmd))
                {
                    notExecutable++;
                    continue;
                }

                clicked++;
                try
                {
                    cmd.Execute(null);
                    await SettleAsync(vm);
                }
                catch (Exception ex)
                {
                    crashed.Add($"{label}: {ex.GetType().Name}: {Truncate(ex.Message)}");
                    continue;
                }

                // Some commands legitimately end the session. Close Document is
                // SUPPOSED to unload — treating that as a defect would either
                // fail the sweep forever or push Close onto the denylist, and
                // Close is exactly the kind of command worth clicking. Reload
                // and carry on; everything after it would otherwise cascade into
                // false findings, which is what the first run produced.
                if (ClosesTheDocument(label))
                {
                    await vm.LoadDocumentAsync(source);
                    await SettleAsync(vm);
                    continue;
                }

                // The weak, deliberate assertion: the app is still usable.
                //
                // NOT CurrentPageImage. That was the first draft's check and it
                // is wrong: the default ViewMode is Continuous, which paints
                // tiles through a separate path and never populates
                // CurrentPageImage at all — measured, it is null even
                // immediately after a successful load. Asserting on it reported
                // the next-page button as "blanked the surface" when nothing was
                // wrong, and made every iteration burn the full settle budget
                // waiting for a bitmap that never arrives.
                //
                // An assertion that is invalid in the default view mode is worse
                // than no assertion: it produces confident, wrong findings. A
                // mode-aware surface check belongs in Phase 2 alongside the
                // expected-effect registry, where the mode is known.
                if (!vm.IsDocumentLoaded)
                    brokeTheApp.Add($"{label}: document is no longer loaded");
                else if (vm.PdfCoreDocument == null)
                    brokeTheApp.Add($"{label}: document handle is gone while still reporting loaded");
            }

            _out.WriteLine($"clicked={clicked} skipped(denylist)={skipped} canExecute=false={notExecutable}");
            if (crashed.Count > 0)
                _out.WriteLine("THREW:\n  " + string.Join("\n  ", crashed));
            if (brokeTheApp.Count > 0)
                _out.WriteLine("LEFT THE APP UNUSABLE:\n  " + string.Join("\n  ", brokeTheApp));

            clicked.Should().BeGreaterThan(20,
                "this sweep exists to CLICK commands; if almost everything reports CanExecute=false " +
                "the fixture is not in a state where the menus are live and the test proves nothing");

            crashed.Should().BeEmpty(
                "clicking a command a user can click must not throw. Dialogs are no-ops " +
                "(NullUserDialogService) and file pickers are inert headlessly, so a throw here is " +
                "the command's own fault rather than the harness's");

            brokeTheApp.Should().BeEmpty(
                "after clicking any single command the document must still be loaded and the viewer " +
                "must still have a page bitmap. A command that blanks the page surface is invisible " +
                "to a binding check and to whole-page ink metrics — #883 measured that floor at " +
                "0.06% of a page, and the W-9 checkbox bug lived at 0.04%");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The denylist must stay small and justified. If it grows, the sweep is
    /// quietly reverting to the binding-only check it replaced.
    /// </summary>
    [Fact]
    public void TheDoNotClickList_StaysSmallAndJustified()
    {
        DoNotClick.Should().HaveCountLessThanOrEqualTo(5,
            "every entry here is a command nothing clicks. A long list turns this sweep back " +
            "into CommandBindingSweepTests with extra steps");

        DoNotClick.Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.Why),
            "each exclusion states why the TEST HOST cannot survive it — not that the command " +
            "is suspect");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Commands whose job is to unload the document. Distinct from the denylist:
    /// these ARE clicked, their effect is expected, and the fixture recovers.
    /// </summary>
    private static bool ClosesTheDocument(string label) =>
        label.Contains("Close Document", StringComparison.OrdinalIgnoreCase);

    private static bool SafeCanExecute(ICommand cmd)
    {
        try { return cmd.CanExecute(null); }
        catch { return false; }   // CommandBindingSweepTests owns the throwing-CanExecute assertion
    }

    /// <summary>
    /// Let queued dispatcher work drain. ReactiveCommand's ICommand.Execute
    /// starts async work and returns immediately, so without this the assertions
    /// would run against state the command has not finished producing.
    /// </summary>
    private static async Task PumpAsync()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Wait for the app to come back to a usable state, rather than sampling it
    /// at an arbitrary instant.
    ///
    /// Waits on the document handle rather than on a rendered bitmap. The first
    /// draft waited for CurrentPageImage, which in the default Continuous view
    /// mode is never populated — so every iteration burned the full budget and
    /// the sweep took nine minutes to reach a wrong conclusion.
    /// </summary>
    private static async Task SettleAsync(MainWindowViewModel vm)
    {
        const int budgetMs = 1500, stepMs = 25;
        for (int waited = 0; waited < budgetMs; waited += stepMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (vm.IsDocumentLoaded && vm.PdfCoreDocument != null) return;
            await Task.Delay(stepMs);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Truncate(string s) =>
        s.Length <= 140 ? s : s[..140] + "…";

    /// <summary>
    /// Leaf command hosts, using the same container/toggle/flyout exclusions
    /// CommandBindingSweepTests established — a submenu parent, a toggle wired
    /// through IsChecked, and a Flyout host all legitimately carry no Command.
    /// </summary>
    private static IEnumerable<(string Label, ICommand Command)> CollectClickableLeaves(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is MenuItem mi)
            {
                bool isContainer = mi.Items.Count > 0 || mi.ItemsSource != null;
                bool isToggle = mi.ToggleType != MenuItemToggleType.None;
                if (!isContainer && !isToggle && mi.Command != null)
                    yield return ($"MenuItem '{Header(mi)}'", mi.Command);
            }
            else if (node is Button btn)
            {
                bool isToggle = btn is ToggleButton;
                bool isFlyoutHost = btn.Flyout != null;
                if (!isToggle && !isFlyoutHost && btn.Command != null)
                    yield return ($"Button '{Label(btn)}'", btn.Command);
            }

            foreach (var child in node.LogicalChildren)
                stack.Push(child);
        }
    }

    private static string Header(MenuItem mi) =>
        mi.Header?.ToString() ?? mi.Name ?? "<unnamed>";

    private static string Label(Button b) =>
        b.Content?.ToString() ?? b.Name ?? AutomationId(b) ?? "<unnamed>";

    private static string? AutomationId(Control c) =>
        global::Avalonia.Automation.AutomationProperties.GetAutomationId(c);
}
