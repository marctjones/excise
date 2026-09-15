using System;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1477: the native half of the file-panel accessory cleanup. Its real effect,
/// stopping the idle AppKit layout loop, needs a live GUI session and is not
/// provable here. What is checked is that it is safe to call anywhere, and that
/// every runtime entry, class, and selector it depends on resolves.
/// </summary>
/// <remarks>
/// Loading AppKit into the test host is safe. <c>dlopen</c> runs no
/// <c>NSApplication</c> initialisation and opens no WindowServer connection;
/// only <c>+[NSApplication sharedApplication]</c> or a first window would, and
/// the cleanup deliberately reads the exported <c>NSApp</c> global instead of
/// calling <c>sharedApplication</c>. This test only loads AppKit to prove the
/// class names resolve.
/// </remarks>
[Collection("AvaloniaTests")]
public class MacFilePanelAccessoryCleanupTests
{
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [Fact]
    public void RunOnMainThread_InTheTestHost_IsASafeNoOp()
    {
        var result = MacFilePanelAccessoryCleanup.RunOnMainThread(logger: null);

        result.PanelsCleared.Should().Be(0);
        result.AccessoryWindowsOrderedOut.Should().Be(0);
        if (OperatingSystem.IsMacOS())
        {
            // The test body is not on the process main thread, and the host has
            // no NSApp: either way the sweep must not run.
            result.Outcome.Should().BeOneOf("not-main-thread", "appkit-not-loaded", "no-nsapp");
        }
        else
        {
            result.Outcome.Should().Be("not-macos");
        }
    }

    /// <summary>
    /// On cancel, Avalonia never calls <c>orderOut:</c>, so the first pass can
    /// meet the panel mid-dismissal, still visible. That pass must retry rather
    /// than report success, and the retries must stop.
    /// </summary>
    [Fact]
    public void ShouldRetry_OnlyWhileAFinishedPanelIsStillVisible_AndIsBounded()
    {
        var stillAnimatingOut = new MacFilePanelAccessoryCleanup.Result("swept", 0, 1, 0);
        var cleared = new MacFilePanelAccessoryCleanup.Result("swept", 1, 0, 1);
        var nothingThere = new MacFilePanelAccessoryCleanup.Result("swept", 0, 0, 0);

        MacFilePanelAccessoryCleanup.ShouldRetry(stillAnimatingOut, attempt: 1).Should().BeTrue(
            "a cancelled sheet is still visible on the first pass");
        MacFilePanelAccessoryCleanup.ShouldRetry(cleared, attempt: 1).Should().BeFalse();
        MacFilePanelAccessoryCleanup.ShouldRetry(nothingThere, attempt: 1).Should().BeFalse(
            "an unfiltered picker leaves nothing to clear");
        MacFilePanelAccessoryCleanup.ShouldRetry(MacFilePanelAccessoryCleanup.Result.Skipped("no-nsapp"), attempt: 1)
            .Should().BeFalse("a binding problem does not fix itself by waiting");
        MacFilePanelAccessoryCleanup.ShouldRetry(stillAnimatingOut, MacFilePanelAccessoryCleanup.MaxAttempts)
            .Should().BeFalse("retries are bounded");
        MacFilePanelAccessoryCleanup.RetryDelay.Should().BeGreaterThan(TimeSpan.FromMilliseconds(400),
            "the retry must outlast AppKit's sheet-dismissal animation");
    }

    [Fact]
    public void Schedule_InTheTestHost_DoesNotThrow()
    {
        var act = () => MacFilePanelAccessoryCleanup.Schedule(logger: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void RuntimeEntries_ClassesAndSelectors_Resolve()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The ObjC runtime and AppKit exist only on macOS.");

        MacFilePanelAccessoryCleanup.TryResolveApi().Should().BeTrue(
            "libobjc exports objc_getClass/sel_registerName/objc_msgSend/autorelease pools and libSystem exports pthread_main_np");

        foreach (var selector in new[]
                 {
                     "respondsToSelector:", "isKindOfClass:", "windows", "count", "objectAtIndex:",
                     "isVisible", "accessoryView", "setAccessoryView:", "window", "orderOut:",
                 })
        {
            MacFilePanelAccessoryCleanup.Selector(selector).Should().NotBe(0, $"selector {selector} must register");
        }

        NativeLibrary.TryLoad(AppKitPath, out _).Should().BeTrue("fixture: AppKit loads on every supported macOS");
        MacFilePanelAccessoryCleanup.Class("NSApplication").Should().NotBe(0);
        MacFilePanelAccessoryCleanup.Class("NSSavePanel").Should().NotBe(0);
        MacFilePanelAccessoryCleanup.Class("NSOpenPanel").Should().NotBe(0);

        var appKit = NativeLibrary.Load(AppKitPath);
        NativeLibrary.TryGetExport(appKit, "NSApp", out var nsAppSlot).Should().BeTrue(
            "the cleanup reads the NSApp data symbol rather than creating an application");
        nsAppSlot.Should().NotBe(0);

        // NSAccessoryViewWindow is a private AppKit class. Pass 2 is skipped
        // when it is absent, so it is reported here rather than asserted.
        var accessoryWindowClass = MacFilePanelAccessoryCleanup.Class("NSAccessoryViewWindow");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"NSAccessoryViewWindow class {(accessoryWindowClass != 0 ? "resolves" : "is not registered")} on this macOS");

        // Even with AppKit loaded, the test host has no NSApp and is off the
        // main thread, so a pass still does nothing.
        MacFilePanelAccessoryCleanup.RunOnMainThread(logger: null).PanelsCleared.Should().Be(0);
    }
}
