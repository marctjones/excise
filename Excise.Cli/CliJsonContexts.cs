using System.Text.Json;
using System.Text.Json.Serialization;
using Excise.Cli.Commands;
using Excise.Core.Automation;

namespace Excise.Cli;

/// <summary>
/// #1389 — every JSON type the CLI writes or reads, declared for the
/// <c>System.Text.Json</c> source generator.
///
/// <para><b>Why this exists.</b> <c>JsonSerializer.Serialize(value, options)</c> discovers a
/// type's shape by reflection. The trimmer cannot see that discovery, so under Native AOT the
/// properties are trimmed away and the CLI emits <c>{}</c> — silently, at runtime, with no
/// build error. Every such call site raised IL2026/IL3050 and those warnings were the only
/// thing standing between a working <c>--json</c> and an empty one. Declaring the types here
/// makes the serializer a compile-time artifact instead.</para>
///
/// <para><b>Three contexts, because options are baked in.</b> A generated context carries the
/// <c>[JsonSourceGenerationOptions]</c> it was declared with, so each distinct option set the
/// CLI used needs its own context. The sets are not arbitrary and must not be unified: the
/// batch progress stream is deliberately un-indented (it is one JSON object per line, and a
/// line-oriented consumer breaks if an event spans lines), and two commands never carried
/// <c>WhenWritingNull</c>. Collapsing them would change the bytes those commands emit, which
/// is the one thing this change must not do.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InfoCommand.DocumentInfoJsonReport))]
[JsonSerializable(typeof(AuditCommand.AuditJsonReport))]
[JsonSerializable(typeof(ValidateCommand.ValidationJsonReport))]
[JsonSerializable(typeof(RenderCommand.RenderPageJsonReport))]
[JsonSerializable(typeof(TextCommand.TextInspectionJsonReport))]
[JsonSerializable(typeof(UnredactReport))]
[JsonSerializable(typeof(Program.AutomationBatchReport))]
[JsonSerializable(typeof(Program.AutomationBatchWorkflow))]
[JsonSerializable(typeof(Program.InfoStepResult))]
[JsonSerializable(typeof(Program.TextStepResult))]
[JsonSerializable(typeof(Program.RenderStepResult))]
[JsonSerializable(typeof(Program.FillFormStepResult))]
[JsonSerializable(typeof(Program.AddFieldStepResult))]
[JsonSerializable(typeof(Program.RedactionStepResult))]
[JsonSerializable(typeof(Program.AuditStepResult))]
internal partial class CliJsonContext : JsonSerializerContext;

/// <summary>
/// The batch <c>--progress</c> stream: camelCase, nulls dropped, and <b>not</b> indented, so
/// each event stays on one line for a streaming consumer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Program.AutomationProgressEvent))]
internal partial class CliProgressJsonContext : JsonSerializerContext;

/// <summary>
/// <c>save-size-report</c> and <c>commands</c>: camelCase and indented, but with no
/// null-dropping — these two predate that convention and their output is pinned by callers.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(SaveSizeReportCommand.SaveSizeReport))]
[JsonSerializable(typeof(IReadOnlyList<PdfCommandMetadata>))]
internal partial class CliPlainJsonContext : JsonSerializerContext;
