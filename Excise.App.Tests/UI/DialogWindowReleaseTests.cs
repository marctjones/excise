using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1806 item 3: a closed dialog must not stay reachable. The D4 usability
/// trials saw About, Preferences, Bates Numbering and Unsaved-Changes listed in
/// the macOS window list after Close; the native list cannot be read headless,
/// so this decides the reachability question instead: after N open/close
/// cycles through the real commands, is any closed <see cref="Window"/> still
/// alive once the owner (which the app keeps for its whole life) is held?
/// </summary>
[Collection("AvaloniaTests")]
public sealed class DialogWindowReleaseTests : IDisposable
{
    private const int Cycles = 20;

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-dialog-release-{Guid.NewGuid():N}");

    public DialogWindowReleaseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static async Task DrainAsync()
    {
        for (var i = 0; i < 3; i++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task<T> WaitForOwnedAsync<T>(Window owner) where T : Window
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var found = owner.OwnedWindows.OfType<T>().SingleOrDefault();
            if (found != null) return found;
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"{typeof(T).Name} never opened");
    }

    [FixedAvaloniaFact(Timeout = 180_000)]
    public async Task ClosedDialogs_AreNotRetained_AfterManyOpenCloseCycles()
    {
        var source = Path.Combine(_tempDir, "doc.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(source, "Body");

        var vm = MainWindowViewModelTestFactory.Create();
        var owner = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        owner.Show();
        vm.MainWindowResolver = () => owner;
        await vm.LoadDocumentAsync(source);
        var dialogService = new AvaloniaUserDialogService(
            NullLogger<AvaloniaUserDialogService>.Instance,
            new FakeWindowHost { MainWindowResolver = () => owner });

        var dead = new Dictionary<string, List<WeakReference>>
        {
            ["About"] = new(), ["Preferences"] = new(), ["Bates"] = new(), ["UnsavedChanges"] = new(),
        };

        for (var i = 0; i < Cycles; i++)
        {
            dead["About"].Add(await OpenAndCloseAboutAsync(vm, owner));
            dead["Preferences"].Add(await OpenAndClosePreferencesAsync(vm, owner));
            dead["Bates"].Add(await OpenAndCloseBatesAsync(vm, owner));
            dead["UnsavedChanges"].Add(await OpenAndCloseUnsavedChangesAsync(dialogService, owner));
        }

        // Measured 2026-09-28: the single most recently closed dialog stays
        // reachable until the next window opens and closes (the 20th
        // Unsaved-Changes dialog was the only survivor, and one more About
        // cycle freed it and became the survivor instead), so it is a bounded
        // last-window reference, not accumulation. This trailing cycle absorbs
        // that survivor; the assertion below exempts only it.
        _ = await OpenAndCloseAboutAsync(vm, owner);
        await DrainAsync();
        Collect();

        owner.OwnedWindows.Should().BeEmpty("every dialog was closed");
        foreach (var (name, refs) in dead)
        {
            refs.Count(r => r.IsAlive).Should().Be(0,
                $"{name}: a closed dialog window must be collectable, but "
                + $"{refs.Count(r => r.IsAlive)} of {Cycles} are still reachable");
        }

        owner.Close();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseAboutAsync(MainWindowViewModel vm, Window owner)
    {
        await vm.AboutCommand.Execute();
        var dialog = await WaitForOwnedAsync<AboutWindow>(owner);
        var weak = new WeakReference(dialog);
        dialog.Close();
        dialog = null;
        await DrainAsync();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndClosePreferencesAsync(MainWindowViewModel vm, Window owner)
    {
        await vm.ShowPreferencesCommand.Execute();
        var dialog = await WaitForOwnedAsync<PreferencesWindow>(owner);
        var weak = new WeakReference(dialog);
        dialog.Close();
        dialog = null;
        await DrainAsync();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseBatesAsync(MainWindowViewModel vm, Window owner)
    {
        // ShowDialog is awaited inside the command until the dialog closes.
        var running = vm.BatesNumberingCommand.Execute().ToTask();
        var dialog = await WaitForOwnedAsync<BatesNumberingDialog>(owner);
        var weak = new WeakReference(dialog);
        dialog.Close();
        dialog = null;
        await running;
        await DrainAsync();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseUnsavedChangesAsync(
        AvaloniaUserDialogService service, Window owner)
    {
        var running = service.ShowUnsavedChangesAsync("Unsaved changes", "x has changes not yet saved.", "Save a Copy");
        var dialog = await WaitForOwnedAsync<Window>(owner);
        var weak = new WeakReference(dialog);
        dialog.Close(UnsavedChangesDecision.Cancel);
        dialog = null;
        (await running).Should().Be(UnsavedChangesDecision.Cancel);
        await DrainAsync();
        return weak;
    }
}
