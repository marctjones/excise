using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Excise.App.Workspace;

/// <summary>
/// Hands documents from a second launch to the running excise (#1553):
/// "Open With" in Explorer or a Linux file manager starts a new process, which
/// forwards its PDF paths to the running instance and exits, so the documents
/// open as new windows of ONE process.
/// </summary>
/// <remarks>
/// <para>
/// Transport: a per-user named pipe with <see cref="PipeOptions.CurrentUserOnly"/>
/// (on Linux .NET implements it as a Unix domain socket and checks the peer's
/// user). Not used on macOS, where Launch Services already delivers documents
/// to the running app as activation events.
/// </para>
/// <para>
/// Only a launch that names at least one PDF is forwarded. A plain launch still
/// starts its own process, as before. The receiver treats what arrives as
/// untrusted input: a fixed header, a bounded number of bounded lines, and every
/// path is re-resolved by <see cref="StartupDocumentResolver"/> (existing
/// <c>.pdf</c> files only) before anything opens.
/// </para>
/// </remarks>
internal static class SingleInstanceChannel
{
    internal const string ProtocolHeader = "excise-open/1";
    internal const string Acknowledgement = "ok";
    internal const int MaxPaths = 256;
    internal const int MaxLineLength = 4096;
    internal const string DisableEnvironmentVariable = "EXCISE_SINGLE_INSTANCE";
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Whether this platform and environment use the channel at all.</summary>
    internal static bool IsEnabled =>
        !OperatingSystem.IsMacOS() &&
        Environment.GetEnvironmentVariable(DisableEnvironmentVariable) != "0";

    /// <summary>
    /// The pipe name for this user. Short on purpose: on Linux it becomes a
    /// socket path under the temp directory, which has a ~100 byte limit.
    /// </summary>
    internal static string DefaultPipeName()
    {
        var identity = $"{Environment.UserName}|{Environment.UserDomainName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "excise-open-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Program entry: forward the command line's PDFs to a running instance.
    /// True means they were accepted and this process should exit.
    /// </summary>
    internal static bool TryForwardStartupDocuments(string[] args)
    {
        if (!IsEnabled || args.Length == 0)
            return false;

        // A measurement launch (#1497, responsiveness reports) must run in
        // its own process.
        if (StartupDocumentResolver.ResolveResponsivenessReportPath(args, null) != null)
            return false;

        var paths = StartupDocumentResolver.ResolveAll(args, null);
        return paths.Count > 0 && TryForward(DefaultPipeName(), paths, ConnectTimeout);
    }

    /// <summary>
    /// Send <paramref name="paths"/> to the instance listening on
    /// <paramref name="pipeName"/>. False when nobody is listening or the
    /// listener did not acknowledge; the caller then starts normally.
    /// </summary>
    internal static bool TryForward(string pipeName, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        if (paths.Count == 0 || paths.Count > MaxPaths)
            return false;

        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Max(1, timeout.TotalMilliseconds));

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                NewLine = "\n",
            };
            writer.WriteLine(ProtocolHeader);
            foreach (var path in paths)
            {
                if (path.Length > MaxLineLength || path.Contains('\n') || path.Contains('\r'))
                    return false;
                writer.WriteLine(path);
            }
            writer.WriteLine();
            writer.Flush();

            using var reader = new StreamReader(client, Encoding.UTF8, false, 256, leaveOpen: true);
            var readTask = reader.ReadLineAsync();
            if (!readTask.Wait(timeout))
                return false;
            return readTask.Result == Acknowledgement;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
                                       or InvalidOperationException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read one request. Null when it breaks the protocol: wrong header, too
    /// many or too long lines, or no terminating empty line.
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var header = await ReadBoundedLineAsync(reader, token).ConfigureAwait(false);
        if (header != ProtocolHeader)
            return null;

        var paths = new List<string>();
        while (true)
        {
            var line = await ReadBoundedLineAsync(reader, token).ConfigureAwait(false);
            if (line == null)
                return null;
            if (line.Length == 0)
                return paths;
            if (paths.Count >= MaxPaths)
                return null;
            paths.Add(line);
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
                return null;
            var c = buffer[0];
            if (c == '\n')
                return builder.ToString();
            if (c == '\r')
                continue;
            if (builder.Length >= MaxLineLength)
                return null;
            builder.Append(c);
        }
    }

    /// <summary>
    /// The listening side, owned by the running application. Requests are
    /// handled one at a time; the <c>onPaths</c> callback runs on a pool thread
    /// and must marshal to the UI thread itself.
    /// </summary>
    internal sealed class Server : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string _pipeName;
        private readonly Action<IReadOnlyList<string>> _onPaths;
        private readonly ILogger? _logger;
        private readonly Task _loop;

        private Server(string pipeName, Action<IReadOnlyList<string>> onPaths, ILogger? logger)
        {
            _pipeName = pipeName;
            _onPaths = onPaths;
            _logger = logger;
            _loop = Task.Run(RunAsync);
        }

        internal static Server? TryStart(string pipeName, Action<IReadOnlyList<string>> onPaths, ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(onPaths);
            try
            {
                return new Server(pipeName, onPaths, logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Single-instance channel could not start (#1553)");
                return null;
            }
        }

        /// <summary>Completes when the loop has stopped. For tests.</summary>
        internal Task Completion => _loop;

        private async Task RunAsync()
        {
            var token = _cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException ex)
                {
                    // Another instance already listens under this name. It
                    // receives the forwarded documents; this one simply does not.
                    _logger?.LogInformation(ex, "Single-instance channel is owned by another excise process");
                    return;
                }

                await using (server.ConfigureAwait(false))
                {
                    try
                    {
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var paths = await ReadRequestAsync(server, timeout.Token).ConfigureAwait(false);
                        if (paths == null)
                        {
                            _logger?.LogWarning("Single-instance channel rejected a malformed request");
                            continue;
                        }

                        var accepted = StartupDocumentResolver.ResolveAll(paths, null);
                        await using (var writer = new StreamWriter(server, new UTF8Encoding(false), 256, leaveOpen: true)
                                     { NewLine = "\n" })
                        {
                            await writer.WriteLineAsync(Acknowledgement).ConfigureAwait(false);
                            await writer.FlushAsync(token).ConfigureAwait(false);
                        }

                        _logger?.LogInformation(
                            "Single-instance channel received {Count} document(s), {Accepted} openable",
                            paths.Count, accepted.Count);
                        if (accepted.Count > 0)
                            _onPaths(accepted);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException)
                    {
                        _logger?.LogDebug(ex, "Single-instance request ended early");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_cancellation.IsCancellationRequested)
                return;
            _cancellation.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            // The token source is not disposed: a loop that outlived the wait
            // above still reads its token.
        }
    }
}
