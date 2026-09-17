using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace Excise.App.Workspace;

/// <summary>
/// macOS native window tabbing for document windows (#1552), through the
/// Objective-C runtime the same way <c>MacFilePanelAccessoryCleanup</c> and the
/// PDFKit printer reach AppKit: <see cref="NativeLibrary"/> and function
/// pointers, no native shim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed at all.</b> Avalonia 12.1.2 turns tabbing off for
/// every window: <c>WindowImpl::OnInitialiseNSWindow</c> in
/// <c>libAvaloniaNative.dylib</c> sends <c>setTabbingMode:</c> with
/// <c>NSWindowTabbingModeDisallowed</c> (2), read from its disassembly.
/// <see cref="Prepare"/> puts it back to <c>Automatic</c> (0) with one
/// <c>tabbingIdentifier</c> for all excise document windows, which is what
/// Window ▸ Merge All Windows and the tab bar need.
/// </para>
/// <para>
/// <b>Why a new window is tabbed explicitly.</b> AppKit decides automatic
/// tabbing when a window is first ordered front, and by the time Avalonia has
/// shown the window (the first moment its <c>NSWindow</c> is reachable) that
/// decision was made under <c>Disallowed</c>. So <see cref="JoinTabGroupIfPreferred"/>
/// reads the user's System Settings choice (<c>+[NSWindow userTabbingPreference]</c>:
/// Manual 0, Always 1, InFullScreen 2) and, when it asks for tabs, sends
/// <c>addTabbedWindow:ordered:</c> to the window the document was opened from.
/// </para>
/// <para>
/// <b>Rules</b>, as in the other AppKit bridges: main thread only, AppKit must
/// already be loaded (a headless test host never loads it, so every call is a
/// no-op there), only an Avalonia.Native handle (<c>NSWindow</c> or its
/// <c>NSView</c>) is touched, <c>respondsToSelector:</c> is checked before every
/// send, nothing throws, and a failure is logged once.
/// </para>
/// </remarks>
internal static unsafe class MacWindowTabbing
{
    private const string LibObjcPath = "/usr/lib/libobjc.A.dylib";
    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    /// <summary>The one tabbing identifier every excise document window shares.</summary>
    internal const string TabbingIdentifier = "excise.document";

    // NSWindowTabbingMode
    internal const nint TabbingModeAutomatic = 0;
    // NSWindowUserTabbingPreference
    // (Manual = 0 needs no constant: it is "not one of the two below".)
    internal const nint UserPreferenceAlways = 1;
    internal const nint UserPreferenceInFullScreen = 2;
    // NSWindowOrderingMode
    private const nint NSWindowAbove = 1;
    // NSWindowStyleMask
    private const nuint StyleMaskFullScreen = 1 << 14;

    /// <summary>Standard AppKit tab actions, sent to the key window.</summary>
    internal enum TabAction
    {
        MergeAllWindows,
        MoveTabToNewWindow,
        ToggleTabBar,
        SelectNextTab,
        SelectPreviousTab,
    }

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

    /// <summary>
    /// Whether open-as-tab applies for this user and origin window:
    /// "Always", or "In Full Screen" with a full-screen origin.
    /// </summary>
    internal static bool PrefersTabs(nint userPreference, bool originIsFullScreen) =>
        userPreference == UserPreferenceAlways ||
        (userPreference == UserPreferenceInFullScreen && originIsFullScreen);

    /// <summary>The AppKit action selector for <paramref name="action"/>.</summary>
    internal static string SelectorFor(TabAction action) => action switch
    {
        TabAction.MergeAllWindows => "mergeAllWindows:",
        TabAction.MoveTabToNewWindow => "moveTabToNewWindow:",
        TabAction.ToggleTabBar => "toggleTabBar:",
        TabAction.SelectNextTab => "selectNextTab:",
        TabAction.SelectPreviousTab => "selectPreviousTab:",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>
    /// Re-enable tabbing on a shown document window and give it the shared
    /// tabbing identifier. Returns false when nothing was done.
    /// </summary>
    internal static bool Prepare(Window window, ILogger? logger)
    {
        return Run(logger, () =>
        {
            var ns = ResolveNSWindow(window);
            if (ns == 0)
                return false;

            var setMode = Selector("setTabbingMode:");
            var setIdentifier = Selector("setTabbingIdentifier:");
            if (!RespondsTo(ns, setMode) || !RespondsTo(ns, setIdentifier))
                return false;

            SendVoidNInt(ns, setMode, TabbingModeAutomatic);
            var identifier = NSString(TabbingIdentifier);
            if (identifier == 0)
                return false;
            SendVoidId(ns, setIdentifier, identifier);
            return true;
        });
    }

    /// <summary>
    /// Put <paramref name="window"/> into <paramref name="origin"/>'s tab group
    /// when the user's system tabbing preference asks for it.
    /// </summary>
    internal static bool JoinTabGroupIfPreferred(Window window, Window origin, ILogger? logger)
    {
        return Run(logger, () =>
        {
            var ns = ResolveNSWindow(window);
            var originNs = ResolveNSWindow(origin);
            if (ns == 0 || originNs == 0 || ns == originNs)
                return false;

            var windowClass = Class("NSWindow");
            var selPreference = Selector("userTabbingPreference");
            if (windowClass == 0 || !RespondsTo(windowClass, selPreference))
                return false;
            var preference = SendNInt(windowClass, selPreference);

            var selStyleMask = Selector("styleMask");
            var fullScreen = RespondsTo(originNs, selStyleMask) &&
                             (SendNUInt(originNs, selStyleMask) & StyleMaskFullScreen) != 0;
            if (!PrefersTabs(preference, fullScreen))
                return false;

            var selAddTabbed = Selector("addTabbedWindow:ordered:");
            if (!RespondsTo(originNs, selAddTabbed))
                return false;
            SendVoidIdNInt(originNs, selAddTabbed, ns, NSWindowAbove);

            var selMakeKey = Selector("makeKeyAndOrderFront:");
            if (RespondsTo(ns, selMakeKey))
                SendVoidId(ns, selMakeKey, 0);
            logger?.LogInformation("Document window joined its origin's tab group (#1552)");
            return true;
        });
    }

    /// <summary>Send a standard tab action to the application's key window.</summary>
    internal static bool Perform(TabAction action, ILogger? logger)
    {
        return Run(logger, () =>
        {
            var app = ReadNSApp();
            if (app == 0)
                return false;
            var selKeyWindow = Selector("keyWindow");
            if (!RespondsTo(app, selKeyWindow))
                return false;
            var key = SendId(app, selKeyWindow);
            var selAction = Selector(SelectorFor(action));
            if (key == 0 || !RespondsTo(key, selAction))
                return false;
            SendVoidId(key, selAction, 0);
            return true;
        });
    }

    private static bool Run(ILogger? logger, Func<bool> body)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            if (!TryResolveApi() || s_pthreadMainNp() == 0 || Class("NSApplication") == 0)
                return false;

            nint pool = s_poolPush();
            try
            {
                return body();
            }
            finally
            {
                s_poolPop(pool);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref s_failureLogged, 1) == 0)
                logger?.LogWarning(ex, "macOS window tabbing failed (#1552); later failures are not logged");
            return false;
        }
    }

    /// <summary>
    /// The NSWindow behind an Avalonia.Native window, or 0. Any other platform
    /// handle (the headless test platform's, for one) is ignored.
    /// </summary>
    private static nint ResolveNSWindow(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == 0)
            return 0;
        if (handle.HandleDescriptor is not ("NSWindow" or "NSView"))
            return 0;

        nint windowClass = Class("NSWindow");
        nint viewClass = Class("NSView");
        if (windowClass == 0 || viewClass == 0)
            return 0;

        nint candidate = handle.Handle;
        if (IsKindOf(candidate, windowClass))
            return candidate;
        if (!IsKindOf(candidate, viewClass))
            return 0;

        var selWindow = Selector("window");
        if (!RespondsTo(candidate, selWindow))
            return 0;
        candidate = SendId(candidate, selWindow);
        return candidate != 0 && IsKindOf(candidate, windowClass) ? candidate : 0;
    }

    private static bool TryResolveApi()
    {
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

    private static nint ReadNSApp()
    {
        // NSApp is an exported DATA symbol; reading it never creates an
        // application, where +[NSApplication sharedApplication] would.
        if (!NativeLibrary.TryLoad(AppKitPath, out var appKit) ||
            !NativeLibrary.TryGetExport(appKit, "NSApp", out var slot) ||
            slot == 0)
            return 0;
        return *(nint*)slot;
    }

    private static nint Class(string name) => CallWithUtf8(name, s_getClass);

    private static nint Selector(string name) => CallWithUtf8(name, s_registerName);

    /// <summary>An autoreleased NSString (the caller's pool owns it).</summary>
    private static nint NSString(string value)
    {
        var cls = Class("NSString");
        var sel = Selector("stringWithUTF8String:");
        if (cls == 0 || !RespondsTo(cls, sel))
            return 0;

        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = length < 128 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        buffer[length] = 0;
        fixed (byte* p = buffer)
            return ((delegate* unmanaged[Cdecl]<nint, nint, byte*, nint>)s_msgSend)(cls, sel, p);
    }

    // objc_msgSend through exact, non-variadic prototypes. Every call here
    // returns void, an id, NSInteger/NSUInteger or BOOL, never a struct or a
    // float, so the one entry point is correct on arm64 and x86_64.
    private static bool RespondsTo(nint receiver, nint selector)
    {
        var selRespondsTo = Selector("respondsToSelector:");
        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)s_msgSend)(receiver, selRespondsTo, selector) != 0;
    }

    private static bool IsKindOf(nint receiver, nint cls)
    {
        var sel = Selector("isKindOfClass:");
        return RespondsTo(receiver, sel) &&
               ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)s_msgSend)(receiver, sel, cls) != 0;
    }

    private static nint SendId(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint SendNInt(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nuint SendNUInt(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nuint>)s_msgSend)(receiver, selector);

    private static void SendVoidId(nint receiver, nint selector, nint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)s_msgSend)(receiver, selector, arg);

    private static void SendVoidNInt(nint receiver, nint selector, nint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)s_msgSend)(receiver, selector, arg);

    private static void SendVoidIdNInt(nint receiver, nint selector, nint arg1, nint arg2) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)s_msgSend)(receiver, selector, arg1, arg2);

    private static nint CallWithUtf8(string value, delegate* unmanaged[Cdecl]<byte*, nint> fn)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = length < 128 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        buffer[length] = 0;
        fixed (byte* p = buffer)
            return fn(p);
    }
}
