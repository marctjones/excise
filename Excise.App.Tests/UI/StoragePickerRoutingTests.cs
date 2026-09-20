using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Moq;
using Xunit;
using Excise.TestSupport;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1477: every file Open and Save picker must go through
/// <see cref="StoragePickers"/>, and that helper must run the macOS
/// accessory-view cleanup after a chosen file, a cancel, and an exception.
/// A picker that skips the cleanup leaves Avalonia's file-type accessory
/// looping in AppKit layout, which measured 4.6-5.3% idle CPU for the rest of
/// the session.
/// </summary>
[Collection("AvaloniaTests")]
public class StoragePickerRoutingTests
{
    private const string HelperRelativePath = "Excise.App/Services/StoragePickers.cs";

    [Fact]
    public async Task OpenFilesAsync_RunsCleanupOnce_WhenAFileIsChosen()
    {
        var provider = new Mock<IStorageProvider>();
        IReadOnlyList<IStorageFile> chosen = new[] { new Mock<IStorageFile>().Object };
        provider.Setup(p => p.OpenFilePickerAsync(It.IsAny<FilePickerOpenOptions>())).ReturnsAsync(chosen);

        var (result, cleanups) = await WithCountingHook(() =>
            StoragePickers.OpenFilesAsync(provider.Object, PdfOpenOptions()));

        result.Should().BeSameAs(chosen);
        cleanups.Should().Be(1);
    }

    [Fact]
    public async Task OpenFilesAsync_RunsCleanupOnce_WhenCancelled()
    {
        var provider = new Mock<IStorageProvider>();
        provider.Setup(p => p.OpenFilePickerAsync(It.IsAny<FilePickerOpenOptions>()))
            .ReturnsAsync(Array.Empty<IStorageFile>());

        var (result, cleanups) = await WithCountingHook(() =>
            StoragePickers.OpenFilesAsync(provider.Object, PdfOpenOptions()));

        result.Should().BeEmpty();
        cleanups.Should().Be(1);
    }

    [Fact]
    public async Task OpenFilesAsync_RunsCleanupOnce_AndRethrows_WhenThePickerThrows()
    {
        var provider = new Mock<IStorageProvider>();
        provider.Setup(p => p.OpenFilePickerAsync(It.IsAny<FilePickerOpenOptions>()))
            .ThrowsAsync(new InvalidOperationException("picker failed"));

        var cleanups = await CountCleanupsExpectingThrow(() =>
            StoragePickers.OpenFilesAsync(provider.Object, PdfOpenOptions()));

        cleanups.Should().Be(1, "the cleanup runs in a finally, so a failed picker still tears the accessory down");
    }

    [Fact]
    public async Task SaveFileAsync_RunsCleanupOnce_WhenAFileIsChosen()
    {
        var provider = new Mock<IStorageProvider>();
        var chosen = new Mock<IStorageFile>().Object;
        provider.Setup(p => p.SaveFilePickerAsync(It.IsAny<FilePickerSaveOptions>())).ReturnsAsync(chosen);

        var (result, cleanups) = await WithCountingHook(() =>
            StoragePickers.SaveFileAsync(provider.Object, PdfSaveOptions()));

        result.Should().BeSameAs(chosen);
        cleanups.Should().Be(1);
    }

    [Fact]
    public async Task SaveFileAsync_RunsCleanupOnce_WhenCancelled()
    {
        var provider = new Mock<IStorageProvider>();
        provider.Setup(p => p.SaveFilePickerAsync(It.IsAny<FilePickerSaveOptions>())).ReturnsAsync((IStorageFile?)null);

        var (result, cleanups) = await WithCountingHook(() =>
            StoragePickers.SaveFileAsync(provider.Object, PdfSaveOptions()));

        result.Should().BeNull();
        cleanups.Should().Be(1);
    }

    [Fact]
    public async Task SaveFileAsync_RunsCleanupOnce_AndRethrows_WhenThePickerThrows()
    {
        var provider = new Mock<IStorageProvider>();
        provider.Setup(p => p.SaveFilePickerAsync(It.IsAny<FilePickerSaveOptions>()))
            .ThrowsAsync(new InvalidOperationException("picker failed"));

        var cleanups = await CountCleanupsExpectingThrow(() =>
            StoragePickers.SaveFileAsync(provider.Object, PdfSaveOptions()));

        cleanups.Should().Be(1, "the cleanup runs in a finally, so a failed picker still tears the accessory down");
    }

    /// <summary>
    /// The Open command, the path the live session reproduced on, is routed
    /// through the helper: cancelling its <c>*.pdf</c>-filtered picker runs
    /// the cleanup.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task OpenFileCommand_CancelledFilteredPicker_RunsCleanupOnce()
    {
        var provider = new Mock<IStorageProvider>();
        FilePickerOpenOptions? seen = null;
        provider.Setup(p => p.OpenFilePickerAsync(It.IsAny<FilePickerOpenOptions>()))
            .Callback<FilePickerOpenOptions>(o => seen = o)
            .ReturnsAsync(Array.Empty<IStorageFile>());

        var vm = MainWindowViewModelTestFactory.Create();
        vm.StorageProviderOverride = provider.Object;

        int cleanups = 0;
        StoragePickers.AfterPickerHookForTests = () => cleanups++;
        try
        {
            await vm.OpenFileCommand.Execute();
        }
        finally
        {
            StoragePickers.AfterPickerHookForTests = null;
        }

        seen.Should().NotBeNull("fixture: the Open command must reach the picker");
        seen!.FileTypeFilter.Should().NotBeNullOrEmpty("the PDF filter is kept; #1477 must not drop it");
        cleanups.Should().Be(1);
        vm.IsDocumentLoaded.Should().BeFalse();
    }

    /// <summary>
    /// A new picker call that bypasses the helper would bring the idle-CPU loop
    /// back without failing any behavioural test, so the source is scanned.
    /// </summary>
    [Fact]
    public void NoProductSource_CallsTheFilePickersDirectly_OutsideStoragePickers()
    {
        var root = FindRepoRoot();
        var pattern = new Regex(@"\.(OpenFilePickerAsync|SaveFilePickerAsync)\s*\(", RegexOptions.Compiled);
        var helper = Path.GetFullPath(Path.Combine(root, HelperRelativePath));

        File.Exists(helper).Should().BeTrue("fixture: the helper lives at " + HelperRelativePath);
        pattern.IsMatch(File.ReadAllText(helper)).Should().BeTrue("fixture: the scan pattern must match the helper's own calls");

        var offenders = new List<string>();
        foreach (var project in new[] { "Excise.App", "Excise.Avalonia" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/") || full == helper)
                    continue;

                var lines = File.ReadAllLines(full);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (pattern.IsMatch(lines[i]))
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every file Open/Save picker must go through StoragePickers so the macOS accessory cleanup runs (#1477)");
    }

    private static async Task<(T Result, int Cleanups)> WithCountingHook<T>(Func<Task<T>> call)
    {
        int cleanups = 0;
        StoragePickers.AfterPickerHookForTests = () => cleanups++;
        try
        {
            var result = await call();
            return (result, cleanups);
        }
        finally
        {
            StoragePickers.AfterPickerHookForTests = null;
        }
    }

    private static async Task<int> CountCleanupsExpectingThrow<T>(Func<Task<T>> call)
    {
        int cleanups = 0;
        StoragePickers.AfterPickerHookForTests = () => cleanups++;
        try
        {
            var act = async () => await call();
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("picker failed");
            return cleanups;
        }
        finally
        {
            StoragePickers.AfterPickerHookForTests = null;
        }
    }

    private static FilePickerOpenOptions PdfOpenOptions() => new()
    {
        Title = "Open PDF File",
        FileTypeFilter = new[] { new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } } },
    };

    private static FilePickerSaveOptions PdfSaveOptions() => new()
    {
        Title = "Save PDF",
        FileTypeChoices = new[] { new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } } },
    };

    // #1706 — TestRepoLayout, not a hand-rolled walk to .git/excise.sln. LOCAL
    // checkout, deliberately: this reads THIS worktree's own source / writes its
    // own artifacts, and the main checkout may be on a different branch.
    private static string FindRepoRoot() =>
        TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException("Could not find repository root.");
}
