using System.CommandLine;

namespace Excise.Cli.Commands;

/// <summary>
/// Builds the command-line adapter for assigning and optionally flattening AcroForm values.
/// </summary>
internal static class FillFormCommand
{
    internal static Command Create()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var fieldOption = new Option<string[]>("--field", "-f")
        {
            Description = "Field assignment in the form 'FullName=Value'. May be repeated for multiple fields.",
            AllowMultipleArgumentsPerToken = false,
        };
        var flattenOption = new Option<bool>("--flatten")
        {
            Description = "Bake values into page content and remove the form (non-interactive output)",
            DefaultValueFactory = _ => false,
        };

        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();
        var command = new Command(
            "fill-form",
            "Set AcroForm field values and save (optionally flatten to baked content)")
        {
            inputArg, outputArg, fieldOption, flattenOption, ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var fields = parseResult.GetValue(fieldOption);
            var flatten = parseResult.GetValue(flattenOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            if (fields == null || fields.Length == 0)
            {
                Console.Error.WriteLine("At least one --field name=value assignment is required.");
                return 1;
            }

            try
            {
                var result = FormMutationHandler.Fill(new FillFormRequest(
                    input.FullName,
                    output.FullName,
                    fields,
                    flatten,
                    ignorePermissions));
                Console.WriteLine($"Set {result.UpdatedFieldCount} field value(s){(flatten ? " (flattened)" : "")}");
                Console.WriteLine($"Output: {result.OutputPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
