using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pdfe.Core.Automation;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Text.Segmentation;
using Pdfe.Rendering;
using SkiaSharp;

namespace Pdfe.Cli;

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
        Environment.ExitCode = AutomationExitSuccess;

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

        Environment.ExitCode = exitCode;
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

        Environment.ExitCode = AutomationExitContractError;
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
        using var doc = OpenPdfDocument(input, step.Password);
        return new
        {
            inputPath = input,
            version = doc.Version,
            pageCount = doc.PageCount,
            encrypted = doc.IsEncrypted,
            metadata = new
            {
                doc.Title,
                doc.Author,
                doc.Subject,
                doc.Creator,
                doc.Producer,
            },
        };
    }

    private static object ExecuteTextStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        using var doc = OpenPdfDocument(input, step.Password);
        var pages = ReadTextPages(doc, step.Page);
        return new
        {
            inputPath = input,
            pageCount = doc.PageCount,
            pages,
        };
    }

    private static object ExecuteRenderStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        var output = ResolveRequiredOutputPath(step.Output, baseDirectory);
        using var doc = OpenPdfDocument(input, step.Password);
        var page = step.Page ?? 1;
        var dpi = step.Dpi ?? 150;
        ValidatePageNumber(doc, page);

        var rendered = RenderPageToPng(doc, page, dpi, output);
        return new
        {
            inputPath = input,
            outputPath = output,
            pageNumber = page,
            dpi,
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
        var updated = RunFillForm(input, output, fields, step.Flatten ?? false);
        return new
        {
            inputPath = input,
            outputPath = output,
            updatedFieldCount = updated,
            flattened = step.Flatten ?? false,
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
        RunAddField(
            input,
            output,
            step.Type ?? "Text",
            step.Name!,
            step.Page ?? 1,
            step.Rect!,
            step.Value,
            step.Option ?? Array.Empty<string>());

        return new
        {
            inputPath = input,
            outputPath = output,
            fieldName = step.Name,
            fieldType = step.Type ?? "Text",
            pageNumber = step.Page ?? 1,
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

        int count;
        try
        {
            count = RunRedact(input, output, step.Text!, step.CaseSensitive ?? false, step.AllowDecrypt ?? false);
        }
        catch (PdfWouldLoseEncryptionException ex)
        {
            throw new AutomationContractException("DECRYPT_CONFIRMATION_REQUIRED", ex.Message, "SECURITY");
        }
        return new
        {
            inputPath = input,
            outputPath = output,
            redactedOccurrenceCount = count,
            caseSensitive = step.CaseSensitive ?? false,
        };
    }

    private static object ExecuteAuditStep(AutomationBatchStep step, string baseDirectory)
    {
        var input = ResolveRequiredInputPath(step.Input, baseDirectory);
        using var doc = OpenPdfDocument(input, step.Password);
        var hits = HiddenTextDetector.Scan(doc);
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
            inputPath = input,
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

    private static PdfDocument OpenPdfDocument(string path, string? password)
        => string.IsNullOrEmpty(password)
            ? PdfDocument.Open(path)
            : PdfDocument.Open(path, password);

    private static RenderedPageResult RenderPageToPng(PdfDocument doc, int page, int dpi, string outputPath)
    {
        ValidatePageNumber(doc, page);
        EnsureOutputParent(outputPath);

        var renderer = new SkiaRenderer();
        var options = new RenderOptions { Dpi = dpi };
        using var bitmap = renderer.RenderPage(doc.GetPage(page), options);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        return new RenderedPageResult(bitmap.Width, bitmap.Height);
    }

    private static void WriteJson(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, AutomationJsonOptions));

    private static void WriteTextJson(string file, int pageCount, IReadOnlyList<TextPageResult> pages)
    {
        WriteJson(new
        {
            schemaVersion = 1,
            command = PdfCommandIds.ExtractText,
            status = "PASS",
            file,
            pageCount,
            pages,
        });
    }

    private static IReadOnlyList<TextPageResult> ReadTextPages(PdfDocument doc, int? page)
    {
        if (page.HasValue)
        {
            ValidatePageNumber(doc, page.Value);
            return [new TextPageResult(page.Value, doc.GetPage(page.Value).Text)];
        }

        return Enumerable.Range(1, doc.PageCount)
            .Select(pageNumber => new TextPageResult(pageNumber, doc.GetPage(pageNumber).Text))
            .ToArray();
    }

    private static void ValidatePageNumber(PdfDocument doc, int page)
    {
        if (page < 1 || page > doc.PageCount)
            throw new AutomationContractException(
                "PAGE_OUT_OF_RANGE",
                $"Page {page} is outside the document range 1..{doc.PageCount}.");
    }

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

    private sealed record RenderedPageResult(int Width, int Height);

    private sealed record TextPageResult(int PageNumber, string Text);

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
        bool? AllowFindings);

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
