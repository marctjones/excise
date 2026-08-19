using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Excise.App.Tests.UI.InteractionCoverage;

/// <summary>
/// Records which interactive elements this assembly's tests actually drive with
/// a SYNTHETIC POINTER OR KEY EVENT — the NUMERATOR of GUI interaction coverage.
///
/// <para><b>Why this and not a registry.</b> The obvious way to answer "which
/// GUI elements have automated mouse/keyboard coverage" is a hand-written table
/// mapping element to test. Such a table is a self-declaration: it goes stale
/// silently, and nothing stops a row claiming coverage that a refactor deleted.
/// This records the events as they are raised, so an element cannot enter the
/// numerator without a test having genuinely raised input at it.</para>
///
/// <para><b>Why it excludes command invocation, deliberately.</b>
/// <c>GuiClickSafetySweepTests</c> reaches 61 commands by calling
/// <c>Command.Execute(parameter)</c>. That is valuable — it is what proves no
/// menu item explodes — but it is not a click: it cannot fail when the control
/// is collapsed, disabled by a broken binding, positioned off-screen, covered by
/// another element, or bound to nothing at all (a null Command is
/// <c>continue</c>d past, silently). Those are exactly the defects a real
/// pointer event catches and command execution cannot, so the two are counted
/// separately rather than pooled.</para>
///
/// <para><b>Append-on-first-sight, not flush-on-exit.</b> A process-exit hook
/// loses everything when the host dies, which this repo sees often enough to
/// have a memory note about it. Each id is appended the first time it is seen,
/// so a killed run still yields the partial truth — and a lost record reads as
/// UNCOVERED, failing toward re-running rather than toward a vacuous green
/// (<c>scripts/lib-runner.sh</c>'s checkpoint rule, applied to an artifact).</para>
/// </summary>
public static class GuiInteractionRecorder
{
    private static readonly ConcurrentDictionary<string, byte> Observed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Inventoried = new(StringComparer.Ordinal);
    private static readonly object FileLock = new();
    private static bool _installed;
    private static string? _artifactPath;

    /// <summary>
    /// Where observations are appended. Overridable so a chunked or sharded run
    /// can point every process at one file; the format is append-only lines, so
    /// concurrent writers merge without coordination.
    /// </summary>
    public static string ArtifactPath =>
        _artifactPath ??= Environment.GetEnvironmentVariable("EXCISE_GUI_INTERACTION_ARTIFACT")
            ?? Path.Combine(RepoArtifactsDirectory(), "gui-interaction-observed.tsv");

    /// <summary>
    /// Subscribe to the routed input events for the whole process. Called from
    /// <c>TestAppBuilder.BuildAvaloniaApp().AfterSetup(...)</c>, which every
    /// <c>[FixedAvaloniaFact]</c> in the assembly goes through, so no individual
    /// test has to opt in — an opt-in would under-report by exactly the tests
    /// whose authors did not know the gate existed.
    /// </summary>
    public static void Install()
    {
        lock (FileLock)
        {
            if (_installed) return;
            _installed = true;
        }

        InputElement.PointerPressedEvent.Raised.Subscribe(
            new AnonymousObserver<(object, RoutedEventArgs)>(t => Record(t.Item2, "pointer")));
        InputElement.PointerReleasedEvent.Raised.Subscribe(
            new AnonymousObserver<(object, RoutedEventArgs)>(t => Record(t.Item2, "pointer")));
        InputElement.KeyDownEvent.Raised.Subscribe(
            new AnonymousObserver<(object, RoutedEventArgs)>(t => Record(t.Item2, "key")));
        InputElement.TextInputEvent.Raised.Subscribe(
            new AnonymousObserver<(object, RoutedEventArgs)>(t => Record(t.Item2, "key")));
    }

    /// <summary>Ids observed so far in this process.</summary>
    public static IReadOnlyCollection<string> ObservedIds => Observed.Keys.ToList();

    /// <summary>
    /// Append a window's interactive elements to the inventory the first time
    /// input reaches it, so the denominator covers the dialogs without this file
    /// carrying five ViewModel construction recipes that would drift from the
    /// real ones.
    ///
    /// <para><b>Why on first input and not on window open.</b> Subscribing to
    /// <c>Control.LoadedEvent.Raised</c> covered strictly more — including a
    /// dialog opened but never touched — and wedged the test host twice at
    /// around the same point, 0% CPU and 33 MB RSS, against a 10m18s clean run
    /// without it. Enumerating from inside layout is not worth a suite that does
    /// not finish.</para>
    ///
    /// <para><b>What that costs, stated plainly.</b> A window no test ever
    /// interacts with contributes neither numerator nor denominator, so it is
    /// invisible here rather than reported as uncovered — the one place this
    /// gate under-reports. <c>SaveRedactedVersionDialog</c> is exactly that case
    /// today. MainWindow, which is nearly all of the surface, is anchored
    /// deterministically by <c>GuiInteractionCoverageTests</c> instead.</para>
    ///
    /// <para>Once per window INSTANCE, keyed by reference; and ids are deduped
    /// across instances, because the suite constructs MainWindow hundreds of
    /// times and appending its ids each time produced a 30,000-line artifact
    /// saying nothing a 300-line one does not.</para>
    /// </summary>
    private static void NoteSurface(TopLevel root)
    {
        try
        {
            lock (SurfaceLock)
            {
                if (!SeenSurfaces.Add(root)) return;
            }

            // Dedupe across window INSTANCES too, not just within one. The suite
            // constructs MainWindow hundreds of times; appending its ~148 ids
            // each time produced a 30,000-line artifact saying nothing a
            // 166-line one does not.
            var ids = GuiInteractiveElementInventory.Enumerate(root)
                .Select(e => e.Id)
                .Where(id => Inventoried.TryAdd(id, 0))
                .ToList();
            if (ids.Count == 0) return;

            lock (FileLock)
            {
                var path = Path.Combine(
                    Path.GetDirectoryName(ArtifactPath)!, "gui-interaction-inventory.tsv");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, ids);
            }
        }
        catch (Exception)
        {
            // Same rule as Record: an instrument may not fail the run it watches.
        }
    }

    private static readonly object SurfaceLock = new();
    private static readonly HashSet<object> SeenSurfaces = new(ReferenceEqualityComparer.Instance);

    private static void Record(RoutedEventArgs args, string modality)
    {
        try
        {
            if (args.Source is not Control source) return;

            var root = source.FindLogicalAncestorOfType<TopLevel>(includeSelf: true);
            if (root == null) return;
            // A bare `new Window()` is test scaffolding, not application surface;
            // counting its buttons would put rows in the gap list that no user
            // can ever reach.
            if (root.GetType() == typeof(Window)) return;
            var surface = root.GetType().Name;
            NoteSurface(root);
            var commandNames = GuiInteractionNaming.CommandNameMap(root.DataContext);

            // ⚠️ The event source on a Button click is the inner TextBlock or
            // ContentPresenter from the control template, never the Button. Not
            // walking up here would leave the numerator at approximately zero
            // while every assertion in this file still passed.
            for (Control? node = source; node != null;
                 node = node.GetLogicalParent() as Control)
            {
                var described = GuiInteractionNaming.Describe(node, surface, commandNames);
                if (described != null)
                {
                    Note(described.Id, modality);
                    break;
                }
            }

            if (modality == "key" && args is KeyEventArgs key)
                NoteGesture(root, surface, key);
        }
        catch (Exception)
        {
            // A recorder must never be able to fail a test it is only watching.
            // A dropped observation reads as uncovered — the safe direction.
        }
    }

    /// <summary>
    /// A keyboard shortcut is declared as <c>InputGesture</c> on a menu item and
    /// dispatched by <c>MainWindow_KeyDown</c>, so it has no element of its own.
    /// Record the gesture separately when the key event matches one the surface
    /// declares — otherwise the 28 declared shortcuts could never be covered by
    /// anything.
    /// </summary>
    private static void NoteGesture(TopLevel root, string surface, KeyEventArgs key)
    {
        var gesture = new KeyGesture(key.Key, key.KeyModifiers);
        foreach (var declared in DeclaredGestures(root))
        {
            if (declared.Matches(key) || declared.Equals(gesture))
                Note($"{surface}/Gesture:{GuiInteractionNaming.Format(declared)}", "key");
        }
    }

    private static IEnumerable<KeyGesture> DeclaredGestures(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is MenuItem { InputGesture: { } g }) yield return g;
            foreach (var child in node.LogicalChildren) stack.Push(child);
        }
    }

    private static void Note(string id, string modality)
    {
        var line = $"{id}\t{modality}";
        if (!Observed.TryAdd(line, 0)) return;

        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPath)!);
                File.AppendAllText(ArtifactPath, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// <c>artifacts/gui-coverage/</c> at the repository root, found by walking up
    /// from the test binary — the test working directory is
    /// <c>bin/Debug/net10.0</c> and a relative path there is not somewhere a
    /// script would think to look.
    /// </summary>
    internal static string RepoArtifactsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "artifacts", "gui-coverage");
    }

    private sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
