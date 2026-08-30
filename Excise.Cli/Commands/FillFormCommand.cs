using System.CommandLine;

namespace Excise.Cli.Commands;

/// <summary>
/// Builds the command-line adapter for assigning and optionally flattening AcroForm values.
/// </summary>
internal static class FillFormCommand
{
    internal delegate int FillFormOperation(
        string inputPath,
        string outputPath,
        string[] fields,
        bool flatten,
        bool ignorePermissions);

    /// <summary>
    /// Creates <c>excise fill-form</c> around the supplied permission option and form operation.
    /// </summary>
    internal static Command Create(
        Option<bool> ignorePermissionsOption,
        FillFormOperation runFillForm)
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
                Environment.ExitCode = 1;
                return;
            }

            if (fields == null || fields.Length == 0)
            {
                Console.Error.WriteLine("At least one --field name=value assignment is required.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                int set = runFillForm(
                    input.FullName,
                    output.FullName,
                    fields,
                    flatten,
                    ignorePermissions);
                Console.WriteLine($"Set {set} field value(s){(flatten ? " (flattened)" : "")}");
                Console.WriteLine($"Output: {output.FullName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }
}
