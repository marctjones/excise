using System.Runtime.InteropServices;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Text.Segmentation;

namespace Excise.Native;

/// <summary>Values of the EXCISE_ERR_* codes in include/excise.h. Never renumber.</summary>
internal static class Status
{
    public const int Ok = 0;
    public const int InvalidArg = 1;
    public const int BadPdf = 2;
    public const int Password = 3;
    public const int Io = 4;
    public const int Unsupported = 5;
    public const int InvalidHandle = 6;
    public const int PageRange = 7;
    public const int RedactionRefused = 8;
    public const int Busy = 9;
    public const int RedactionIncomplete = 10;
    public const int Internal = 99;
}

/// <summary>Carries a status code and message out of the implementation methods.</summary>
internal sealed class NativeException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Per-thread last-error message, handed out as a NUL-terminated UTF-8 buffer.</summary>
internal static unsafe class LastError
{
    [ThreadStatic] private static nint t_buffer;

    public static nint Set(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        if (t_buffer != 0) NativeMemory.Free((void*)t_buffer);
        t_buffer = (nint)p;
        return t_buffer;
    }

    public static nint Get() => t_buffer != 0 ? t_buffer : Set("");

    /// <summary>Record an exception and return its status code. Never throws.</summary>
    public static int Fail(Exception ex, int fallback = Status.Internal)
    {
        int code;
        string message;
        try
        {
            (code, message) = Map(ex, fallback);
            Set(message);
        }
        catch
        {
            code = Status.Internal;
        }
        return code;
    }

    private static (int, string) Map(Exception ex, int fallback)
    {
        switch (ex)
        {
            case NativeException n:
                return (n.Status, n.Message);
            case PdfEncryptionNotSupportedException e:
                return (e.Message.Contains("assword", StringComparison.Ordinal)
                    ? Status.Password : Status.Unsupported, e.Message);
            case PdfParseException e:
                return (Status.BadPdf, e.Message);
            case PdfPortfolioRedactionException or AttachmentRedactionRefusedException
                or UnresolvedRedactAnnotationsException:
                return (Status.RedactionRefused, ex.Message);
            case ArgumentException:
                return (Status.InvalidArg, ex.Message);
            case IOException or UnauthorizedAccessException:
                return (Status.Io, ex.Message);
            default:
                return (fallback, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>An open document plus the state a handle carries.</summary>
internal sealed class DocEntry(PdfDocument document, string? password)
{
    public PdfDocument Document { get; } = document;

    /// <summary>User password the document was opened with; reused to re-encrypt on save.</summary>
    public string? Password { get; } = password;

    /// <summary>0 = idle, 1 = in use by a call, -1 = closed.</summary>
    public int State;
}

/// <summary>
/// Opaque handle table. A handle is a never-reused 64-bit id, so a double close, a stale
/// handle or a garbage pointer is a lookup miss (EXCISE_ERR_INVALID_HANDLE), never a crash.
/// </summary>
internal static class Handles
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, DocEntry> Table = new();
    private static long _next;

    public static nint Add(DocEntry entry)
    {
        lock (Gate)
        {
            var id = ++_next;
            Table[id] = entry;
            return (nint)id;
        }
    }

    /// <summary>Claim exclusive use of the document for the duration of one call.</summary>
    public static DocEntry Acquire(nint handle)
    {
        DocEntry? entry;
        lock (Gate) Table.TryGetValue(handle, out entry);
        if (entry == null)
            throw new NativeException(Status.InvalidHandle, "unknown, stale or already-closed document handle");
        if (Interlocked.CompareExchange(ref entry.State, 1, 0) != 0)
            throw new NativeException(Status.Busy, "document handle is in use by another thread");
        return entry;
    }

    public static void Release(DocEntry entry) => Interlocked.CompareExchange(ref entry.State, 0, 1);

    public static void Close(nint handle)
    {
        DocEntry? entry;
        lock (Gate) Table.TryGetValue(handle, out entry);
        if (entry == null)
            throw new NativeException(Status.InvalidHandle, "unknown, stale or already-closed document handle");
        if (Interlocked.CompareExchange(ref entry.State, -1, 0) != 0)
            throw new NativeException(Status.Busy, "document handle is in use by another thread");
        lock (Gate) Table.Remove(handle);
        entry.Document.Dispose();
    }
}
