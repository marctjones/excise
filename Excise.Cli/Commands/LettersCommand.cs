using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class LettersCommand
{
    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file" };
        var pageOption = new Option<int>("--page", "-p")
        {
            Description = "Page number (1-based)",
            DefaultValueFactory = _ => 1,
        };
        var limitOption = new Option<int>("--limit", "-n")
        {
            Description = "Maximum letters to show",
            DefaultValueFactory = _ => 50,
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();
        var forAccessibilityOption = CliPermissionOptions.CreateForAccessibilityOption();

        var command = new Command("letters", "Show letters with position information")
        {
            fileArgument,
            pageOption,
            limitOption,
            ignorePermissionsOption,
            forAccessibilityOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return 1;
            }

            try
            {
                var limit = parseResult.GetValue(limitOption);
                if (limit < 0)
                {
                    Console.Error.WriteLine("Invalid limit. The maximum letter count cannot be negative.");
                    return 1;
                }

                var result = LetterInspectionHandler.Execute(new LetterInspectionRequest(
                    file.FullName,
                    parseResult.GetValue(pageOption),
                    limit,
                    parseResult.GetValue(ignorePermissionsOption),
                    parseResult.GetValue(forAccessibilityOption)));
                WriteHuman(result);
                return 0;
            }
            catch (DocumentPageOutOfRangeException ex)
            {
                Console.Error.WriteLine($"Invalid page number. Document has {ex.PageCount} pages.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void WriteHuman(LetterInspectionResult result)
    {
        Console.WriteLine($"Page {result.PageNumber}: {result.TotalLetterCount} letters");
        Console.WriteLine();
        Console.WriteLine("Char  X       Y       Width   Font");
        Console.WriteLine("----  ------  ------  ------  ----");

        foreach (var letter in result.Letters)
        {
            var value = letter.Value.Length == 1 && char.IsControl(letter.Value[0])
                ? $"\\x{(int)letter.Value[0]:X2}"
                : letter.Value;
            Console.WriteLine(
                $"{value,-4}  {letter.StartX,6:F1}  {letter.StartY,6:F1}  " +
                $"{letter.Width,6:F1}  {letter.FontName}");
        }

        if (result.TotalLetterCount > result.Letters.Count)
            Console.WriteLine($"... and {result.TotalLetterCount - result.Letters.Count} more letters");
    }
}
