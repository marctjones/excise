using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using AwesomeAssertions;
using Excise.App.Services.Host;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Moq;
using Xunit;
using Excise.TestSupport;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1500 step 1: <see cref="IFilePicker"/>, <see cref="IWindowHost"/> and
/// <see cref="ITextClipboard"/>.
/// </summary>
/// <remarks>
/// <para>
/// These pin things that were NOT observable before the seams existed. A
/// command's picker request — its title, file-type filters, suggested filename
/// and start directory — was built inline as Avalonia
/// <c>FilePicker*Options</c> and handed straight to a sealed
/// <c>IStorageProvider</c>; the nine <c>*Override</c> test seams could only
/// intercept the PATH that came back, never the ask. Likewise
/// <c>ExitCommand</c>'s shutdown request went to
/// <c>Application.Current.ApplicationLifetime</c>, which is null in a headless
/// host and settable only once, so "did Exit try to quit?" had no answer.
/// </para>
/// <para>
/// The requests asserted here are the pre-#1500 strings verbatim. That is the
/// point: this is a structural step, so these tests are the record of what the
/// inline options said.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class HostSeamTests
{
    [Fact]
    public void StorageProviderOverride_ForwardsToTheWindowHost()
    {
        var host = new FakeWindowHost();
        var vm = MainWindowViewModelTestFactory.Create(windowHost: host);

        vm.StorageProviderOverride.Should().BeNull("nothing is overridden yet");

        var provider = new Mock<IStorageProvider>().Object;
        vm.StorageProviderOverride = provider;

        // The public seam must land on the host the picker resolves from,
        // otherwise every existing test that sets it would steer nothing.
        host.StorageProviderOverride.Should().BeSameAs(provider);
        host.StorageProvider.Should().BeSameAs(provider);
        vm.StorageProviderOverride.Should().BeSameAs(provider, "the property reads back through the host");
    }

    [Fact]
    public void MainWindowResolver_ForwardsToTheWindowHost()
    {
        var host = new FakeWindowHost();
        var vm = MainWindowViewModelTestFactory.Create(windowHost: host);

        var sentinelRan = false;
        vm.MainWindowResolver = () => { sentinelRan = true; return null; };

        _ = host.MainWindow;

        sentinelRan.Should().BeTrue("the view model's resolver seam is the host's resolver");
    }

    [FixedAvaloniaFact]
    public async Task ExitCommand_WithNothingUnsaved_AsksTheHostToShutDown()
    {
        // Previously unobservable: the real path read a desktop
        // ApplicationLifetime that a headless test cannot create.
        var host = new FakeWindowHost();
        var vm = MainWindowViewModelTestFactory.Create(windowHost: host);

        await vm.ExitCommand.Execute();

        host.ShutdownRequests.Should().Be(1);
    }

    [FixedAvaloniaFact]
    public async Task OpenFileCommand_AsksForASinglePdf()
    {
        var picker = new RecordingFilePicker();   // defaults to "user cancelled"
        var vm = MainWindowViewModelTestFactory.Create(filePicker: picker);

        await vm.OpenFileCommand.Execute();

        var request = picker.LastOpenRequest;
        request.Should().NotBeNull();
        request!.Title.Should().Be("Open PDF File");
        request.AllowMultiple.Should().BeFalse();
        request.Filters.Should().ContainSingle()
            .Which.Patterns.Should().Equal("*.pdf");

        vm.IsDocumentLoaded.Should().BeFalse("the picker was cancelled");
    }

    [FixedAvaloniaFact]
    public async Task OpenFileCommand_OpensNothing_WhenNoStorageProviderIsAvailable()
    {
        // The production adapter with a host that has neither a window nor an
        // override: the command must fall through quietly, exactly as the
        // pre-#1500 "storage provider unavailable" early return did.
        var vm = MainWindowViewModelTestFactory.Create(windowHost: new FakeWindowHost());

        await vm.OpenFileCommand.Execute();

        vm.IsDocumentLoaded.Should().BeFalse();
    }

    [FixedAvaloniaFact]
    public async Task CopyTextCommand_WithNoDocument_DoesNotTouchTheClipboard()
    {
        var clipboard = new FakeTextClipboard();
        var vm = MainWindowViewModelTestFactory.Create(clipboard: clipboard);

        await vm.CopyTextCommand.Execute();

        clipboard.SetCount.Should().Be(0);
        vm.ClipboardHistory.Should().BeEmpty();
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopy_RecordsHistory_EvenWhenTheOsClipboardIsUnavailable()
    {
        // The "recorded in history only" branch. Before ITextClipboard this could
        // only be reached by having no desktop lifetime at all, so the two
        // branches were indistinguishable from a test.
        var clipboard = new FakeTextClipboard { IsAvailable = false };
        var vm = MainWindowViewModelTestFactory.Create(clipboard: clipboard);

        await vm.SetSelectedTextAndCopyAsync("hello");

        clipboard.SetCount.Should().Be(1, "the copy was attempted");
        clipboard.Text.Should().BeNull("...and reported unavailable");
        vm.ClipboardHistory.Should().ContainSingle()
            .Which.Text.Should().Be("hello", "history is independent of the OS clipboard");
    }

    [FixedAvaloniaFact]
    public async Task SetSelectedTextAndCopy_PutsTheTextOnAnAvailableClipboard()
    {
        var clipboard = new FakeTextClipboard();
        var vm = MainWindowViewModelTestFactory.Create(clipboard: clipboard);

        await vm.SetSelectedTextAndCopyAsync("hello");

        clipboard.Text.Should().Be("hello");
    }

    /// <summary>
    /// The redacted-copy save request. Its title, its MIME-typed "PDF Document"
    /// filter (which differs from every other PDF picker in the app) and its
    /// start-beside-the-source behaviour were previously buried in a private
    /// method that needed a real <c>Window</c> to call.
    /// </summary>
    [Fact]
    public void RedactedSaveRequest_StatesThePendingCountAndOpensBesideTheSource()
    {
        var picker = new RecordingFilePicker();
        var vm = MainWindowViewModelTestFactory.Create(filePicker: picker);

        var suggested = Path.Combine(Path.GetTempPath(), "case-notes.pdf");
        var request = vm.BuildRedactedSaveRequest(suggested);

        request.Title.Should().Be("Save Redacted PDF (0 areas will be redacted)");
        request.DefaultExtension.Should().Be("pdf");
        request.SuggestedFileName.Should().Be("case-notes.pdf");
        request.SuggestedStartDirectory.Should().Be(Path.GetDirectoryName(suggested));

        var filter = request.Filters.Should().ContainSingle().Subject;
        filter.Name.Should().Be("PDF Document");
        filter.Patterns.Should().Equal("*.pdf");
        filter.MimeTypes.Should().NotBeNull();
        filter.MimeTypes!.Should().Equal("application/pdf");
    }

    [Fact]
    public void PdfFilter_IsOneDefinitionSharedByEveryPdfPicker()
    {
        // Two call sites offering different patterns for "a PDF" is the drift
        // the shared filters exist to prevent.
        FilePickerFilters.Pdf.Name.Should().Be("PDF Files");
        FilePickerFilters.Pdf.Patterns.Should().Equal("*.pdf");
        FilePickerFilters.Pdf.MimeTypes.Should().BeNull();

        FilePickerFilters.Pkcs12Certificate.Patterns.Should().Equal("*.p12", "*.pfx");
        FilePickerFilters.Images.Patterns.Should()
            .Equal("*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp");
        FilePickerFilters.Png.Patterns.Should().Equal("*.png");
        FilePickerFilters.Jpeg.Patterns.Should().Equal("*.jpg", "*.jpeg");
    }

    /// <summary>
    /// A new <c>Application.Current</c> read in a view model would put host
    /// access back where #1500 step 1 took it from, and no behavioural test
    /// would fail. So the source is scanned, in the style of
    /// <c>StoragePickerRoutingTests</c>.
    /// </summary>
    [Fact]
    public void NoViewModel_ReadsApplicationLifetimeDirectly_ExceptTheDocumentedOne()
    {
        var offenders = ScanViewModels(new Regex(@"Application\s*\.\s*Current", RegexOptions.Compiled));

        // ShowErrorDialogAsync builds a raw Avalonia Window and owns it against
        // the lifetime's main window rather than MainWindowResolver. Routing it
        // through IWindowHost would make a test that sets the resolver show a
        // real modal dialog on a failed open (GuiClickSafetySweepTests sets the
        // resolver and clicks everything), which a structural step must not do.
        // The design deletes this method outright in its step 13.
        offenders.Should().HaveCount(1,
            "only ShowErrorDialogAsync may still read the lifetime; it is deleted by the code-behind step. "
            + "Found: " + string.Join(" | ", offenders));
        offenders[0].Should().Contain("MainWindowViewModel.cs");
    }

    /// <summary>
    /// The same guard for persistence (#1500 step 2): a view model must reach
    /// <c>window.json</c>, <c>zoom.txt</c> and <c>recent.txt</c> only through
    /// the injected stores.
    /// </summary>
    [Fact]
    public void NoViewModel_ReadsOrWritesPersistedSettingsDirectly()
    {
        var pattern = new Regex(@"WindowSettings\s*\.\s*(Load|Update)\s*\(|AppPaths\s*\.", RegexOptions.Compiled);

        ScanViewModels(pattern).Should().BeEmpty(
            "window.json / zoom.txt / recent.txt go through ISettingsStore and IRecentFilesStore, "
            + "so a test can supply an in-memory store and the view-mode leak class loses its mechanism");
    }

    private static List<string> ScanViewModels(Regex pattern)
    {
        var root = FindRepoRoot();
        var viewModels = Path.Combine(root, "Excise.App", "ViewModels");
        Directory.Exists(viewModels).Should().BeTrue("fixture: the view-model folder must exist");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(viewModels, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments and doc comments talk ABOUT these names on purpose.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("///", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(line))
                    offenders.Add($"{relative}:{i + 1}: {line.Trim()}");
            }
        }

        return offenders;
    }

    // #1706 — TestRepoLayout, not a hand-rolled walk to .git/excise.sln. LOCAL
    // checkout, deliberately: this reads THIS worktree's own source / writes its
    // own artifacts, and the main checkout may be on a different branch.
    private static string FindRepoRoot() =>
        TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException("Could not find repository root.");
}
