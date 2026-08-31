using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class AutodetectFieldsCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArgument = new Argument<FileInfo?>("output")
        {
            Description = "Output PDF (required with --apply)",
            DefaultValueFactory = _ => null,
        };
        var applyOption = new Option<bool>("--apply")
        {
            Description = "Add the detected fields to the PDF and save to <output>",
            DefaultValueFactory = _ => false,
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();

        var command = new Command(
            "autodetect-fields",
            "Heuristically detect likely form-field locations on each page")
        {
            inputArgument,
            outputArgument,
            applyOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputArgument);
            var apply = parseResult.GetValue(applyOption);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            if (apply && output == null)
            {
                Console.Error.WriteLine("--apply requires an <output> PDF path.");
                return 1;
            }

            try
            {
                var result = FormMutationHandler.Autodetect(new AutodetectFieldsRequest(
                    input.FullName,
                    output?.FullName,
                    apply,
                    parseResult.GetValue(ignorePermissionsOption)));
                Console.WriteLine($"Detected {result.Suggestions.Count} field candidate(s):");
                foreach (var suggestion in result.Suggestions)
                {
                    Console.WriteLine(
                        $"  page {suggestion.PageNumber}  {suggestion.FieldType,-9}  " +
                        $"[{suggestion.Rect.Left:0.#},{suggestion.Rect.Bottom:0.#}-" +
                        $"{suggestion.Rect.Right:0.#},{suggestion.Rect.Top:0.#}]  " +
                        $"{suggestion.SuggestedName}  ({suggestion.Reason})");
                }

                if (apply)
                    Console.WriteLine($"Applied {result.AppliedFieldCount} field(s); wrote {result.OutputPath}");
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
