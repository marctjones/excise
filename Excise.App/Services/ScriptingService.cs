using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Excise.App.ViewModels;

namespace Excise.App.Services;

/// <summary>
/// Service for executing C# scripts against the MainWindowViewModel.
/// Enables GUI automation and testing through Roslyn scripting.
/// </summary>
public class ScriptingService
{
    private readonly MainWindowViewModel _viewModel;
    private ScriptOptions? _scriptOptions;

    public ScriptingService(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>
    /// Gets or creates the script options with required references and imports.
    /// </summary>
    private ScriptOptions GetScriptOptions()
    {
        if (_scriptOptions != null)
            return _scriptOptions;

        // Add references to assemblies the script might need
        var assemblies = new[]
        {
            typeof(MainWindowViewModel).Assembly,  // Excise.App
            typeof(object).Assembly,               // System.Private.CoreLib
            typeof(Console).Assembly,              // System.Console
            typeof(File).Assembly,                 // System.IO.FileSystem
            typeof(Enumerable).Assembly,           // System.Linq
            Assembly.Load("System.Runtime"),
            Assembly.Load("System.Collections"),
        };

        // Add common namespaces
        var imports = new[]
        {
            "System",
            "System.IO",
            "System.Linq",
            "System.Collections.Generic",
            "System.Threading.Tasks",
            "Excise.App.ViewModels",
            "Excise.App.Services",
            "Excise.App.Models",
        };

        _scriptOptions = ScriptOptions.Default
            .AddReferences(assemblies)
            .AddImports(imports);

        return _scriptOptions;
    }

    /// <summary>
    /// Executes a C# script with the MainWindowViewModel as the global context.
    /// </summary>
    /// <param name="scriptCode">The C# code to execute</param>
    /// <returns>The result of the script execution</returns>
    public async Task<ScriptExecutionResult> ExecuteAsync(string scriptCode)
    {
        try
        {
            var options = GetScriptOptions();

            // Create and run the script with the ViewModel as global context
            var script = CSharpScript.Create(scriptCode, options, typeof(MainWindowViewModel));

            // Compile the script first to catch syntax errors. Only Error-severity
            // diagnostics block execution — warnings such as "Assuming assembly
            // reference X.0.0.0 used by ReactiveUI matches identity Y.0.0.0" are
            // benign on cross-major-version runtimes (e.g. ReactiveUI built for
            // net8 loaded by net10 host) and should not fail the script.
            var fatal = script.Compile()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToList();

            if (fatal.Count > 0)
            {
                var errors = fatal.Select(d => d.GetMessage()).ToList();
                return ScriptExecutionResult.FromError(string.Join("\n", errors));
            }

            // Execute the script
            var result = await script.RunAsync(_viewModel);

            return ScriptExecutionResult.FromSuccess(result.ReturnValue);
        }
        catch (CompilationErrorException ex)
        {
            return ScriptExecutionResult.FromError($"Compilation error: {ex.Message}");
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
            var options = GetScriptOptions();
            var script = CSharpScript.Create(scriptCode, options, typeof(MainWindowViewModel));
            return script.Compile()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
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
