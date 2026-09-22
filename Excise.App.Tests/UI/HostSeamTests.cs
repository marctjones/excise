using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using AwesomeAssertions;
using Excise.App.Services.Host;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Moq;
using Xunit;

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

    // The two source scans that lived here (#1500: no Application.Current read and
    // no direct window.json/zoom.txt/recent.txt access in a view model) moved to
    // scripts/check-viewmodel-seams.sh, a t0 gate (#1773): they read source text
    // and need nothing from this host.
}
