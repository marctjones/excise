using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Excise.App.Services;

/// <summary>
/// Live performance metrics as one JSON object per line (#1491), so an automated
/// session can tail a file instead of scraping stdout or sampling the process
/// from outside.
/// </summary>
/// <remarks>
/// Enabled by pointing <c>EXCISE_TRACE_VIEWER</c> at a file. <c>=1</c> keeps its
/// original meaning (the viewer's free-text trace on stdout) and never starts
/// this sink. Off by default: without the variable no listener exists, and every
/// instrument's <see cref="Instrument.Enabled"/> stays false.
/// <para>
/// Lines, all with <c>ts</c> and <c>kind</c>:
/// <c>session-start</c> (pid, intervalMs); <c>measurement</c> (one per histogram
/// record); <c>observation</c> (one per gauge/observable-counter reading, every
/// interval); <c>snapshot</c> (heap size and committed bytes as of the last GC,
/// current live heap, working set, cumulative CPU and collection counts, every
/// interval). The file is appended
/// to, so a restart adds a new <c>session-start</c> rather than truncating.
/// </para>
/// </remarks>
internal sealed class MetricsJsonlSink : IDisposable
{
    internal const string EnvironmentVariable = "EXCISE_TRACE_VIEWER";
    internal const string IntervalEnvironmentVariable = "EXCISE_METRICS_INTERVAL_MS";
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    // Excise.Viewer is Excise.Avalonia's ViewerMetrics meter (internal there).
    private static readonly HashSet<string> MeterNames = new(StringComparer.Ordinal)
    {
        "Excise.Viewer",
        AppMetrics.MeterName,
    };

    private readonly object _writeGate = new();
    private readonly StreamWriter _writer;
    private readonly MeterListener _listener;
    private readonly Timer _timer;
    private bool _disposed;

    internal string Path { get; }

    private MetricsJsonlSink(string path, TimeSpan interval)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // ReadWrite share so `tail -f` (or a test) can read while this appends.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        Write(new MetricsJsonLine
        {
            Kind = "session-start",
            Pid = Environment.ProcessId,
            IntervalMs = interval.TotalMilliseconds,
        });

        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (MeterNames.Contains(instrument.Meter.Name))
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => WriteMeasurement(i, v, tags));
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => WriteMeasurement(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => WriteMeasurement(i, v, tags));
        _listener.Start();

        _timer = new Timer(static state => ((MetricsJsonlSink)state!).Tick(), this, interval, interval);
    }

    /// <summary>
    /// The JSONL path <paramref name="value"/> names, or null when it does not
    /// name one. <c>1</c> is the stdout trace; other values count as a path only
    /// when rooted, containing a directory separator, or ending in <c>.jsonl</c>,
    /// so a stray <c>true</c> or <c>0</c> does not create a file.
    /// </summary>
    internal static string? ResolvePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "1")
            return null;

        value = value.Trim();
        bool namesAFile = System.IO.Path.IsPathRooted(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        return namesAFile ? System.IO.Path.GetFullPath(value) : null;
    }

    internal static MetricsJsonlSink? TryStartFromEnvironment(ILogger logger)
    {
        var interval = DefaultInterval;
        if (int.TryParse(Environment.GetEnvironmentVariable(IntervalEnvironmentVariable),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            interval = TimeSpan.FromMilliseconds(ms);

        return TryStart(Environment.GetEnvironmentVariable(EnvironmentVariable), logger, interval);
    }

    /// <summary>Start a sink for <paramref name="value"/>, or return null (not a path, or unwritable).</summary>
    internal static MetricsJsonlSink? TryStart(string? value, ILogger logger, TimeSpan interval)
    {
        var path = ResolvePath(value);
        if (path == null)
            return null;

        try
        {
            var sink = new MetricsJsonlSink(path, interval);
            logger.LogInformation("Writing live metrics JSONL to {Path} every {IntervalMs} ms", path, interval.TotalMilliseconds);
            return sink;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Failed to start live metrics JSONL sink at {Path}", path);
            return null;
        }
    }

    /// <summary>Read every observable instrument and write a runtime snapshot. Runs on the timer; internal for tests.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            _listener.RecordObservableInstruments();

            // GCMemoryInfo describes the LAST collection (all zero before the
            // first one); live heap and working set are current.
            var gc = GC.GetGCMemoryInfo();
            var cpu = Environment.CpuUsage;
            Write(new MetricsJsonLine
            {
                Kind = "snapshot",
                LastGcHeapSizeBytes = gc.HeapSizeBytes,
                LastGcCommittedBytes = gc.TotalCommittedBytes,
                LiveHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                WorkingSetBytes = Environment.WorkingSet,
                CpuTotalMs = cpu.TotalTime.TotalMilliseconds,
                CpuUserMs = cpu.UserTime.TotalMilliseconds,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
            });
        }
        catch (ObjectDisposedException)
        {
            // A tick racing Dispose.
        }
    }

    private void WriteMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string>? tagMap = null;
        if (!tags.IsEmpty)
        {
            tagMap = new Dictionary<string, string>(tags.Length, StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value switch
                {
                    null => string.Empty,
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    var other => other.ToString() ?? string.Empty,
                };
            }
        }

        Write(new MetricsJsonLine
        {
            Kind = instrument.IsObservable ? "observation" : "measurement",
            Meter = instrument.Meter.Name,
            Instrument = instrument.Name,
            Unit = instrument.Unit,
            Value = value,
            Tags = tagMap,
        });
    }

    private void Write(MetricsJsonLine line)
    {
        line.Timestamp = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(line, MetricsJsonContext.Default.MetricsJsonLine);
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
        _timer.Dispose();
        _listener.Dispose();
        lock (_writeGate)
        {
            _writer.Dispose();
        }
    }
}

/// <summary>One line of the #1491 metrics JSONL. Null fields are omitted.</summary>
internal sealed class MetricsJsonLine
{
    [JsonPropertyName("ts")] public DateTime Timestamp { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("intervalMs")] public double? IntervalMs { get; set; }
    [JsonPropertyName("meter")] public string? Meter { get; set; }
    [JsonPropertyName("instrument")] public string? Instrument { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
    [JsonPropertyName("lastGcHeapSizeBytes")] public long? LastGcHeapSizeBytes { get; set; }
    [JsonPropertyName("lastGcCommittedBytes")] public long? LastGcCommittedBytes { get; set; }
    [JsonPropertyName("liveHeapBytes")] public long? LiveHeapBytes { get; set; }
    [JsonPropertyName("allocatedBytes")] public long? AllocatedBytes { get; set; }
    [JsonPropertyName("workingSetBytes")] public long? WorkingSetBytes { get; set; }
    [JsonPropertyName("cpuTotalMs")] public double? CpuTotalMs { get; set; }
    [JsonPropertyName("cpuUserMs")] public double? CpuUserMs { get; set; }
    [JsonPropertyName("gen0Collections")] public int? Gen0Collections { get; set; }
    [JsonPropertyName("gen1Collections")] public int? Gen1Collections { get; set; }
    [JsonPropertyName("gen2Collections")] public int? Gen2Collections { get; set; }
}

/// <summary>
/// Source-generated, single-line JSON for <see cref="MetricsJsonLine"/>. Separate
/// from <see cref="ExciseJsonContext"/> because that context is
/// <c>WriteIndented = true</c>, which would break one-object-per-line.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MetricsJsonLine))]
internal partial class MetricsJsonContext : JsonSerializerContext
{
}
