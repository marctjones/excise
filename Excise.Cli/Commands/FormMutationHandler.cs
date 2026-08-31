using System.Globalization;
using Excise.Core.Document;

namespace Excise.Cli.Commands;

internal static class FormMutationHandler
{
    internal static FillFormResult Fill(
        FillFormRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePaths(request.InputPath, request.OutputPath);
        if (request.Fields.Count == 0)
            throw new ArgumentException("At least one field assignment is required.");
        cancellationToken.ThrowIfCancellationRequested();

        var input = RequireInput(request.InputPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        using var document = PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath);
        DocumentPermissionGuard.Require(
            document,
            DocumentAction.FillForms,
            "filling form fields",
            request.IgnorePermissions);

        var form = document.GetAcroForm()
            ?? throw new InvalidOperationException("Document has no /AcroForm — nothing to fill.");

        var updated = 0;
        foreach (var raw in request.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separator = raw.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException(
                    $"Malformed --field '{raw}'. Expected 'FullName=Value'.");
            }

            var name = raw[..separator];
            var value = raw[(separator + 1)..];
            var field = form.FindField(name)
                ?? throw new KeyNotFoundException($"Field '{name}' not found in document.");
            field.SetValue(value);
            updated++;
        }

        if (request.Flatten)
            document.FlattenAcroForm();
        cancellationToken.ThrowIfCancellationRequested();
        SavePreservingEncryption(document, outputPath);
        return new FillFormResult(input.FullName, outputPath, updated, request.Flatten);
    }

    internal static AddFieldResult AddField(
        AddFieldRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePaths(request.InputPath, request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        var rectangle = ParseRectangle(request.Rectangle);
        cancellationToken.ThrowIfCancellationRequested();

        var input = RequireInput(request.InputPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        using var document = PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath);
        DocumentPermissionGuard.Require(
            document,
            DocumentAction.ModifyContents,
            "adding form fields",
            request.IgnorePermissions);

        switch (request.Type.ToLowerInvariant())
        {
            case "text":
                document.AddTextField(request.PageNumber, rectangle, request.Name, defaultValue: request.Value);
                break;
            case "checkbox":
            case "btn":
            case "button":
                document.AddCheckBox(
                    request.PageNumber,
                    rectangle,
                    request.Name,
                    defaultChecked: string.Equals(request.Value, "Yes", StringComparison.OrdinalIgnoreCase));
                break;
            case "choice":
            case "combo":
            case "dropdown":
                if (request.Options.Count == 0)
                    throw new ArgumentException("--option is required at least once for --type Choice.");
                document.AddChoiceField(
                    request.PageNumber,
                    rectangle,
                    request.Name,
                    request.Options,
                    defaultValue: request.Value);
                break;
            case "signature":
            case "sig":
                document.AddSignatureField(request.PageNumber, rectangle, request.Name);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown field type '{request.Type}'. Use Text, Checkbox, Choice, or Signature.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SavePreservingEncryption(document, outputPath);
        return new AddFieldResult(
            input.FullName,
            outputPath,
            request.Type,
            request.Name,
            request.PageNumber,
            rectangle);
    }

    internal static AutodetectFieldsResult Autodetect(
        AutodetectFieldsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        if (request.Apply && string.IsNullOrWhiteSpace(request.OutputPath))
            throw new ArgumentException("--apply requires an output PDF path.");
        cancellationToken.ThrowIfCancellationRequested();

        var input = RequireInput(request.InputPath);
        var outputPath = request.OutputPath == null ? null : Path.GetFullPath(request.OutputPath);
        using var document = request.Apply
            ? PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath!)
            : PdfDocument.Open(input.FullName);
        if (request.Apply)
        {
            DocumentPermissionGuard.Require(
                document,
                DocumentAction.ModifyContents,
                "adding detected form fields (--apply)",
                request.IgnorePermissions);
        }

        var suggestions = PdfFormAutoDetector.Scan(document);
        cancellationToken.ThrowIfCancellationRequested();
        var applied = 0;
        if (request.Apply)
        {
            applied = PdfFormAutoDetector.Apply(document, suggestions);
            SavePreservingEncryption(document, outputPath!);
        }

        return new AutodetectFieldsResult(
            input.FullName,
            outputPath,
            suggestions,
            applied);
    }

    internal static PdfRectangle ParseRectangle(string value)
    {
        var parts = value.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new ArgumentException($"Expected --rect 'left,bottom,right,top'; got '{value}'.");

        var numbers = new double[4];
        for (var index = 0; index < numbers.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
                throw new ArgumentException($"Bad number in --rect: '{parts[index]}'.");
        }

        return new PdfRectangle(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static void SavePreservingEncryption(PdfDocument document, string outputPath)
        => document.Save(outputPath, document.GetReEncryptionOptions(userPassword: null));

    private static void ValidatePaths(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
    }

    private static FileInfo RequireInput(string inputPath)
    {
        var input = new FileInfo(inputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);
        return input;
    }
}

internal readonly record struct FillFormRequest(
    string InputPath,
    string OutputPath,
    IReadOnlyList<string> Fields,
    bool Flatten,
    bool IgnorePermissions);

internal sealed record FillFormResult(
    string InputPath,
    string OutputPath,
    int UpdatedFieldCount,
    bool Flattened);

internal readonly record struct AddFieldRequest(
    string InputPath,
    string OutputPath,
    string Type,
    string Name,
    int PageNumber,
    string Rectangle,
    string? Value,
    IReadOnlyList<string> Options,
    bool IgnorePermissions);

internal sealed record AddFieldResult(
    string InputPath,
    string OutputPath,
    string FieldType,
    string FieldName,
    int PageNumber,
    PdfRectangle Rectangle);

internal readonly record struct AutodetectFieldsRequest(
    string InputPath,
    string? OutputPath,
    bool Apply,
    bool IgnorePermissions);

internal sealed record AutodetectFieldsResult(
    string InputPath,
    string? OutputPath,
    IReadOnlyList<SuggestedField> Suggestions,
    int AppliedFieldCount);
