using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>The per-process cost report (<c>EXCISE_PERF_REPORT</c>) that the Native AOT performance harness reads.</summary>
public class ProcessPerfReportTests
{
    [Fact]
    public void Format_IsValidJson_WithEveryField()
    {
        var json = ProcessPerfReport.Format("redact", 3, 1234.56, 1200.04, 987_654_321, 12, 4, 1, 55.5, dynamicCode: false);

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        r.GetProperty("command").GetString().Should().Be("redact");
        r.GetProperty("exitCode").GetInt32().Should().Be(3);
        r.GetProperty("wallMs").GetDouble().Should().BeApproximately(1234.6, 0.01);
        r.GetProperty("allocatedBytes").GetInt64().Should().Be(987_654_321);
        r.GetProperty("gcCollections").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(12, 4, 1);
        r.GetProperty("nativeAot").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Format_EscapesTheCommandName_AndKeepsTheInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var json = ProcessPerfReport.Format("a\"b\\c", 0, 1.5, 1.5, 1, 0, 0, 0, 0.5, dynamicCode: true);

            using var doc = JsonDocument.Parse(json);   // a comma decimal separator would make this invalid
            doc.RootElement.GetProperty("command").GetString().Should().Be("a\"b\\c");
            doc.RootElement.GetProperty("nativeAot").GetBoolean().Should().BeFalse();
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TryWrite_WritesTheReportForTheRunningProcess_AndNeverThrowsOnABadPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-perf-report-{Guid.NewGuid():N}.json");
        try
        {
            ProcessPerfReport.TryWrite(path, new[] { "info", "x.pdf" }, 0, TimeSpan.FromMilliseconds(5));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            doc.RootElement.GetProperty("command").GetString().Should().Be("info");
            doc.RootElement.GetProperty("allocatedBytes").GetInt64().Should().BeGreaterThan(0);

            var bad = () => ProcessPerfReport.TryWrite("/nonexistent-dir/x/report.json", new string[0], 0, TimeSpan.Zero);
            bad.Should().NotThrow();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
