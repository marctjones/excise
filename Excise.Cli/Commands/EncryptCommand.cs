using System.CommandLine;
using Excise.Core.Security;

namespace Excise.Cli.Commands;

internal static class EncryptCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input")
        {
            Description = "Input PDF file (must not already be encrypted)",
        };
        var outputArgument = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var userPasswordOption = new Option<string?>("--user-password")
        {
            Description = "User (open) password. Omit for no password required to open the file.",
        };
        var ownerPasswordOption = new Option<string?>("--owner-password")
        {
            Description = "Owner (permissions) password. Omit for no owner password.",
        };
        var permissionsOption = new Option<long>("--permissions")
        {
            Description = "Raw /P permission bitmask (ISO 32000-2 Table 22). Default -4 grants every " +
                "permission bit — excise stores this value correctly but does not yet enforce permissions " +
                "on read (#642); this is a plumbing-only escape hatch, not a security control yet.",
            DefaultValueFactory = _ => -4L,
        };
        var algorithmOption = new Option<string>("--algorithm")
        {
            Description = "Encryption algorithm: 'aes256' (V=5 R=6, PDF 2.0 native, default) or " +
                "'aes128' (V=4 R=4, for readers that don't support PDF 2.0 encryption).",
            DefaultValueFactory = _ => "aes256",
        };
        var noEncryptMetadataOption = new Option<bool>("--no-encrypt-metadata")
        {
            Description = "Leave the XMP /Metadata stream unencrypted while encrypting everything else. " +
                "Default: metadata is encrypted too.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "encrypt",
            "Write a password-protected copy of a PDF (AES-256 R=6 by default; AES-128 R=4 with --algorithm aes128)")
        {
            inputArgument,
            outputArgument,
            userPasswordOption,
            ownerPasswordOption,
            permissionsOption,
            algorithmOption,
            noEncryptMetadataOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputArgument)!;
            var userPassword = parseResult.GetValue(userPasswordOption);
            var ownerPassword = parseResult.GetValue(ownerPasswordOption);
            var algorithmText = parseResult.GetValue(algorithmOption)!;

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            if (string.IsNullOrEmpty(userPassword) && string.IsNullOrEmpty(ownerPassword))
            {
                Console.Error.WriteLine(
                    "At least one of --user-password or --owner-password is required " +
                    "(otherwise there is nothing to protect).");
                return 1;
            }

            if (!TryParseAlgorithm(algorithmText, out var algorithm))
            {
                Console.Error.WriteLine($"Unknown --algorithm '{algorithmText}'. Use 'aes256' or 'aes128'.");
                return 1;
            }

            try
            {
                var result = EncryptionCommandHandler.Encrypt(new EncryptCommandRequest(
                    input.FullName,
                    output.FullName,
                    userPassword,
                    ownerPassword,
                    parseResult.GetValue(permissionsOption),
                    algorithm,
                    EncryptMetadata: !parseResult.GetValue(noEncryptMetadataOption)));

                var revision = result.Algorithm == PdfEncryptionAlgorithm.Aes256
                    ? "V=5 R=6"
                    : "V=4 R=4";
                Console.WriteLine($"Encrypted with {algorithmText} ({revision}).");
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

    internal static bool TryParseAlgorithm(string value, out PdfEncryptionAlgorithm algorithm)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "aes256":
                algorithm = PdfEncryptionAlgorithm.Aes256;
                return true;
            case "aes128":
                algorithm = PdfEncryptionAlgorithm.Aes128;
                return true;
            default:
                algorithm = default;
                return false;
        }
    }
}
