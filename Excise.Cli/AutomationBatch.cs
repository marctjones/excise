using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Excise.Cli.Commands;
using Excise.Core.Automation;
using Excise.Core.Document;
using Excise.Core.Parsing;

namespace Excise.Cli;

partial class Program
{
    private const int AutomationExitSuccess = 0;
    private const int AutomationExitOperationFailed = 1;
    private const int AutomationExitContractError = 2;

    private static readonly JsonSerializerOptions AutomationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions AutomationProgressJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static Command CreateBatchCommand()
    {
        var workflowArg = new Argument<FileInfo>("workflow")
        {
            Description = "Automation workflow JSON file",
        };
        var outputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Optional JSON report path",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the final workflow report as JSON to stdout",
            DefaultValueFactory = _ => false,
        };
        var progressOption = new Option<bool>("--progress")
        {
            Description = "Write newline-delimited JSON progress events to stderr",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("batch", "Run a stable JSON automation workflow without screen automation")
        {
            workflowArg,
            outputOption,
            jsonOption,
            progressOption,
        };

        command.SetAction(parseResult => ExecuteBatchCommand(
            parseResult.GetValue(workflowArg)!,
            parseResult.GetValue(outputOption),
            parseResult.GetValue(jsonOption),
            parseResult.GetValue(progressOption)));

        return command;
    }

    private static int ExecuteBatchCommand(FileInfo workflowFile, FileInfo? outputFile, bool json, bool progress)
    {
        if (!workflowFile.Exists)
            return CompleteBatchContractError(json, outputFile, $"Workflow file not found: {workflowFile.FullName}");

        AutomationBatchWorkflow? workflow;
        try
        {
            workflow = JsonSerializer.Deserialize<AutomationBatchWorkflow>(
                File.ReadAllText(workflowFile.FullName),
                AutomationJsonOptions);
        }
        catch (JsonException ex)
        {
            return CompleteBatchContractError(json, outputFile, $"Invalid workflow JSON: {ex.Message}");
        }

        if (workflow == null || workflow.Steps is null || workflow.Steps.Length == 0)
            return CompleteBatchContractError(json, outputFile, "Workflow must contain at least one step.");

        var report = RunAutomationBatch(workflow, workflowFile.DirectoryName ?? Directory.GetCurrentDirectory(), progress);
        if (outputFile != null)
        {
            EnsureOutputParent(outputFile.FullName);
            File.WriteAllText(outputFile.FullName, JsonSerializer.Serialize(report, AutomationJsonOptions));
        }

        if (json)
        {
            WriteJson(report);
        }
        else
        {
            Console.WriteLine($"Batch {report.OverallStatus}: {report.PassedCount}/{report.Steps.Count} step(s) passed");
            foreach (var step in report.Steps)
            {
                var suffix = step.Error == null ? string.Empty : $" - {step.Error.Code}: {step.Error.Message}";
                Console.WriteLine($"  {step.Status} {step.Id} {step.Command}{suffix}");
            }
        }

        var exitCode = report.OverallStatus == "PASS"
            ? AutomationExitSuccess
            : report.Steps.Any(s => s.Error?.Category is "SCHEMA" or "SECURITY")
                ? AutomationExitContractError
                : AutomationExitOperationFailed;

        return exitCode;
    }

    private static int CompleteBatchContractError(bool json, FileInfo? outputFile, string message)
    {
        var report = new AutomationBatchReport(
            1,
            DateTimeOffset.UtcNow,
            "FAIL",
            0,
            0,
            [
                new AutomationBatchStepReport(
                    "workflow",
                    "workflow.load",
                    "FAIL",
                    0,
                    0,
                    null,
                    new AutomationStepError("INVALID_WORKFLOW", "SCHEMA", message))
            ]);

        if (outputFile != null)
        {
            EnsureOutputParent(outputFile.FullName);
            File.WriteAllText(outputFile.FullName, JsonSerializer.Serialize(report, AutomationJsonOptions));
        }

        if (json)
            WriteJson(report);
        else
            Console.Error.WriteLine(message);

        return AutomationExitContractError;
    }

    private static AutomationBatchReport RunAutomationBatch(
        AutomationBatchWorkflow workflow,
        string baseDirectory,
        bool progress)
    {
        var reports = new List<AutomationBatchStepReport>();
        var stopOnError = workflow.StopOnError ?? true;
        var total = workflow.Steps.Length;

        for (var i = 0; i < total; i++)
        {
            var step = workflow.Steps[i];
            var id = string.IsNullOrWhiteSpace(step.Id) ? $"step-{i + 1}" : step.Id!;
            var command = NormalizeAutomationCommand(step.Command);
            var stopwatch = Stopwatch.StartNew();

            WriteProgress(progress, new
            {
                type = "step-start",
                timestampUtc = DateTimeOffset.UtcNow,
                ordinal = i + 1,
                total,
                id,
                command,
            });

            AutomationBatchStepReport stepReport;
            try
            {
                if (command == null)
                    throw new AutomationContractException(
                        "UNKNOWN_COMMAND",
                        $"Unknown or missing automation command '{step.Command}'.");

                var result = ExecuteAutomationStep(command, step, baseDirectory);
                stopwatch.Stop();
                stepReport = new AutomationBatchStepReport(
                    id,
                    command,
                    "PASS",
                    AutomationExitSuccess,
                    stopwatch.ElapsedMilliseconds,
                    result,
                    null);
            }
            catch (AutomationContractException ex)
            {
                stopwatch.Stop();
                stepReport = new AutomationBatchStepReport(
                    id,
                    command ?? step.Command ?? string.Empty,
                    "FAIL",
                    AutomationExitContractError,
                    stopwatch.ElapsedMilliseconds,
                    null,
                    new AutomationStepError(ex.Code, ex.Category, ex.Message));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var error = CategorizeAutomationException(ex);
                stepReport = new AutomationBatchStepReport(
                    id,
                    command ?? step.Command ?? string.Empty,
                    "FAIL",
                    AutomationExitOperationFailed,
                    stopwatch.ElapsedMilliseconds,
                    null,
                    error);
            }

            reports.Add(stepReport);

            WriteProgress(progress, new
            {
                type = "step-complete",
                timestampUtc = DateTimeOffset.UtcNow,
                ordinal = i + 1,
                total,
                id,
                command = stepReport.Command,
                status = stepReport.Status,
                elapsedMs = stepReport.ElapsedMs,
                errorCode = stepReport.Error?.Code,
            });

            if (stepReport.Status != "PASS" && stopOnError)
                break;
        }

        var passed = reports.Count(r => r.Status == "PASS");
        return new AutomationBatchReport(
            workflow.SchemaVersion ?? 1,
            DateTimeOffset.UtcNow,
            passed == reports.Count && reports.Count == total ? "PASS" : "FAIL",
            passed,
            reports.Count,
            reports);
    }

    private static object ExecuteAutomationStep(
        string command,
        AutomationBatchStep step,
        string baseDirectory)
    {
        try
        {
            return ExecuteAutomationStepCore(command, step, baseDirectory);
        }
        catch (PdfPermissionDeniedException ex)
        {
            // #642: document /P permissions denied the step and the step
            // didn't set ignorePermissions: true. Same SECURITY category
            // as the #638 decrypt confirmation.
            var message = ex.Message.Contains("ignorePermissions", StringComparison.Ordinal)
                ? ex.Message
                : ex.Message + " (In batch workflows, the override is ignorePermissions: true on this step.)";
            throw new AutomationContractException("PERMISSION_DENIED", message, "SECURITY");
        }
        catch (DocumentPageOutOfRangeException ex)
        {
            throw new AutomationContractException(
                "PAGE_OUT_OF_RANGE",
                $"Page {ex.PageNumber} is outside the document range 1..{ex.PageCount}.");
        }
    }

    private static object ExecuteAutomationStepCore(
        string command,
        AutomationBatchStep step,
        string baseDirectory)
    {
        return command switch
        {
            PdfCommandIds.DocumentInfo => ExecuteInfoStep(step, baseDirectory),
            PdfCommandIds.ExtractText => ExecuteTextStep(step, baseDirectory),
            PdfCommandIds.RenderPage => ExecuteRenderStep(step, baseDirectory),
            PdfCommandIds.FillForm => ExecuteFillFormStep(step, baseDirectory),
            PdfCommandIds.AddFormField => ExecuteAddFieldStep(step, baseDirectory),
            PdfCommandIds.ApplyRedaction => ExecuteRedactionStep(step, baseDirectory),
            PdfCommandIds.AuditHiddenText => ExecuteAuditStep(step, baseDirectory),
            _ => throw new AutomationContractException("UNKNOWN_COMMAND", $"Unsupported automation command '{command}'."),
        };
    }

    private static object ExecuteInfoStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var result = InfoCommandHandler.Execute(new DocumentInfoRequest(input, step.Password));
        return new
        {
            inputPath = result.FilePath,
            result.Version,
            result.PageCount,
            result.Encrypted,
            result.Metadata,
        };
    }

    private static object ExecuteTextStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var result = TextInspectionHandler.Execute(new TextInspectionRequest(
            input,
            step.Password,
            step.Page,
            step.IgnorePermissions ?? false,
            step.ForAccessibility ?? false,
            AccessibilityHint: "forAccessibility: true",
            OverrideHint: "ignorePermissions: true on this step"));
        return new
        {
            inputPath = result.FilePath,
            result.PageCount,
            result.Pages,
        };
    }

    private static object ExecuteRenderStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var output = ResolveRequiredOutputPath(step.Output, baseDirectory);
        var page = step.Page ?? 1;
        var dpi = step.Dpi ?? 150;
        var rendered = RenderPageHandler.Execute(new RenderPageRequest(
            input,
            output,
            step.Password,
            page,
            dpi,
            step.IgnorePermissions ?? false,
            OverrideHint: "ignorePermissions: true on this step"));
        return new
        {
            inputPath = rendered.InputPath,
            outputPath = rendered.OutputPath,
            rendered.PageNumber,
            rendered.Dpi,
            rendered.Width,
            rendered.Height,
        };
    }

    private static object ExecuteFillFormStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var output = ResolveRequiredOutputPath(step.Output, baseDirectory);
        EnsureMutationWritesCopy(input, output);
        var fields = ResolveFieldAssignments(step);
        if (fields.Length == 0)
            throw new AutomationContractException("MISSING_FIELDS", "form.fillForm requires fields or field assignments.");

        EnsureOutputParent(output);
        var result = FormMutationHandler.Fill(new FillFormRequest(
            input,
            output,
            fields,
            step.Flatten ?? false,
            step.IgnorePermissions ?? false));
        return new
        {
            inputPath = result.InputPath,
            outputPath = result.OutputPath,
            updatedFieldCount = result.UpdatedFieldCount,
            flattened = result.Flattened,
        };
    }

    private static object ExecuteAddFieldStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var output = ResolveRequiredOutputPath(step.Output, baseDirectory);
        EnsureMutationWritesCopy(input, output);
        if (string.IsNullOrWhiteSpace(step.Name))
            throw new AutomationContractException("MISSING_NAME", "form.addField requires name.");
        if (string.IsNullOrWhiteSpace(step.Rect))
            throw new AutomationContractException("MISSING_RECT", "form.addField requires rect.");

        EnsureOutputParent(output);
        var result = FormMutationHandler.AddField(new AddFieldRequest(
            input,
            output,
            step.Type ?? "Text",
            step.Name!,
            step.Page ?? 1,
            step.Rect!,
            step.Value,
            step.Option ?? Array.Empty<string>(),
            step.IgnorePermissions ?? false));

        return new
        {
            inputPath = result.InputPath,
            outputPath = result.OutputPath,
            fieldName = result.FieldName,
            fieldType = result.FieldType,
            pageNumber = result.PageNumber,
        };
    }

    private static object ExecuteRedactionStep(AutomationBatchStep step, string baseDirectory)
    {
        if (step.ConfirmDestructive != true)
            throw new AutomationContractException(
                "DESTRUCTIVE_CONFIRMATION_REQUIRED",
                "redaction.apply requires confirmDestructive: true.",
                "SECURITY");

        if (string.IsNullOrEmpty(step.Text))
            throw new AutomationContractException("MISSING_TEXT", "redaction.apply requires non-empty text.");

        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var output = ResolveRequiredOutputPath(step.Output, baseDirectory);
        EnsureMutationWritesCopy(input, output);
        EnsureOutputParent(output);

        // #643: encrypted sources re-encrypt by default with the same
        // parameters and the step's password; allowDecrypt: true is now the
        // explicit opt-OUT that writes an unprotected copy (it was the #638
        // opt-in to proceed at all, back when excise could not write /Encrypt).
        var result = RedactCommandHandler.Execute(new RedactCommandRequest(
            input,
            output,
            step.Text!,
            step.CaseSensitive ?? false,
            step.AllowDecrypt ?? false,
            Password: step.Password));
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic);
        return new
        {
            inputPath = input,
            outputPath = output,
            redactedOccurrenceCount = result.Count,
            caseSensitive = step.CaseSensitive ?? false,
            // #916/#905 — carriers the redaction could not examine (bookmark
            // titles, annotations away from the box, terms under the scrub
            // floor). A batch run is unattended, so reporting this in the step
            // result is the only way it reaches anyone.
            carrierNotes = result.CarrierNotes,
        };
    }

    private static object ExecuteAuditStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var result = AuditInspectionHandler.Execute(new AuditInspectionRequest(
            input,
            step.Password,
            Deep: false));
        var hits = result.StructuralHits;
        if (hits.Count > 0 && step.AllowFindings != true)
            throw new AutomationValidationException(
                "HIDDEN_TEXT_FOUND",
                $"Hidden-text audit found {hits.Count} issue(s). Set allowFindings: true to record findings without failing the workflow.",
                new
                {
                    inputPath = input,
                    hitCount = hits.Count,
                });

        return new
        {
            inputPath = result.FilePath,
            hitCount = hits.Count,
            hits = hits.Select(hit => new
            {
                hit.PageNumber,
                hit.Text,
                hit.HiddenBy,
                bbox = new[]
                {
                    hit.BoundingBox.Left,
                    hit.BoundingBox.Bottom,
                    hit.BoundingBox.Right,
                    hit.BoundingBox.Top,
                },
            }).ToArray(),
        };
    }

    private static void WriteJson(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, AutomationJsonOptions));

    private static string? NormalizeAutomationCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        return command.Trim() switch
        {
            "info" => PdfCommandIds.DocumentInfo,
            "text" => PdfCommandIds.ExtractText,
            "render" => PdfCommandIds.RenderPage,
            "fill-form" => PdfCommandIds.FillForm,
            "add-field" => PdfCommandIds.AddFormField,
            "redact" => PdfCommandIds.ApplyRedaction,
            "audit" or "audit-hidden-text" => PdfCommandIds.AuditHiddenText,
            var value => value,
        };
    }

    private static string ResolveRequiredInputPath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AutomationContractException("MISSING_INPUT", "Step requires an input PDF path.");

        var resolved = ResolvePath(path, baseDirectory);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Input PDF not found: {resolved}", resolved);

        return resolved;
    }

    private static string ResolveRequiredOutputPath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AutomationContractException("MISSING_OUTPUT", "Step requires an output path.");

        return ResolvePath(path, baseDirectory);
    }

    private static string ResolvePath(string path, string baseDirectory)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    private static void EnsureMutationWritesCopy(string input, string output)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
            throw new AutomationContractException(
                "UNSAFE_OVERWRITE_REFUSED",
                "Mutating automation commands must write to a different output path.",
                "SECURITY");
    }

    private static void EnsureOutputParent(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static string[] ResolveFieldAssignments(AutomationBatchStep step)
    {
        var assignments = new List<string>();
        if (step.Field != null)
            assignments.AddRange(step.Field);

        if (step.Fields != null)
        {
            foreach (var (name, value) in step.Fields)
                assignments.Add($"{name}={value}");
        }

        return assignments.ToArray();
    }

    private static AutomationStepError CategorizeAutomationException(Exception ex)
    {
        if (ex is AutomationValidationException validation)
            return new AutomationStepError(validation.Code, "VALIDATION", validation.Message);
        if (ex is FileNotFoundException)
            return new AutomationStepError("FILE_NOT_FOUND", "INPUT", ex.Message);
        if (ex is PdfEncryptionNotSupportedException)
            return new AutomationStepError("PASSWORD_OR_ENCRYPTION_ERROR", "INPUT", ex.Message);
        if (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            return new AutomationStepError("INVALID_INPUT", "INPUT", ex.Message);

        return new AutomationStepError("OPERATION_FAILED", "RUNTIME", ex.Message);
    }

    private static void WriteProgress(bool enabled, object progressEvent)
    {
        if (!enabled)
            return;

        Console.Error.WriteLine(JsonSerializer.Serialize(progressEvent, AutomationProgressJsonOptions));
    }

    private sealed class AutomationContractException(string code, string message, string category = "SCHEMA")
        : Exception(message)
    {
        public string Code { get; } = code;
        public string Category { get; } = category;
    }

    private sealed class AutomationValidationException(string code, string message, object? result = null)
        : Exception(message)
    {
        public string Code { get; } = code;
        public object? Result { get; } = result;
    }



    private sealed record AutomationBatchWorkflow(
        int? SchemaVersion,
        bool? StopOnError,
        AutomationBatchStep[] Steps);

    private sealed record AutomationBatchStep(
        string? Id,
        string? Command,
        string? Input,
        string? Output,
        string? Password,
        int? Page,
        int? Dpi,
        string? Text,
        bool? CaseSensitive,
        bool? ConfirmDestructive,
        bool? AllowDecrypt,
        bool? Flatten,
        string[]? Field,
        Dictionary<string, string>? Fields,
        string? Type,
        string? Name,
        string? Rect,
        string? Value,
        string[]? Option,
        bool? AllowFindings,
        bool? IgnorePermissions,
        bool? ForAccessibility);

    private sealed record AutomationBatchReport(
        int SchemaVersion,
        DateTimeOffset GeneratedUtc,
        string OverallStatus,
        int PassedCount,
        int CompletedCount,
        IReadOnlyList<AutomationBatchStepReport> Steps);

    private sealed record AutomationBatchStepReport(
        string Id,
        string Command,
        string Status,
        int ExitCode,
        long ElapsedMs,
        object? Result,
        AutomationStepError? Error);

    private sealed record AutomationStepError(string Code, string Category, string Message);
}
