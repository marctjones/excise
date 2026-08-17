using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #979 — command-line/file-association launch had no xunit method driving it
/// end to end: the only coverage was <c>scripts/run-packaged-gui-smoke.sh
/// --mode direct-exec</c> (a shell script) and
/// (a source-text check that the docs mention the handler, not that it works).
///
/// <see cref="Excise.App.StartupDocumentResolver"/> is already unit-tested
/// (arg-parsing precedence, PSN skip, file URIs, missing/non-pdf rejection —
/// see <c>StartupDocumentResolverTests</c>), but that only proves a path can be
/// EXTRACTED from argv; it never proves the extracted path actually reaches
/// <c>MainWindowViewModel.LoadDocumentAsync</c>. <c>App.OpenPathAsync</c> and
/// <c>App.ResolveActivatedPdfPath</c> — the two halves of that "resolved path
/// -&gt; loaded document" glue used by both the command-line launch path and the
/// macOS file-activation path in <c>App.OnFrameworkInitializationCompleted</c>
/// — were <c>private static</c> and therefore unreachable from a test. #979
/// made them <c>internal</c> (behavior-preserving; <c>InternalsVisibleTo</c>
/// already covers this project) so these tests call the REAL production
/// methods rather than re-implementing their logic.
///
/// What this does NOT cover, and is not claimed to: spawning the actual OS
/// process, <c>Environment.GetCommandLineArgs()</c> from a real launch, the
/// <c>desktop.Startup</c> dispatcher-timer scheduling, and macOS Launch
/// Services actually delivering an <c>IActivatableLifetime.Activated</c>
/// event. Those remain the shell script's job
/// (<c>scripts/run-packaged-gui-smoke.sh --mode direct-exec</c>) — see the
/// updated note on this capability in
/// <see cref="GuiWorkflowCoverageMatrixTests"/>.
/// </summary>
[Collection("AvaloniaTests")]
public class StartupActivationWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task CommandLineArgs_ResolvedPath_ActuallyLoadsIntoTheViewModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-startup-activation-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(path, "STARTUP ACTIVATION VIA COMMAND LINE");
        try
        {
            // Mirrors App.OnFrameworkInitializationCompleted's
            // `StartupDocumentResolver.Resolve(desktop.Args, processArgs)` call:
            // a platform option ahead of the real path, exactly like a user
            // running `excise --some-flag file.pdf`.
            var processArgs = new[] { "--some-flag", path };
            var resolved = Excise.App.StartupDocumentResolver.Resolve(lifetimeArgs: null, processArgs);
            resolved.Should().Be(Path.GetFullPath(path), "sanity: the resolver must find the path before we test loading it");

            var vm = new MainWindowViewModel();
            vm.IsDocumentLoaded.Should().BeFalse("fixture sanity — nothing loaded yet");

            await Excise.App.App.OpenPathAsync(vm, resolved!, NullLogger.Instance);

            vm.IsDocumentLoaded.Should().BeTrue(
                "the path StartupDocumentResolver extracts from command-line args must actually end up loaded — " +
                "that hookup, not just the arg-parsing, is what 'open via command line' means");
            vm.TotalPages.Should().Be(1);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task FileAssociationActivation_ResolvedPath_ActuallyLoadsIntoTheViewModel()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-startup-activation-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "STARTUP ACTIVATION VIA FILE ASSOCIATION");
        var missingPdfPath = Path.Combine(Path.GetTempPath(), $"excise-startup-activation-missing-{Guid.NewGuid():N}.pdf");
        var textPath = Path.Combine(Path.GetTempPath(), $"excise-startup-activation-{Guid.NewGuid():N}.txt");
        File.WriteAllText(textPath, "not a pdf");
        try
        {
            // Mirrors the macOS double-click / "Open With" path: Avalonia
            // delivers FileActivatedEventArgs.Files, a list of IStorageItem.
            // A non-pdf file and a pdf path that doesn't exist on disk are
            // included to prove ResolveActivatedPdfPath skips both rather
            // than picking the first entry blindly.
            IReadOnlyList<IStorageItem> files = new[]
            {
                MockStorageItem(textPath),
                MockStorageItem(missingPdfPath),
                MockStorageItem(pdfPath),
            };

            var resolved = Excise.App.App.ResolveActivatedPdfPath(files);
            resolved.Should().Be(Path.GetFullPath(pdfPath),
                "it must skip the non-pdf file and the pdf that doesn't exist, and pick the real one");

            var vm = new MainWindowViewModel();
            await Excise.App.App.OpenPathAsync(vm, resolved!, NullLogger.Instance);

            vm.IsDocumentLoaded.Should().BeTrue(
                "the path resolved from a macOS file-activation event must actually end up loaded");
            vm.TotalPages.Should().Be(1);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
            TestPdfGenerator.CleanupTestFile(textPath);
        }
    }

    private static IStorageItem MockStorageItem(string path)
    {
        var mock = new Mock<IStorageItem>();
        mock.Setup(f => f.Path).Returns(new Uri(new FileInfo(path).FullName));
        mock.Setup(f => f.Name).Returns(Path.GetFileName(path));
        return mock.Object;
    }
}
