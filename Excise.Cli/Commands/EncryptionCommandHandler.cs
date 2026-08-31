using Excise.Core.Parsing;
using Excise.Core.Security;
using Excise.Core.Writing;

namespace Excise.Cli.Commands;

/// <summary>
/// Owns whole-document encryption mutations independently of command parsing
/// and console presentation.
/// </summary>
internal static class EncryptionCommandHandler
{
    internal static EncryptCommandResult Encrypt(
        EncryptCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        if (string.IsNullOrEmpty(request.UserPassword) && string.IsNullOrEmpty(request.OwnerPassword))
        {
            throw new ArgumentException(
                "At least one user or owner password is required; otherwise there is nothing to protect.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);
        var outputPath = Path.GetFullPath(request.OutputPath);

        const string alreadyEncrypted =
            "Source PDF is already encrypted. To change its password, run `excise decrypt` " +
            "first, then `excise encrypt` the result with the new password(s).";

        Excise.Core.Document.PdfDocument document;
        try
        {
            document = PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath);
        }
        catch (PdfEncryptionNotSupportedException)
        {
            // A password-protected source can fail to open before IsEncrypted
            // is observable. Give the same unambiguous change-password route.
            throw new InvalidOperationException(alreadyEncrypted);
        }

        using (document)
        {
            if (document.IsEncrypted)
                throw new InvalidOperationException(alreadyEncrypted);

            cancellationToken.ThrowIfCancellationRequested();
            var options = new PdfEncryptionOptions
            {
                UserPassword = request.UserPassword,
                OwnerPassword = request.OwnerPassword,
                Permissions = request.Permissions,
                Algorithm = request.Algorithm,
                EncryptMetadata = request.EncryptMetadata,
            };

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            new PdfDocumentWriter(document, options).Write(stream);
        }

        return new EncryptCommandResult(
            input.FullName,
            outputPath,
            request.Algorithm,
            request.EncryptMetadata);
    }

    internal static DecryptCommandResult Decrypt(
        DecryptCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);
        var outputPath = Path.GetFullPath(request.OutputPath);

        using var document = PdfDocumentLifetime.OpenInputForOutput(
            input.FullName,
            outputPath,
            request.Password);
        if (!document.IsEncrypted)
            throw new InvalidOperationException("Source PDF is not encrypted; nothing to decrypt.");

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        new PdfDocumentWriter(document, encryptionOptions: null).Write(stream);
        return new DecryptCommandResult(input.FullName, outputPath);
    }
}

internal readonly record struct EncryptCommandRequest(
    string InputPath,
    string OutputPath,
    string? UserPassword,
    string? OwnerPassword,
    long Permissions,
    PdfEncryptionAlgorithm Algorithm,
    bool EncryptMetadata);

internal sealed record EncryptCommandResult(
    string InputPath,
    string OutputPath,
    PdfEncryptionAlgorithm Algorithm,
    bool EncryptMetadata);

internal readonly record struct DecryptCommandRequest(
    string InputPath,
    string OutputPath,
    string? Password);

internal sealed record DecryptCommandResult(
    string InputPath,
    string OutputPath);
