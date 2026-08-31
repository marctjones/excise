using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class AddFieldCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArgument = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var typeOption = new Option<string>("--type")
        {
            Description = "Field type: Text, Checkbox, Choice, Signature",
            DefaultValueFactory = _ => "Text",
        };
        var nameOption = new Option<string>("--name")
        {
            Description = "Full field name",
            Required = true,
        };
        var pageOption = new Option<int>("--page")
        {
            Description = "1-based page number",
            DefaultValueFactory = _ => 1,
        };
        var rectangleOption = new Option<string>("--rect")
        {
            Description = "Rect in PDF points as 'left,bottom,right,top' (bottom-left origin)",
            Required = true,
        };
        var valueOption = new Option<string?>("--value")
        {
            Description = "Default value (Text/Choice) or 'Yes'/'Off' (Checkbox)",
        };
        var optionsOption = new Option<string[]>("--option")
        {
            Description = "Choice option (repeatable). At least one required for --type Choice.",
            AllowMultipleArgumentsPerToken = false,
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();

        var command = new Command(
            "add-field",
            "Add a new AcroForm field (Text/Checkbox/Choice/Signature) to a PDF")
        {
            inputArgument,
            outputArgument,
            typeOption,
            nameOption,
            pageOption,
            rectangleOption,
            valueOption,
            optionsOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            try
            {
                var result = FormMutationHandler.AddField(new AddFieldRequest(
                    input.FullName,
                    parseResult.GetValue(outputArgument)!.FullName,
                    parseResult.GetValue(typeOption)!,
                    parseResult.GetValue(nameOption)!,
                    parseResult.GetValue(pageOption),
                    parseResult.GetValue(rectangleOption)!,
                    parseResult.GetValue(valueOption),
                    parseResult.GetValue(optionsOption) ?? [],
                    parseResult.GetValue(ignorePermissionsOption)));
                Console.WriteLine(
                    $"Added {result.FieldType} field '{result.FieldName}' to page {result.PageNumber}");
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
