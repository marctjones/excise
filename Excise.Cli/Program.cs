using System.CommandLine;
using Excise.Cli.Commands;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Excise.Cli.Tests")]

namespace Excise.Cli;

partial class Program
{
    static async Task<int> Main(string[] args)
    {
        // EXCISE_PERF_REPORT=<file>: write what this process cost (ProcessPerfReport). Unset, this is just RunAsync.
        var report = Environment.GetEnvironmentVariable(ProcessPerfReport.EnvironmentVariable);
        if (string.IsNullOrEmpty(report))
            return await RunAsync(args);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var exitCode = await RunAsync(args);
        ProcessPerfReport.TryWrite(report, args, exitCode, started.Elapsed);
        return exitCode;
    }

    /// <summary>
    /// Build and invoke the root command. Exposed for tests so they can
    /// exercise the CLI parsing + handler pipeline without spawning a
    /// subprocess.
    /// </summary>
    internal static Task<int> RunAsync(string[] args)
    {
        var rootCommand = new RootCommand("excise - PDF toolkit powered by Excise.Core")
        {
            CommandMetadataCommand.Create(),
            CreateBatchCommand(),
            InfoCommand.Create(),
            ValidateCommand.Create(),
            TextCommand.Create(),
            LettersCommand.Create(),
            RenderCommand.Create(),
            RedactCommand.Create(),
            MergeCommand.Create(),
            SplitCommand.Create(),
            FillFormCommand.Create(),
            AddFieldCommand.Create(),
            AutodetectFieldsCommand.Create(),
            AuditCommand.Create(),
            UnredactCommand.Create(),
            OcrCommand.Create(),
            MakeSearchableCommand.Create(),
            EncryptCommand.Create(),
            DecryptCommand.Create(),
            OptimizeCommand.Create(),
            SaveSizeReportCommand.Create(),
        };

        // Command actions return their explicit invocation outcome. This keeps
        // test and embedded callers independent of process-global exit state.
        return Task.FromResult(rootCommand.Parse(args).Invoke());
    }

}
