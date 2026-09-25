using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Security;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;

namespace Excise.Native;

/// <summary>
/// C ABI entry points. The contract is include/excise.h; docs/native-api.md explains it.
/// Every export catches every exception, records it for excise_last_error and returns a
/// status code: nothing propagates across the boundary.
/// </summary>
public static unsafe class NativeExports
{
    private const int AbiVersion = 1;
    private static readonly nint VersionString = AllocStatic("excise-native 1");

    private static nint AllocStatic(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        return (nint)p;
    }

    private static string? Utf8(byte* s) => s == null ? null : Marshal.PtrToStringUTF8((nint)s);

    private static string RequireUtf8(byte* s, string name) =>
        Utf8(s) ?? throw new NativeException(Status.InvalidArg, $"{name} must not be NULL");

    private static void RequireOut(void* p, string name)
    {
        if (p == null) throw new NativeException(Status.InvalidArg, $"{name} must not be NULL");
    }

    /// <summary>Copy managed bytes into a NativeMemory buffer the caller frees with excise_free_buffer.</summary>
    private static void HandOut(ReadOnlySpan<byte> data, byte** outPtr, nuint* outLen)
    {
        var p = (byte*)NativeMemory.Alloc((nuint)data.Length + 1);
        data.CopyTo(new Span<byte>(p, data.Length));
        p[data.Length] = 0;
        *outPtr = p;
        *outLen = (nuint)data.Length;
    }

    private static PdfPage PageOf(PdfDocument doc, int page)
    {
        if (page < 1 || page > doc.PageCount)
            throw new NativeException(Status.PageRange, $"page {page} is out of range 1..{doc.PageCount}");
        return doc.GetPage(page);
    }

    /// <summary>Same defaults as `excise redact`: Standard profile, box drawn, layout-preserving width.</summary>
    private static RedactionOptions DefaultOptions()
    {
        var profile = RedactionOptions.ForProfile(RedactionProfile.Standard);
        return profile with
        {
            DrawBox = true,
            Width = WidthPolicy.CollapsePreserveLayout,
            CarrierPolicy = profile.CarrierPolicy,
        };
    }

    // ---- identity ------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "excise_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "excise_version", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* VersionExport() => (byte*)VersionString;

    [UnmanagedCallersOnly(EntryPoint = "excise_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte* LastErrorExport()
    {
        try { return (byte*)LastError.Get(); }
        catch { return (byte*)VersionString; }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_free_buffer", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void FreeBufferExport(void* buffer)
    {
        if (buffer != null) NativeMemory.Free(buffer);
    }

    // ---- open / close --------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "excise_open_bytes", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int OpenBytesExport(byte* data, nuint len, byte* password, nint* outDoc)
    {
        try
        {
            RequireOut(outDoc, "out");
            *outDoc = 0;
            if (data == null && len != 0) throw new NativeException(Status.InvalidArg, "data must not be NULL");
            var copy = data == null ? Array.Empty<byte>() : new ReadOnlySpan<byte>(data, checked((int)len)).ToArray();
            *outDoc = OpenCore(copy, Utf8(password));
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_open_path", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int OpenPathExport(byte* path, byte* password, nint* outDoc)
    {
        try
        {
            RequireOut(outDoc, "out");
            *outDoc = 0;
            var bytes = File.ReadAllBytes(RequireUtf8(path, "path"));
            *outDoc = OpenCore(bytes, Utf8(password));
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
    }

    private static nint OpenCore(byte[] bytes, string? password)
    {
        if (bytes.Length == 0) throw new NativeException(Status.BadPdf, "empty input");
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(bytes, password);
        }
        catch (Exception ex) when (ex is not NativeException)
        {
            // Malformed input surfaces as many exception types; from Open they all mean "bad PDF".
            throw new NativeException(LastError.Fail(ex, Status.BadPdf), ex.Message);
        }
        return Handles.Add(new DocEntry(doc, password));
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_close", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int CloseExport(nint doc)
    {
        try { Handles.Close(doc); return Status.Ok; }
        catch (Exception ex) { return LastError.Fail(ex); }
    }

    // ---- queries -------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "excise_page_count", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int PageCountExport(nint doc, int* outCount)
    {
        DocEntry? e = null;
        try
        {
            RequireOut(outCount, "out_count");
            *outCount = 0;
            e = Handles.Acquire(doc);
            *outCount = e.Document.PageCount;
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_page_size", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int PageSizeExport(nint doc, int page, double* width, double* height)
    {
        DocEntry? e = null;
        try
        {
            RequireOut(width, "width");
            RequireOut(height, "height");
            *width = 0; *height = 0;
            e = Handles.Acquire(doc);
            var p = PageOf(e.Document, page);
            *width = p.Width;
            *height = p.Height;
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_extract_text", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ExtractTextExport(nint doc, int page, byte** outUtf8, nuint* outLen)
    {
        DocEntry? e = null;
        try
        {
            RequireOut(outUtf8, "out_utf8");
            RequireOut(outLen, "out_len");
            *outUtf8 = null; *outLen = 0;
            e = Handles.Acquire(doc);
            var text = new TextExtractor(PageOf(e.Document, page)).ExtractText();
            HandOut(Encoding.UTF8.GetBytes(text), outUtf8, outLen);
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    // ---- redaction -----------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "excise_redact_text", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RedactTextExport(nint doc, byte* term, int* outRemoved)
    {
        DocEntry? e = null;
        try
        {
            RequireOut(outRemoved, "out_removed_count");
            *outRemoved = 0;
            var text = RequireUtf8(term, "term");
            if (text.Length == 0) throw new NativeException(Status.InvalidArg, "term must not be empty");
            e = Handles.Acquire(doc);
            var report = e.Document.RedactText(text, DefaultOptions());
            *outRemoved = report.VerifiedRemovals;
            // Success must mean "the term is gone". Anything the engine located and could not
            // remove, or a carrier it refused to scrub, is reported, never returned as OK.
            if (!report.IsCleanSuccess)
                throw new NativeException(Status.RedactionIncomplete,
                    "redaction was NOT clean, do not treat the output as redacted: " + report);
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_redact_area", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RedactAreaExport(nint doc, int page, double left, double bottom, double right, double top)
    {
        DocEntry? e = null;
        try
        {
            if (!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top))
                throw new NativeException(Status.InvalidArg, "area coordinates must be finite");
            e = Handles.Acquire(doc);
            var p = PageOf(e.Document, page);
            // PDF user space: origin bottom-left, y up.
            var report = p.RedactAreaWithReport(new PdfRectangle(left, bottom, right, top), DefaultOptions());
            if (report.Carriers.Any(c => c.RefusedReason != null))
                throw new NativeException(Status.RedactionIncomplete,
                    "area redaction was NOT clean, do not treat the output as redacted: " + report);
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    // ---- save / encrypt / decrypt --------------------------------------------------

    /// <summary>An encrypted source is re-encrypted with the same password and permissions (as the CLI does).</summary>
    private static PdfEncryptionOptions? ReEncryption(DocEntry e) =>
        e.Document.GetReEncryptionOptions(e.Password);

    [UnmanagedCallersOnly(EntryPoint = "excise_save_path", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int SavePathExport(nint doc, byte* path)
    {
        DocEntry? e = null;
        try
        {
            var p = RequireUtf8(path, "path");
            e = Handles.Acquire(doc);
            e.Document.Save(p, ReEncryption(e));
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex, Status.Io); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_save_bytes", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int SaveBytesExport(nint doc, byte** outData, nuint* outLen)
    {
        DocEntry? e = null;
        try
        {
            RequireOut(outData, "out");
            RequireOut(outLen, "out_len");
            *outData = null; *outLen = 0;
            e = Handles.Acquire(doc);
            HandOut(e.Document.SaveToBytes(ReEncryption(e)), outData, outLen);
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_encrypt_save_path", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int EncryptSavePathExport(nint doc, byte* path, byte* userPassword, byte* ownerPassword)
    {
        DocEntry? e = null;
        try
        {
            var p = RequireUtf8(path, "path");
            var user = Utf8(userPassword);
            var owner = Utf8(ownerPassword);
            if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(owner))
                throw new NativeException(Status.InvalidArg, "a user or owner password is required");
            e = Handles.Acquire(doc);
            e.Document.Save(p, new PdfEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = owner,
                Algorithm = PdfEncryptionAlgorithm.Aes256,
            });
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex, Status.Io); }
        finally { if (e != null) Handles.Release(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "excise_decrypt_save_path", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int DecryptSavePathExport(nint doc, byte* path)
    {
        DocEntry? e = null;
        try
        {
            var p = RequireUtf8(path, "path");
            e = Handles.Acquire(doc);
            e.Document.Save(p, encryptionOptions: null);
            return Status.Ok;
        }
        catch (Exception ex) { return LastError.Fail(ex, Status.Io); }
        finally { if (e != null) Handles.Release(e); }
    }
}
