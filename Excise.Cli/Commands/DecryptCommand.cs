using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class DecryptCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input")
        {
            Description = "Input PDF file (must be encrypted)",
        };
        var outputArgument = new Argument<FileInfo>("output")
        {
            Description = "Output PDF path (will NOT be password-protected)",
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "Password to open the source PDF (tried as the USER/open password; an " +
                "owner-only password is not yet supported for opening, see #324). Omit for an empty password.",
        };

        var command = new Command(
            "decrypt",
            "Write an unprotected copy of a password-protected PDF")
        {
            inputArgument,
            outputArgument,
            passwordOption,
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
                var result = EncryptionCommandHandler.Decrypt(new DecryptCommandRequest(
                    input.FullName,
                    parseResult.GetValue(outputArgument)!.FullName,
                    parseResult.GetValue(passwordOption)));
                Console.WriteLine($"Decrypted. Output: {result.OutputPath}");
                Console.WriteLine("Warning: the output file is NOT password-protected.");
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
