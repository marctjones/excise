using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.App.Workspace;
using Excise.Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace Excise.App.Automation;

/// <summary>
/// Entry point for the in-app performance-scenario runner (#1497): reads the
/// scenario, builds the live target, runs it, writes the run manifest, quits.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PerfScenarioRunner"/> so the sequencing is
/// testable without Avalonia — this class is the only part that needs a window.
/// </remarks>
internal static class PerfScenarioHost
{
    internal const string ResultFileName = "scenario-result.json";
    internal const string ErrorFileName = "SCENARIO_ERROR.txt";

    internal static async Task RunAsync(
        Window window,
        MainWindowViewModel viewModel,
        ViewerCacheTrimCoordinator? cacheTrim,
        CacheTrimPolicy trimPolicy,
        ILogger logger,
        DocumentWorkspace? workspace = null,
        Func<MainWindow, ViewerCacheTrimCoordinator?>? cacheTrimFor = null)
    {
        PerfScenarioOptions? options = null;
        try
        {
            options = PerfScenarioOptions.FromEnvironment();
            Directory.CreateDirectory(options.OutputDirectory);

            var scenarios = PerfScenarioFile.Parse(File.ReadAllText(options.ScenarioFile));
            var scenario = PerfScenarioFile.Select(scenarios, options.ScenarioId);

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            if (viewer == null)
            {
                logger.LogWarning(
                    "Performance scenario: PdfViewerControl not found; viewer counters will be zero");
            }

            // #1551-#1554: with the workspace, every step follows the active
            // document into whichever window shows it.
            var target = new AppPerfScenarioTarget(viewModel, viewer, cacheTrim, workspace, cacheTrimFor);
            using var journal = new PerfStepJournal(options.OutputDirectory)
            {
                AckTimeout = options.SampleWindow,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            var result = await PerfScenarioRunner
                .RunAsync(target, scenario, options, journal, cts.Token)
                .ConfigureAwait(true);

            WriteResult(options, scenario, result, cacheTrim != null, viewer != null, trimPolicy);
            logger.LogInformation(
                "Performance scenario {Scenario} finished: {Steps} steps, {Failures} failures, " +
                "{Acked}/{Boundaries} boundaries sampled",
                scenario.Id, result.Steps, result.Failures,
                result.AcknowledgedBoundaries,
                result.AcknowledgedBoundaries + result.UnacknowledgedBoundaries);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Performance scenario failed");
            TryWriteError(options, ex);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// The run manifest. Shape follows the RenderTools report family
    /// (<c>schemaVersion</c> / <c>generatedUtc</c> / <c>issues</c> /
    /// <c>configuration</c>) so a consumer that already reads
    /// <c>reference-performance.json</c> needs no new conventions.
    /// </summary>
    private static void WriteResult(
        PerfScenarioOptions options,
        PerfScenario scenario,
        PerfScenarioResult result,
        bool cacheTrimAvailable,
        bool viewerAvailable,
        CacheTrimPolicy trimPolicy)
    {
        var builder = new StringBuilder(1024);
        builder.AppendLine("{");
        builder.AppendLine("  \"schemaVersion\": 1,");
        builder.Append("  \"generatedUtc\": \"")
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .AppendLine("\",");
        builder.AppendLine("  \"issues\": [\"#1497\"],");
        builder.AppendLine("  \"kind\": \"gui-perf-scenario\",");
        builder.Append("  \"scenario\": \"").Append(Escape(scenario.Id)).AppendLine("\",");
        builder.Append("  \"why\": \"").Append(Escape(scenario.Why)).AppendLine("\",");
        builder.Append("  \"repeat\": ").Append(result.Repeat).AppendLine(",");
        builder.Append("  \"pid\": ").Append(Environment.ProcessId).AppendLine(",");
        builder.Append("  \"steps\": ").Append(result.Steps).AppendLine(",");
        builder.Append("  \"failures\": ").Append(result.Failures).AppendLine(",");
        builder.Append("  \"boundariesSampled\": ").Append(result.AcknowledgedBoundaries).AppendLine(",");
        builder.Append("  \"boundariesUnsampled\": ").Append(result.UnacknowledgedBoundaries).AppendLine(",");
        builder.Append("  \"totalMs\": ")
            .Append(result.TotalMs.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(",");
        builder.Append("  \"cacheTrimAvailable\": ")
            .Append(cacheTrimAvailable ? "true" : "false").AppendLine(",");
        builder.Append("  \"viewerAvailable\": ")
            .Append(viewerAvailable ? "true" : "false").AppendLine(",");
        builder.AppendLine("  \"configuration\": {");
        builder.Append("    \"scenarioFile\": \"").Append(Escape(options.ScenarioFile)).AppendLine("\",");
        builder.Append("    \"sampleWindowMs\": ")
            .Append(((long)options.SampleWindow.TotalMilliseconds)).AppendLine(",");
        builder.Append("    \"stepTimeoutMs\": ")
            .Append(((long)options.StepTimeout.TotalMilliseconds)).AppendLine(",");
        builder.Append("    \"idleTimeoutMs\": ")
            .Append(((long)options.IdleTimeout.TotalMilliseconds)).AppendLine(",");
        builder.Append("    \"runtimeMode\": \"")
            .Append(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled ? "jit" : "aot")
            .AppendLine("\",");
        builder.Append("    \"processorCount\": ").Append(Environment.ProcessorCount).AppendLine(",");
        // The policy is a USER PREFERENCE, and it decides whether a trim step
        // does anything at all. A run that does not state it cannot explain its
        // own trim rows later.
        builder.AppendLine("    \"cacheTrimPolicy\": {");
        builder.Append("      \"onMemoryPressure\": ")
            .Append(trimPolicy.OnMemoryPressure ? "true" : "false").AppendLine(",");
        builder.Append("      \"softTriggers\": ")
            .Append(trimPolicy.SoftTriggers ? "true" : "false").AppendLine(",");
        builder.Append("      \"idleDelaySeconds\": ")
            .Append(trimPolicy.IdleDelay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine();
        builder.AppendLine("    }");
        builder.AppendLine("  }");
        builder.AppendLine("}");

        File.WriteAllText(Path.Combine(options.OutputDirectory, ResultFileName), builder.ToString());
    }

    private static void TryWriteError(PerfScenarioOptions? options, Exception ex)
    {
        try
        {
            var directory = options?.OutputDirectory
                ?? Environment.GetEnvironmentVariable(PerfScenarioOptions.OutputVariable);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ErrorFileName), ex.ToString());
        }
        catch (IOException)
        {
            // Nothing useful left to do; the log line already carries it.
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Quit through the normal shutdown path — the same route the app menu's
    /// Quit takes, and the same one <see cref="VisualTraceRunner"/> uses. Never
    /// SIGTERM: the runbook is explicit that a killed process skips the
    /// teardown whose cost is part of what is being measured.
    /// </summary>
    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }
}
