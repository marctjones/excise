using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.Automation;

/// <summary>
/// The step-boundary journal and the in-app ↔ outer-harness handshake (#1497).
/// </summary>
/// <remarks>
/// <para><b>Why a handshake and not polling.</b> The outer harness's numbers —
/// <c>footprint</c>, and especially <c>vmmap --summary</c> — are the ones #1461
/// and #1496 are argued in, and <c>vmmap</c> suspends the target process while
/// it walks the region map. A sampler that polls on a timer will sometimes land
/// mid-scroll, and then the band-render p50/p99 for that step is garbage and
/// nothing says so. So the runner tells the harness when a step has ended and
/// waits for it, instead of the harness guessing.</para>
///
/// <para><b>The protocol.</b> At every step boundary the runner
/// (1) appends one JSON object to <c>steps.jsonl</c>, flushed;
/// (2) atomically replaces <c>step.marker</c> with the step's sequence number;
/// (3) waits until <c>step.ack</c> contains that sequence number, or
/// <see cref="AckTimeout"/> elapses.
/// The harness tails <c>step.marker</c>, takes its expensive samples while the
/// app is quiescent, then writes the sequence number to <c>step.ack</c>.</para>
///
/// <para><b>It cannot deadlock.</b> The wait is bounded, and a run with no
/// harness attached (a developer running a scenario by hand, or the headless
/// tests) simply times out at every boundary. <see cref="AckTimeout"/> is
/// therefore also the per-step sampling budget, and whether each boundary was
/// acknowledged is recorded — a step that timed out has no trustworthy outer
/// sample beside it, and the summary must say so rather than silently pairing
/// it with a stale one.</para>
/// </remarks>
internal sealed class PerfStepJournal : IDisposable
{
    internal const string StepsFileName = "steps.jsonl";
    internal const string MarkerFileName = "step.marker";
    internal const string AckFileName = "step.ack";

    private readonly string _directory;
    private readonly StreamWriter _writer;
    private readonly object _writeGate = new();
    private int _sequence;
    private bool _disposed;

    /// <summary>How long a boundary waits for the harness before giving up.</summary>
    internal TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often the boundary re-reads the ack file.</summary>
    internal TimeSpan AckPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Boundaries that the harness acknowledged.</summary>
    internal int AcknowledgedBoundaries { get; private set; }

    /// <summary>Boundaries that timed out waiting for the harness.</summary>
    internal int UnacknowledgedBoundaries { get; private set; }

    internal PerfStepJournal(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);

        var stream = new FileStream(
            Path.Combine(directory, StepsFileName),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            // Same posture as MetricsJsonlSink: the harness tails this file, and
            // a run the machine kills must leave every completed step on disk.
            AutoFlush = true,
        };
    }

    /// <summary>
    /// Record a step boundary and give the harness its sampling window.
    /// Returns whether the harness acknowledged within <see cref="AckTimeout"/>.
    /// </summary>
    internal async Task<bool> RecordAsync(PerfStepRecord record, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        Write(record.ToJson(sequence));

        try
        {
            WriteAtomic(MarkerFileName, sequence.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // A marker we cannot publish means no outer sample for this step.
            // Not fatal: the in-process numbers are still good.
            UnacknowledgedBoundaries++;
            return false;
        }

        var acknowledged = await WaitForAckAsync(sequence, cancellationToken).ConfigureAwait(true);
        if (acknowledged) AcknowledgedBoundaries++;
        else UnacknowledgedBoundaries++;
        return acknowledged;
    }

    private async Task<bool> WaitForAckAsync(int sequence, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)AckTimeout.TotalMilliseconds;
        var ackPath = Path.Combine(_directory, AckFileName);

        while (Environment.TickCount64 < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                if (File.Exists(ackPath))
                {
                    using var stream = new FileStream(
                        ackPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd().Trim();
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var acked) &&
                        acked >= sequence)
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                // The harness is mid-write. Try again on the next poll.
            }

            await Task.Delay(AckPollInterval, cancellationToken).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>
    /// Write-then-rename so a reader never sees a torn marker. The same
    /// sync-then-atomic-rename discipline the suite runner's checkpoints use,
    /// and for the same reason: a half-written control file that reads as
    /// plausible is worse than no control file.
    /// </summary>
    private void WriteAtomic(string name, string content)
    {
        var target = Path.Combine(_directory, name);
        var temporary = target + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, target, overwrite: true);
    }

    private void Write(string json)
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            _writer.WriteLine(json);
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _writer.Dispose();
    }
}

/// <summary>
/// One journal row. Written by hand rather than through a serializer so it
/// works unchanged in the Native AOT build without another
/// <c>JsonSerializerContext</c>.
/// </summary>
internal sealed record PerfStepRecord(
    string Scenario,
    int Repeat,
    string Step,
    string Op,
    double WallMs,
    long MonotonicMs,
    bool Ok,
    string? Note,
    PerfSample Sample)
{
    internal string ToJson(int sequence)
    {
        var builder = new StringBuilder(512);
        builder.Append('{');
        Num(builder, "seq", sequence).Append(',');
        Str(builder, "scenario", Scenario).Append(',');
        Num(builder, "repeat", Repeat).Append(',');
        Str(builder, "step", Step).Append(',');
        Str(builder, "op", Op).Append(',');
        Dbl(builder, "wallMs", WallMs).Append(',');
        Num(builder, "monotonicMs", MonotonicMs).Append(',');
        builder.Append("\"ok\":").Append(Ok ? "true" : "false").Append(',');

        if (!string.IsNullOrEmpty(Note))
            Str(builder, "note", Note).Append(',');

        Num(builder, "liveHeapBytes", Sample.LiveHeapBytes).Append(',');
        Num(builder, "committedBytes", Sample.CommittedBytes).Append(',');
        Num(builder, "heapSizeBytes", Sample.HeapSizeBytes).Append(',');
        Num(builder, "fragmentedBytes", Sample.FragmentedBytes).Append(',');
        Num(builder, "allocatedBytes", Sample.AllocatedBytes).Append(',');
        Num(builder, "workingSetBytes", Sample.WorkingSetBytes).Append(',');
        Dbl(builder, "cpuTotalMs", Sample.CpuTotalMs).Append(',');
        Num(builder, "gen0Collections", Sample.Gen0Collections).Append(',');
        Num(builder, "gen1Collections", Sample.Gen1Collections).Append(',');
        Num(builder, "gen2Collections", Sample.Gen2Collections).Append(',');
        Num(builder, "continuousInFlight", Sample.ContinuousInFlight).Append(',');
        Num(builder, "continuousEntries", Sample.ContinuousEntries).Append(',');
        Num(builder, "continuousResidentBytes", Sample.ContinuousResidentBytes).Append(',');
        Num(builder, "continuousByteBudget", Sample.ContinuousByteBudget).Append(',');
        Num(builder, "singlePageEntries", Sample.SinglePageEntries).Append(',');
        Dbl(builder, "zoomLevel", Sample.ZoomLevel).Append(',');
        Num(builder, "currentPageIndex", Sample.CurrentPageIndex).Append(',');
        Num(builder, "totalPages", Sample.TotalPages).Append(',');
        Num(builder, "openDocuments", Sample.OpenDocuments).Append(',');
        Num(builder, "documentWindows", Sample.DocumentWindows);
        builder.Append('}');
        return builder.ToString();
    }

    private static StringBuilder Num(StringBuilder builder, string name, long value) =>
        builder.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));

    private static StringBuilder Dbl(StringBuilder builder, string name, double value) =>
        builder.Append('"').Append(name).Append("\":")
            .Append(double.IsFinite(value)
                ? value.ToString("0.###", CultureInfo.InvariantCulture)
                : "null");

    private static StringBuilder Str(StringBuilder builder, string name, string? value)
    {
        builder.Append('"').Append(name).Append("\":\"");
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }

        return builder.Append('"');
    }
}
