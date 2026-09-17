using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services.Printing;

/// <summary>
/// Prints a PDF file through PDFKit and the standard macOS print sheet
/// (#1545). The file excise wrote is opened with
/// <c>-[PDFDocument initWithURL:]</c>, turned into an <c>NSPrintOperation</c>
/// with <c>-printOperationForPrintInfo:scalingMode:autoRotate:</c>, and run
/// as a sheet on the excise window with
/// <c>-runOperationModalForWindow:delegate:didRunSelector:contextInfo:</c>.
/// The sheet is the system's own: printer, copies, page range, paper,
/// orientation, scale, duplex, preview, and the PDF menu (Save as PDF).
/// </summary>
/// <remarks>
/// <para>
/// Known limitation: the pages are rasterised by Apple's PDF renderer, not by
/// excise's, so small visual differences from the excise viewer are possible.
/// </para>
/// <para>
/// The Objective-C runtime is bound the same way as in
/// <see cref="MacFilePanelAccessoryCleanup"/>: <see cref="NativeLibrary"/>,
/// function pointers, and one <c>objc_msgSend</c> cast to each exact
/// non-variadic prototype. None of the messages here return a struct or a
/// float, so that entry point is correct on arm64 and x86_64.
/// </para>
/// <para>
/// The sheet is asynchronous: <c>runOperationModalForWindow:…</c> returns at
/// once and AppKit later sends <c>printOperationDidRun:success:contextInfo:</c>
/// to the delegate. The delegate is an Objective-C class registered at run
/// time (<c>ExcisePrintOperationDelegate</c>) whose method is an
/// <see cref="UnmanagedCallersOnly"/> function. The context is a
/// <see cref="GCHandle"/> to the pending job, as in
/// <see cref="MacMemoryPressureSource"/>. The job retains the PDFDocument,
/// the print info and the operation until the callback, and the task
/// <see cref="PrintAsync"/> returns completes only then, so the caller
/// deletes the file after the sheet and its preview are done with it.
/// </para>
/// <para>
/// Rules: main thread only (Avalonia's UI thread is the process main thread
/// on macOS); <c>respondsToSelector:</c> is not needed for these public,
/// long-stable APIs, but a nil result from any constructor fails the print
/// with a message; no Objective-C exception may be raised, so the window is
/// checked for an attached sheet first; the callback catches everything.
/// </para>
/// </remarks>
internal sealed class MacPdfKitDocumentPrinter : IDocumentPrinter
{
    private readonly ILogger<MacPdfKitDocumentPrinter> _logger;

    public MacPdfKitDocumentPrinter(ILogger<MacPdfKitDocumentPrinter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported => OperatingSystem.IsMacOS();

    public string UnsupportedReason => UnsupportedDocumentPrinter.DefaultReason;

    public Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(DocumentPrintResult.Fail(UnsupportedReason));

        try
        {
            var nsWindow = ResolveNSWindow(request.Owner);
            if (nsWindow == 0)
            {
                return Task.FromResult(DocumentPrintResult.Fail(
                    "The print sheet needs the excise window, and none was found."));
            }

            return MacPdfKitInterop.RunPrintSheet(request, nsWindow, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDFKit print failed before the sheet opened");
            return Task.FromResult(DocumentPrintResult.Fail($"Printing failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// The NSWindow behind an Avalonia window. Avalonia.Native describes the
    /// top-level handle as either an <c>NSWindow</c> or its content
    /// <c>NSView</c>; for a view, <c>-window</c> gives the window.
    /// </summary>
    private nint ResolveNSWindow(Window? owner)
    {
        var handle = owner?.TryGetPlatformHandle();
        if (handle == null || handle.Handle == 0)
            return 0;

        var resolved = MacPdfKitInterop.ResolveWindow(handle.Handle, handle.HandleDescriptor);
        if (resolved == 0)
        {
            _logger.LogWarning(
                "Owner platform handle {Descriptor} did not resolve to an NSWindow", handle.HandleDescriptor);
        }
        return resolved;
    }
}

/// <summary>The Objective-C side of <see cref="MacPdfKitDocumentPrinter"/>. Internal for tests.</summary>
internal static unsafe class MacPdfKitInterop
{
    private const string LibObjcPath = "/usr/lib/libobjc.A.dylib";
    private const string LibSystemPath = "/usr/lib/libSystem.B.dylib";
    private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string PdfKitPath = "/System/Library/Frameworks/PDFKit.framework/PDFKit";

    private const string DelegateClassName = "ExcisePrintOperationDelegate";
    private const string DidRunSelectorName = "printOperationDidRun:success:contextInfo:";

    // NSPrintPanelOptions (NSPrintPanel.h).
    internal const nuint PanelShowsCopies = 1 << 0;
    internal const nuint PanelShowsPageRange = 1 << 1;
    internal const nuint PanelShowsPaperSize = 1 << 2;
    internal const nuint PanelShowsOrientation = 1 << 3;
    internal const nuint PanelShowsScaling = 1 << 4;
    internal const nuint PanelShowsPreview = 1 << 17;

    internal const nuint DesiredPanelOptions =
        PanelShowsCopies | PanelShowsPageRange | PanelShowsPaperSize |
        PanelShowsOrientation | PanelShowsScaling | PanelShowsPreview;

    private static readonly object ApiGate = new();
    private static bool s_apiResolved;
    private static bool s_apiAvailable;
    private static nint s_delegateInstance;

    private static delegate* unmanaged[Cdecl]<byte*, nint> s_getClass;
    private static delegate* unmanaged[Cdecl]<byte*, nint> s_registerName;
    private static delegate* unmanaged[Cdecl]<nint, byte*, nuint, nint> s_allocateClassPair;
    private static delegate* unmanaged[Cdecl]<nint, void> s_registerClassPair;
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, byte*, byte> s_addMethod;
    private static delegate* unmanaged[Cdecl]<nint> s_poolPush;
    private static delegate* unmanaged[Cdecl]<nint, void> s_poolPop;
    private static delegate* unmanaged[Cdecl]<int> s_pthreadMainNp;
    private static nint s_msgSend;

    /// <summary>
    /// Bind the ObjC runtime and load Foundation, AppKit and PDFKit. PDFKit
    /// classes are not registered until the framework is loaded, so
    /// <c>objc_getClass("PDFDocument")</c> would return 0 without this.
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
                !NativeLibrary.TryLoad(LibSystemPath, out var system) ||
                !NativeLibrary.TryLoad(FoundationPath, out _) ||
                !NativeLibrary.TryLoad(AppKitPath, out _) ||
                !NativeLibrary.TryLoad(PdfKitPath, out _))
                return false;

            if (!Export(objc, "objc_getClass", out var getClass) ||
                !Export(objc, "sel_registerName", out var registerName) ||
                !Export(objc, "objc_msgSend", out var msgSend) ||
                !Export(objc, "objc_allocateClassPair", out var allocateClassPair) ||
                !Export(objc, "objc_registerClassPair", out var registerClassPair) ||
                !Export(objc, "class_addMethod", out var addMethod) ||
                !Export(objc, "objc_autoreleasePoolPush", out var poolPush) ||
                !Export(objc, "objc_autoreleasePoolPop", out var poolPop) ||
                !Export(system, "pthread_main_np", out var pthreadMainNp))
                return false;

            s_getClass = (delegate* unmanaged[Cdecl]<byte*, nint>)getClass;
            s_registerName = (delegate* unmanaged[Cdecl]<byte*, nint>)registerName;
            s_msgSend = msgSend;
            s_allocateClassPair = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, nint>)allocateClassPair;
            s_registerClassPair = (delegate* unmanaged[Cdecl]<nint, void>)registerClassPair;
            s_addMethod = (delegate* unmanaged[Cdecl]<nint, nint, nint, byte*, byte>)addMethod;
            s_poolPush = (delegate* unmanaged[Cdecl]<nint>)poolPush;
            s_poolPop = (delegate* unmanaged[Cdecl]<nint, void>)poolPop;
            s_pthreadMainNp = (delegate* unmanaged[Cdecl]<int>)pthreadMainNp;
            s_apiAvailable = Class("PDFDocument") != 0 && Class("NSPrintOperation") != 0;
            return s_apiAvailable;
        }

        static bool Export(nint lib, string name, out nint address) =>
            NativeLibrary.TryGetExport(lib, name, out address) && address != 0;
    }

    internal static bool IsMainThread => TryResolveApi() && s_pthreadMainNp() != 0;

    internal static nint Class(string name) => Utf8Call(name, s_getClass);

    internal static nint Selector(string name) => Utf8Call(name, s_registerName);

    /// <summary>
    /// Resolve an Avalonia top-level handle to an NSWindow, or 0. Accepts a
    /// window, or a view whose <c>-window</c> is one.
    /// </summary>
    internal static nint ResolveWindow(nint handle, string? descriptor)
    {
        if (handle == 0 || !TryResolveApi())
            return 0;

        nint windowClass = Class("NSWindow");
        nint viewClass = Class("NSView");
        nint candidate = handle;
        if (!IsKindOf(candidate, windowClass))
        {
            if (!IsKindOf(candidate, viewClass))
                return 0;
            candidate = SendId(candidate, Selector("window"));
        }

        _ = descriptor; // Logged by the caller; the class check above is what decides.
        return candidate != 0 && IsKindOf(candidate, windowClass) ? candidate : 0;
    }

    /// <summary>
    /// Open <paramref name="pdfPath"/> as a PDFDocument and return its page
    /// count, or -1 when PDFKit cannot open it. Needs no window and no
    /// NSApplication. Internal for the headless smoke test.
    /// </summary>
    internal static long ProbePageCount(string pdfPath)
    {
        if (!TryResolveApi())
            return -1;

        nint pool = s_poolPush();
        try
        {
            nint document = OpenPdfDocument(pdfPath);
            if (document == 0)
                return -1;
            try
            {
                return (long)SendNUInt(document, Selector("pageCount"));
            }
            finally
            {
                Release(document);
            }
        }
        finally
        {
            s_poolPop(pool);
        }
    }

    /// <summary>
    /// Build the NSPrintOperation for <paramref name="pdfPath"/> without
    /// running it, and report whether it was created and whether its print
    /// panel carries <see cref="DesiredPanelOptions"/>. Internal for the
    /// headless smoke test; must run on the main thread.
    /// </summary>
    internal static (bool Created, bool PanelOptionsApplied) ProbePrintOperation(string pdfPath, PrintScalingMode scaling)
    {
        if (!TryResolveApi())
            return (false, false);

        nint pool = s_poolPush();
        try
        {
            nint document = OpenPdfDocument(pdfPath);
            if (document == 0)
                return (false, false);
            nint printInfo = 0;
            try
            {
                printInfo = CopySharedPrintInfo();
                nint operation = CreatePrintOperation(document, printInfo, scaling, "excise smoke");
                if (operation == 0)
                    return (false, false);
                nint panel = SendId(operation, Selector("printPanel"));
                nuint options = panel == 0 ? 0 : SendNUInt(panel, Selector("options"));
                return (true, (options & DesiredPanelOptions) == DesiredPanelOptions);
            }
            finally
            {
                Release(printInfo);
                Release(document);
            }
        }
        finally
        {
            s_poolPop(pool);
        }
    }

    internal static Task<DocumentPrintResult> RunPrintSheet(DocumentPrintRequest request, nint nsWindow, ILogger logger)
    {
        if (!TryResolveApi())
            return Task.FromResult(DocumentPrintResult.Fail("PDFKit is not available on this system."));
        if (s_pthreadMainNp() == 0)
            return Task.FromResult(DocumentPrintResult.Fail("Printing must start on the main thread."));

        // A second sheet on a window that already has one raises an
        // Objective-C exception, which would take the process down.
        if (SendId(nsWindow, Selector("attachedSheet")) != 0)
        {
            return Task.FromResult(DocumentPrintResult.Fail(
                "Another sheet is open on the excise window. Close it and print again."));
        }

        nint delegateInstance = EnsureDelegate();
        if (delegateInstance == 0)
            return Task.FromResult(DocumentPrintResult.Fail("Could not register the print delegate."));

        nint pool = s_poolPush();
        try
        {
            nint document = OpenPdfDocument(request.PdfPath);
            if (document == 0)
                return Task.FromResult(DocumentPrintResult.Fail("macOS could not open the print copy of this document."));

            nint printInfo = CopySharedPrintInfo();
            nint operation = CreatePrintOperation(document, printInfo, request.Scaling, request.JobTitle);
            if (operation == 0)
            {
                Release(printInfo);
                Release(document);
                return Task.FromResult(DocumentPrintResult.Fail("macOS could not create a print operation for this document."));
            }

            Retain(operation);
            var job = new PendingPrintJob(document, printInfo, operation, logger);
            var context = GCHandle.Alloc(job);
            logger.LogInformation(
                "Opening the macOS print sheet: {Pages} page(s), scaling {Scaling}",
                (long)SendNUInt(document, Selector("pageCount")), request.Scaling);

            SendVoidRunModal(
                operation,
                Selector("runOperationModalForWindow:delegate:didRunSelector:contextInfo:"),
                nsWindow,
                delegateInstance,
                Selector(DidRunSelectorName),
                GCHandle.ToIntPtr(context));
            return job.Completion.Task;
        }
        finally
        {
            s_poolPop(pool);
        }
    }

    private sealed class PendingPrintJob(nint document, nint printInfo, nint operation, ILogger logger)
    {
        public TaskCompletionSource<DocumentPrintResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Finish(bool success)
        {
            Release(operation);
            Release(printInfo);
            Release(document);
            logger.LogInformation("macOS print sheet finished: success={Success}", success);
            Completion.TrySetResult(success ? DocumentPrintResult.Printed : DocumentPrintResult.Cancelled);
        }
    }

    // - (void)printOperationDidRun:(NSPrintOperation *)op success:(BOOL)success contextInfo:(void *)ctx
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPrintOperationDidRun(nint self, nint selector, nint operation, byte success, nint contextInfo)
    {
        // An exception must never unwind into AppKit.
        try
        {
            if (contextInfo == 0)
                return;
            var handle = GCHandle.FromIntPtr(contextInfo);
            var job = handle.Target as PendingPrintJob;
            handle.Free();
            job?.Finish(success != 0);
        }
        catch
        {
        }
    }

    private static nint EnsureDelegate()
    {
        lock (ApiGate)
        {
            if (s_delegateInstance != 0)
                return s_delegateInstance;

            nint cls = Class(DelegateClassName);
            if (cls == 0)
            {
                nint superclass = Class("NSObject");
                byte[] name = Utf8Z(DelegateClassName);
                fixed (byte* pName = name)
                    cls = s_allocateClassPair(superclass, pName, 0);
                if (cls == 0)
                    return 0;

                // BOOL is `bool` ('B') on arm64 and `signed char` ('c') on x86_64.
                byte[] types = Utf8Z(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "v@:@B^v" : "v@:@c^v");
                delegate* unmanaged[Cdecl]<nint, nint, nint, byte, nint, void> imp = &OnPrintOperationDidRun;
                nint didRun = Selector(DidRunSelectorName);
                byte added;
                fixed (byte* pTypes = types)
                    added = s_addMethod(cls, didRun, (nint)imp, pTypes);
                if (added == 0)
                    return 0;
                s_registerClassPair(cls);
            }

            // One instance for the life of the process; never released.
            nint instance = SendId(SendId(cls, Selector("alloc")), Selector("init"));
            s_delegateInstance = instance;
            return instance;
        }
    }

    private static nint OpenPdfDocument(string path)
    {
        nint nsPath = NSString(path);
        if (nsPath == 0)
            return 0;
        nint url = SendIdId(Class("NSURL"), Selector("fileURLWithPath:"), nsPath);
        if (url == 0)
            return 0;
        nint allocated = SendId(Class("PDFDocument"), Selector("alloc"));
        // +1 retained, or nil when the file is not a PDF PDFKit can open.
        return allocated == 0 ? 0 : SendIdId(allocated, Selector("initWithURL:"), url);
    }

    /// <summary>
    /// A private copy of the shared print info, so the user's printer and
    /// paper are the starting point but PDFKit's auto-rotate does not change
    /// the app-wide object. +1 retained.
    /// </summary>
    private static nint CopySharedPrintInfo()
    {
        nint shared = SendId(Class("NSPrintInfo"), Selector("sharedPrintInfo"));
        return shared == 0 ? 0 : SendId(shared, Selector("copy"));
    }

    /// <summary>Autoreleased NSPrintOperation, or 0.</summary>
    private static nint CreatePrintOperation(nint document, nint printInfo, PrintScalingMode scaling, string jobTitle)
    {
        nint operation = SendPrintOperation(
            document,
            Selector("printOperationForPrintInfo:scalingMode:autoRotate:"),
            printInfo,
            (nint)(int)scaling,
            1);
        if (operation == 0)
            return 0;

        SendVoidByte(operation, Selector("setShowsPrintPanel:"), 1);
        SendVoidByte(operation, Selector("setShowsProgressPanel:"), 1);
        nint title = NSString(jobTitle);
        if (title != 0)
            SendVoidId(operation, Selector("setJobTitle:"), title);

        nint panel = SendId(operation, Selector("printPanel"));
        if (panel != 0)
        {
            nuint options = SendNUInt(panel, Selector("options"));
            SendVoidNUInt(panel, Selector("setOptions:"), options | DesiredPanelOptions);
        }
        return operation;
    }

    /// <summary>Autoreleased NSString, or 0.</summary>
    private static nint NSString(string value)
    {
        var buffer = Utf8Z(value);
        fixed (byte* p = buffer)
            return ((delegate* unmanaged[Cdecl]<nint, nint, byte*, nint>)s_msgSend)(
                Class("NSString"), Selector("stringWithUTF8String:"), p);
    }

    private static bool IsKindOf(nint receiver, nint cls) =>
        receiver != 0 && cls != 0 &&
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)s_msgSend)(receiver, Selector("isKindOfClass:"), cls) != 0;

    private static void Retain(nint receiver)
    {
        if (receiver != 0)
            SendId(receiver, Selector("retain"));
    }

    private static void Release(nint receiver)
    {
        if (receiver != 0)
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)s_msgSend)(receiver, Selector("release"));
    }

    private static nint SendId(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint SendIdId(nint receiver, nint selector, nint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nuint SendNUInt(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nuint>)s_msgSend)(receiver, selector);

    private static void SendVoidId(nint receiver, nint selector, nint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)s_msgSend)(receiver, selector, arg);

    private static void SendVoidByte(nint receiver, nint selector, byte arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, byte, void>)s_msgSend)(receiver, selector, arg);

    private static void SendVoidNUInt(nint receiver, nint selector, nuint arg) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nuint, void>)s_msgSend)(receiver, selector, arg);

    // printOperationForPrintInfo:(NSPrintInfo*) scalingMode:(NSInteger) autoRotate:(BOOL)
    private static nint SendPrintOperation(nint receiver, nint selector, nint printInfo, nint scalingMode, byte autoRotate) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, byte, nint>)s_msgSend)(
            receiver, selector, printInfo, scalingMode, autoRotate);

    // runOperationModalForWindow:(NSWindow*) delegate:(id) didRunSelector:(SEL) contextInfo:(void*)
    private static void SendVoidRunModal(nint receiver, nint selector, nint window, nint del, nint didRun, nint context) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(
            receiver, selector, window, del, didRun, context);

    private static nint Utf8Call(string value, delegate* unmanaged[Cdecl]<byte*, nint> fn)
    {
        // Callers resolve the API first; this is also reached from inside
        // TryResolveApi, so it must not call it.
        if (fn == null)
            return 0;
        // Class and selector names are short ASCII constants; the runtime
        // copies what it registers.
        byte[] buffer = Utf8Z(value);
        fixed (byte* p = buffer)
            return fn(p);
    }

    private static byte[] Utf8Z(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        var buffer = new byte[length + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        return buffer;
    }
}
