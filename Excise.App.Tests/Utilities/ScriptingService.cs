using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Excise.App.ViewModels;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Service for executing C# scripts against the MainWindowViewModel.
/// Enables GUI automation and testing through Roslyn scripting.
/// </summary>
internal class ScriptingService
{
    // Every assembly the host loaded: the view model's base types (ReactiveUI, System.ObjectModel)
    // must be referenced for a script to bind to its members.
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());

    private static readonly string[] Imports = ScriptOptions.Default.Imports
        .Concat(new[]
        {
            "System",
            "System.IO",
            "System.Linq",
            "System.Collections.Generic",
            "System.Threading.Tasks",
            "Excise.App.ViewModels",
            "Excise.App.Services",
            "Excise.App.Models",
        })
        .Distinct()
        .ToArray();

    private readonly MainWindowViewModel _viewModel;

    public ScriptingService(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>
    /// Compiles a script into an assembly named <c>Excise.App.Scripts</c>, the name
    /// <c>Excise.App</c> grants <c>InternalsVisibleTo</c>. <c>CSharpScript</c> names its
    /// submission assembly with a fresh GUID, so it could not see the internal
    /// <see cref="MainWindowViewModel"/> that scripts take as their globals.
    /// </summary>
    private static CSharpCompilation CreateCompilation(string scriptCode) =>
        CSharpCompilation.CreateScriptCompilation(
            "Excise.App.Scripts",
            CSharpSyntaxTree.ParseText(scriptCode, new CSharpParseOptions(kind: SourceCodeKind.Script)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: Imports),
            returnType: typeof(object),
            globalsType: typeof(MainWindowViewModel));

    /// <summary>
    /// Executes a C# script with the MainWindowViewModel as the global context.
    /// </summary>
    /// <param name="scriptCode">The C# code to execute</param>
    /// <returns>The result of the script execution</returns>
    public async Task<ScriptExecutionResult> ExecuteAsync(string scriptCode)
    {
        try
        {
            var compilation = CreateCompilation(scriptCode);

            // Compile the script first to catch syntax errors. Only Error-severity
            // diagnostics block execution — warnings such as "Assuming assembly
            // reference X.0.0.0 used by ReactiveUI matches identity Y.0.0.0" are
            // benign on cross-major-version runtimes (e.g. ReactiveUI built for
            // net8 loaded by net10 host) and should not fail the script.
            var fatal = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (fatal.Count > 0)
            {
                var errors = fatal.Select(d => d.GetMessage()).ToList();
                return ScriptExecutionResult.FromError(string.Join("\n", errors));
            }

            using var dll = new MemoryStream();
            var emitted = compilation.Emit(dll);
            if (!emitted.Success)
            {
                return ScriptExecutionResult.FromError(string.Join("\n",
                    emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
            }

            // The script's entry point takes the submission array: slot 0 holds the
            // globals, slot 1 receives the submission itself.
            var factory = Assembly.Load(dll.ToArray()).GetType("Script")!.GetMethod("<Factory>")!;
            Task<object?> run;
            try
            {
                run = (Task<object?>)factory.Invoke(null, new object?[] { new object?[] { _viewModel, null } })!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            return ScriptExecutionResult.FromSuccess(await run);
        }
        catch (Exception ex)
        {
            return ScriptExecutionResult.FromError($"Runtime error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Executes a C# script from a file.
    /// </summary>
    /// <param name="scriptFilePath">Path to the .csx file</param>
    /// <returns>The result of the script execution</returns>
    public async Task<ScriptExecutionResult> ExecuteFileAsync(string scriptFilePath)
    {
        if (!File.Exists(scriptFilePath))
        {
            return ScriptExecutionResult.FromError($"Script file not found: {scriptFilePath}");
        }

        try
        {
            var scriptCode = await File.ReadAllTextAsync(scriptFilePath);
            return await ExecuteAsync(scriptCode);
        }
        catch (Exception ex)
        {
            return ScriptExecutionResult.FromError($"Failed to read script file: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates a script without executing it.
    /// </summary>
    /// <param name="scriptCode">The C# code to validate</param>
    /// <returns>List of compilation errors, empty if valid</returns>
    public List<string> ValidateScript(string scriptCode)
    {
        try
        {
            return CreateCompilation(scriptCode).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToList();
        }
        catch (Exception ex)
        {
            return new List<string> { ex.Message };
        }
    }
}

/// <summary>
/// Result of a script execution.
/// </summary>
public class ScriptExecutionResult
{
    public bool Success { get; init; }
    public object? ReturnValue { get; init; }
    public string? ErrorMessage { get; init; }

    public static ScriptExecutionResult FromSuccess(object? returnValue = null) =>
        new() { Success = true, ReturnValue = returnValue };

    public static ScriptExecutionResult FromError(string errorMessage) =>
        new() { Success = false, ErrorMessage = errorMessage };

    public override string ToString()
    {
        if (Success)
            return ReturnValue?.ToString() ?? "Script executed successfully";
        return $"Error: {ErrorMessage}";
    }
}
