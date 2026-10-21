namespace Excise.Core.Filters.Jpx;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CSJ2K.j2k.util;

/// <summary>
/// Runs CSJ2K's <c>FacilityManager</c> static constructor with the process's
/// stdout and stderr pointed at nothing, so its UTF-8 BOM never reaches them
/// (#2084).
/// </summary>
/// <remarks>
/// <para>
/// The constructor builds a <c>DotnetMsgLogger</c>, whose base
/// <c>StreamMsgLogger</c> wraps <c>Console.OpenStandardOutput()</c> and
/// <c>Console.OpenStandardError()</c> in <c>new StreamWriter(stream,
/// Encoding.UTF8) { AutoFlush = true }</c>; setting AutoFlush writes EF BB BF to
/// descriptors 1 and 2. CSJ2K 3.0.0 (the newest) has no seam: the class has an
/// explicit static constructor, so touching any member runs it first, and it
/// picks the logger by scanning only CSJ2K's own assembly.
/// </para>
/// <para>
/// So the descriptors are swapped for the constructor's duration: on Unix,
/// <c>dup2</c> of <c>/dev/null</c> over 1 and 2 (<c>OpenStandardOutput</c> dups
/// the descriptor, so CSJ2K's logger keeps writing to <c>/dev/null</c>); on
/// Windows, null standard handles, for which <c>OpenStandardOutput</c> returns
/// <c>Stream.Null</c>. Anything else writing to the console during the swap
/// would lose its output, so call this only while the process is
/// single-threaded: the first statement of a <c>Main</c>. It never throws; if
/// the swap cannot be made the constructor still runs and the BOM is written,
/// which is the old behaviour.
/// </para>
/// </remarks>
internal static partial class Csj2kQuietStartup
{
    private static bool s_done;

    internal static void Run()
    {
        if (s_done)
            return;
        s_done = true;
        try
        {
            InitializeConsoleOutsideTheSwap();
            if (OperatingSystem.IsWindows())
                RunWithWindowsHandlesCleared();
            else
                RunWithUnixDescriptorsNulled();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        RunConstructor();
    }

    // Measured 2026-10-20 (.NET 10, macOS): the first write to ANY console stream
    // initialises the console, and a first write made while descriptor 1 points
    // at /dev/null left Console.Out writing there for the rest of the process.
    // So the writers are created, and the console initialised, against the real
    // descriptors before the swap; the empty writes put no bytes on either.
    private static void InitializeConsoleOutsideTheSwap()
    {
        _ = Console.Out;
        _ = Console.Error;
        // Console caches these on first read. Read against a null handle (Windows)
        // or /dev/null (Unix) they would be wrong for the rest of the process. On
        // Windows, OpenStandardOutput reads IsOutputRedirected only when the output
        // code page is UTF-16, so CSJ2K's call during the swap could be that first read.
        _ = Console.IsOutputRedirected;
        _ = Console.IsErrorRedirected;
        using (var stdout = Console.OpenStandardOutput())
            stdout.Write(ReadOnlySpan<byte>.Empty);
        using (var stderr = Console.OpenStandardError())
            stderr.Write(ReadOnlySpan<byte>.Empty);
    }

    private static void RunConstructor()
    {
        RuntimeHelpers.RunClassConstructor(typeof(FacilityManager).TypeHandle);
        FacilityManager.DefaultMsgLogger = JpxDecoder.NullMsgLogger.Instance;
    }

    private static void RunWithUnixDescriptorsNulled()
    {
        using var devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Write);
        var nullFd = (int)devNull.DangerousGetHandle();
        var savedOut = Dup(1);
        var savedErr = Dup(2);
        if (savedOut < 0 || savedErr < 0)
        {
            CloseIfOpen(savedOut);
            CloseIfOpen(savedErr);
            return;
        }
        try
        {
            if (Dup2(nullFd, 1) < 0 || Dup2(nullFd, 2) < 0)
                return;
            RunConstructor();
        }
        finally
        {
            Dup2(savedOut, 1);
            Dup2(savedErr, 2);
            Close(savedOut);
            Close(savedErr);
        }
    }

    private static void RunWithWindowsHandlesCleared()
    {
        var savedOut = GetStdHandle(StdOutputHandle);
        var savedErr = GetStdHandle(StdErrorHandle);
        try
        {
            if (!SetStdHandle(StdOutputHandle, 0) || !SetStdHandle(StdErrorHandle, 0))
                return;
            RunConstructor();
        }
        finally
        {
            SetStdHandle(StdOutputHandle, savedOut);
            SetStdHandle(StdErrorHandle, savedErr);
        }
    }

    private static void CloseIfOpen(int fd)
    {
        if (fd >= 0)
            Close(fd);
    }

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [LibraryImport("libc", EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "dup2")]
    private static partial int Dup2(int fd, int fd2);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "SetStdHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, nint hHandle);
}
