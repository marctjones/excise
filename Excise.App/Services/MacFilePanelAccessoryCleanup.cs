using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Excise.App.Services;

/// <summary>
/// Removes the file-type accessory view that Avalonia's macOS picker leaves on a
/// finished <c>NSSavePanel</c>/<c>NSOpenPanel</c> (#1477). The ObjC runtime is
/// bound through <see cref="NativeLibrary"/> and function pointers, as in
/// <see cref="MacMemoryPressureSource"/>, so there is no native shim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the panel outlives the picker.</b> Avalonia 12.0.4 through 12.1.2
/// <c>native/Avalonia.Native/src/OSX/StorageProvider.mm</c> keeps its
/// <c>ExtensionDropdownHandler</c> in a <c>__strong</c> ivar on the singleton
/// <c>StorageProvider</c>. The handler's <c>NSSavePanel* _dialog</c> ivar is
/// commented "Weak reference" but is strong under ARC (the project builds with
/// <c>CLANG_ENABLE_OBJC_ARC = YES</c>). So the last filtered panel stays alive,
/// with its accessory view and <c>NSAccessoryViewWindow</c>, until the next
/// filtered picker replaces the handler. Nothing removes the accessory view, and
/// the cancel path never even calls <c>orderOut:</c>. A live <c>sample</c> shows
/// the main thread looping through
/// <c>accessoryWindowOfViewWillUpdateConstraintsIfNeeded</c> and
/// <c>NSDisplayCycleFlush</c>.
/// </para>
/// <para>
/// <b>What this does.</b> It works through <c>[NSApp windows]</c> in two passes:
/// <list type="number">
/// <item>Every panel that is a kind of <c>NSSavePanel</c> (the KVO subclass and
/// <c>NSOpenPanel</c> included), is not visible, and still has an accessory
/// view: detach the view with <c>setAccessoryView:nil</c> and <c>orderOut:</c>
/// the window that hosted it.</item>
/// <item>If no file panel is visible at all: <c>orderOut:</c> any remaining
/// <c>NSAccessoryViewWindow</c>. That class is private, so the pass is skipped
/// when <c>objc_getClass</c> cannot find it.</item>
/// </list>
/// Nothing is closed or released. The panel's lifetime belongs to Avalonia, and
/// <c>releasedWhenClosed</c> on an AppKit-internal window is unknown, so a close
/// could leave a dangling pointer, which is worse than the loop.
/// </para>
/// <para>
/// <b>Rules.</b> The work is posted to the UI dispatcher at background priority,
/// never run inline. Avalonia completes the picker's task from inside the native
/// completion block, so an inline run could reach the panel before AppKit has
/// finished dismissing it. The class runs on the main thread only, checks
/// <c>respondsToSelector:</c> before every send, never creates an
/// <c>NSApplication</c> (it reads the exported <c>NSApp</c> global and does
/// nothing when that is nil), catches everything, and logs a failure once.
/// </para>
/// </remarks>
internal static unsafe class MacFilePanelAccessoryCleanup
{
    private const string LibObjcPath = "/usr/lib/libobjc.A.dylib";
    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly object ApiGate = new();
    private static bool s_apiResolved;
    private static bool s_apiAvailable;
    private static int s_failureLogged;

    private static delegate* unmanaged[Cdecl]<byte*, nint> s_getClass;
    private static delegate* unmanaged[Cdecl]<byte*, nint> s_registerName;
    private static delegate* unmanaged[Cdecl]<nint> s_poolPush;
    private static delegate* unmanaged[Cdecl]<nint, void> s_poolPop;
    private static delegate* unmanaged[Cdecl]<int> s_pthreadMainNp;
    private static nint s_msgSend;

    /// <summary>What one cleanup pass did, logged so a live check can see which pass fired.</summary>
    internal readonly record struct Result(
        string Outcome,
        int PanelsCleared,
        int VisiblePanelsSkipped,
        int AccessoryWindowsOrderedOut)
    {
        internal static Result Skipped(string why) => new(why, 0, 0, 0);
    }

    /// <summary>Passes per picker: the immediate one, then up to three retries.</summary>
    internal const int MaxAttempts = 4;

    /// <summary>
    /// The wait before a retry. It covers AppKit's animated sheet dismissal, which
    /// takes about 250-400 ms.
    /// </summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Post a cleanup pass to the UI thread at background priority, and retry
    /// while the finished panel is still visible (see <see cref="ShouldRetry"/>).
    /// Does nothing off macOS. Never throws.
    /// </summary>
    internal static void Schedule(ILogger? logger)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            Dispatcher.UIThread.Post(() => RunAttempt(logger, attempt: 1), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            LogFailureOnce(logger, ex);
        }
    }

    /// <summary>
    /// Whether a pass should be tried again. On the OK path Avalonia calls
    /// <c>orderOut:</c>, so the first pass finds the panel hidden. On CANCEL it
    /// does not: the sheet is still animating out, and <c>isVisible</c> stays
    /// YES for a few hundred milliseconds. A pass then skips a visible panel
    /// and clears nothing, so it retries after <see cref="RetryDelay"/>, up to
    /// <see cref="MaxAttempts"/> passes in all.
    /// </summary>
    internal static bool ShouldRetry(Result result, int attempt) =>
        attempt < MaxAttempts &&
        result.Outcome == "swept" &&
        result.PanelsCleared == 0 &&
        result.VisiblePanelsSkipped > 0;

    private static void RunAttempt(ILogger? logger, int attempt)
    {
        var result = RunOnMainThread(logger, attempt);
        if (!ShouldRetry(result, attempt))
            return;

        try
        {
            DispatcherTimer.RunOnce(() => RunAttempt(logger, attempt + 1), RetryDelay, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            LogFailureOnce(logger, ex);
        }
    }

    /// <summary>
    /// Run one cleanup pass now. It must be called on the process main thread,
    /// and it is a no-op anywhere else. Never throws.
    /// </summary>
    internal static Result RunOnMainThread(ILogger? logger, int attempt = 1)
    {
        if (!OperatingSystem.IsMacOS())
            return Result.Skipped("not-macos");

        try
        {
            if (!TryResolveApi())
                return Result.Skipped("objc-runtime-unavailable");

            if (s_pthreadMainNp() == 0)
                return Result.Skipped("not-main-thread");

            // AppKit is not loaded, so there can be no panel. Checked before
            // AppKit is opened, so this never loads AppKit into a process that
            // did not already have it.
            if (Class("NSApplication") == 0)
                return Result.Skipped("appkit-not-loaded");

            nint app = ReadNSApp();
            if (app == 0)
                return Result.Skipped("no-nsapp");

            nint pool = s_poolPush();
            try
            {
                var result = Sweep(app);
                logger?.LogInformation(
                    "File panel accessory cleanup (#1477): attempt={Attempt}/{MaxAttempts} outcome={Outcome} " +
                    "panelsCleared={PanelsCleared} visiblePanelsSkipped={VisiblePanelsSkipped} " +
                    "accessoryWindowsOrderedOut={AccessoryWindowsOrderedOut}",
                    attempt, MaxAttempts, result.Outcome, result.PanelsCleared, result.VisiblePanelsSkipped,
                    result.AccessoryWindowsOrderedOut);
                return result;
            }
            finally
            {
                s_poolPop(pool);
            }
        }
        catch (Exception ex)
        {
            LogFailureOnce(logger, ex);
            return Result.Skipped("failed");
        }
    }

    /// <summary>
    /// Bind the ObjC runtime, libSystem's <c>pthread_main_np</c>, and the
    /// autorelease pool. Does not load AppKit. Internal for tests.
    /// </summary>
    internal static bool TryResolveApi()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        lock (ApiGate)
        {
            if (s_apiResolved)
                return s_apiAvailable;
            s_apiResolved = true;

            if (!NativeLibrary.TryLoad(LibObjcPath, out var objc) ||
                !NativeLibrary.TryLoad(LibSystemPath, out var system))
                return false;

            if (!Export(objc, "objc_getClass", out var getClass) ||
                !Export(objc, "sel_registerName", out var registerName) ||
                !Export(objc, "objc_msgSend", out var msgSend) ||
                !Export(objc, "objc_autoreleasePoolPush", out var poolPush) ||
                !Export(objc, "objc_autoreleasePoolPop", out var poolPop) ||
                !Export(system, "pthread_main_np", out var pthreadMainNp))
                return false;

            s_getClass = (delegate* unmanaged[Cdecl]<byte*, nint>)getClass;
            s_registerName = (delegate* unmanaged[Cdecl]<byte*, nint>)registerName;
            s_msgSend = msgSend;
            s_poolPush = (delegate* unmanaged[Cdecl]<nint>)poolPush;
            s_poolPop = (delegate* unmanaged[Cdecl]<nint, void>)poolPop;
            s_pthreadMainNp = (delegate* unmanaged[Cdecl]<int>)pthreadMainNp;
            s_apiAvailable = true;
            return true;
        }

        static bool Export(nint lib, string name, out nint address) =>
            NativeLibrary.TryGetExport(lib, name, out address) && address != 0;
    }

    /// <summary><c>objc_getClass</c>; 0 when the class is not registered. Internal for tests.</summary>
    internal static nint Class(string name) => TryResolveApi() ? CallWithUtf8(name, s_getClass) : 0;

    /// <summary><c>sel_registerName</c>. Internal for tests.</summary>
    internal static nint Selector(string name) => TryResolveApi() ? CallWithUtf8(name, s_registerName) : 0;

    private static nint ReadNSApp()
    {
        // NSApp is an exported DATA symbol (`id NSApp`), so the export address
        // points at the id. Reading it never creates an application, where
        // +[NSApplication sharedApplication] would.
        if (!NativeLibrary.TryLoad(AppKitPath, out var appKit) ||
            !NativeLibrary.TryGetExport(appKit, "NSApp", out var nsAppSlot) ||
            nsAppSlot == 0)
            return 0;
        return *(nint*)nsAppSlot;
    }

    private static Result Sweep(nint app)
    {
        nint savePanelClass = Class("NSSavePanel");
        if (savePanelClass == 0)
            return Result.Skipped("no-nssavepanel-class");
        nint accessoryWindowClass = Class("NSAccessoryViewWindow");

        nint selWindows = Selector("windows");
        nint selCount = Selector("count");
        nint selObjectAtIndex = Selector("objectAtIndex:");
        nint selIsKindOfClass = Selector("isKindOfClass:");
        nint selIsVisible = Selector("isVisible");
        nint selAccessoryView = Selector("accessoryView");
        nint selSetAccessoryView = Selector("setAccessoryView:");
        nint selWindow = Selector("window");
        nint selOrderOut = Selector("orderOut:");

        if (!RespondsTo(app, selWindows))
            return Result.Skipped("nsapp-no-windows-selector");
        nint windows = SendId(app, selWindows);
        if (windows == 0 || !RespondsTo(windows, selCount) || !RespondsTo(windows, selObjectAtIndex))
            return Result.Skipped("no-window-list");

        // The array is autoreleased and the pool is ours, so it stays alive
        // through both passes. orderOut: does not mutate this snapshot.
        nuint count = SendNUInt(windows, selCount);
        int cleared = 0, visibleSkipped = 0, orderedOut = 0;

        for (nuint i = 0; i < count; i++)
        {
            nint window = SendIdNUInt(windows, selObjectAtIndex, i);
            if (window == 0 || !IsKindOf(window, selIsKindOfClass, savePanelClass))
                continue;
            if (!RespondsTo(window, selAccessoryView) || !RespondsTo(window, selSetAccessoryView))
                continue;
            if (RespondsTo(window, selIsVisible) && SendBool(window, selIsVisible))
            {
                // A panel on screen now is a picker in progress; its accessory is live.
                visibleSkipped++;
                continue;
            }

            nint accessoryView = SendId(window, selAccessoryView);
            if (accessoryView == 0)
                continue;

            nint hostWindow = RespondsTo(accessoryView, selWindow) ? SendId(accessoryView, selWindow) : 0;
            SendVoidId(window, selSetAccessoryView, 0);
            cleared++;

            if (hostWindow != 0 && hostWindow != window && RespondsTo(hostWindow, selOrderOut))
            {
                SendVoidId(hostWindow, selOrderOut, 0);
                orderedOut++;
            }
        }

        if (accessoryWindowClass != 0 && visibleSkipped == 0)
        {
            for (nuint i = 0; i < count; i++)
            {
                nint window = SendIdNUInt(windows, selObjectAtIndex, i);
                if (window == 0 || !IsKindOf(window, selIsKindOfClass, accessoryWindowClass))
                    continue;
                if (!RespondsTo(window, selIsVisible) || !SendBool(window, selIsVisible))
                    continue;
                if (!RespondsTo(window, selOrderOut))
                    continue;
                SendVoidId(window, selOrderOut, 0);
                orderedOut++;
            }
        }

        return new Result("swept", cleared, visibleSkipped, orderedOut);
    }

    // objc_msgSend is called through a function pointer cast to each exact,
    // non-variadic prototype. All of these return integers, pointers, or BOOL
    // (never a struct or a float), so the one entry point is correct on arm64
    // and x86_64. BOOL is read as a byte and tested against zero.
    private static bool RespondsTo(nint receiver, nint selector)
    {
        nint selRespondsTo = Selector("respondsToSelector:");
        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)s_msgSend)(receiver, selRespondsTo, selector) != 0;
    }

    private static bool IsKindOf(nint receiver, nint selIsKindOfClass, nint cls) =>
        RespondsTo(receiver, selIsKindOfClass) &&
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)s_msgSend)(receiver, selIsKindOfClass, cls) != 0;

    private static nint SendId(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint SendIdNUInt(nint receiver, nint selector, nuint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nuint, nint>)s_msgSend)(receiver, selector, arg);

    private static nuint SendNUInt(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nuint>)s_msgSend)(receiver, selector);

    private static bool SendBool(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, byte>)s_msgSend)(receiver, selector) != 0;

    private static void SendVoidId(nint receiver, nint selector, nint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)s_msgSend)(receiver, selector, arg);

    private static nint CallWithUtf8(string value, delegate* unmanaged[Cdecl]<byte*, nint> fn)
    {
        // Class and selector names are short ASCII constants. The runtime
        // copies a name it registers, so a NUL-terminated buffer that lives
        // only for the call is enough.
        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = length < 128 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        buffer[length] = 0;
        fixed (byte* p = buffer)
            return fn(p);
    }

    private static void LogFailureOnce(ILogger? logger, Exception ex)
    {
        if (Interlocked.Exchange(ref s_failureLogged, 1) == 0)
            logger?.LogWarning(ex, "File panel accessory cleanup failed (#1477); later failures are not logged");
    }
}
