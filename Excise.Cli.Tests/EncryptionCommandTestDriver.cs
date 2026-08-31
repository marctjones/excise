using Excise.Cli.Commands;
using Excise.Core.Security;

namespace Excise.Cli.Tests;

internal static class EncryptionCommandTestDriver
{
    internal static void RunEncrypt(
        string inputPath,
        string outputPath,
        string? userPassword,
        string? ownerPassword,
        long permissions,
        PdfEncryptionAlgorithm algorithm,
        bool encryptMetadata)
        => EncryptionCommandHandler.Encrypt(new EncryptCommandRequest(
            inputPath,
            outputPath,
            userPassword,
            ownerPassword,
            permissions,
            algorithm,
            encryptMetadata));

    internal static void RunDecrypt(
        string inputPath,
        string outputPath,
        string? password)
        => EncryptionCommandHandler.Decrypt(new DecryptCommandRequest(
            inputPath,
            outputPath,
            password));
}
