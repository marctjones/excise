using Excise.Core.Document;

namespace Excise.Cli.Commands;

/// <summary>
/// Owns the CLI's input-document lifetime choice for workflows that may write
/// back to the same path. Distinct outputs stream the input; same-path outputs
/// detach from the source file before save so Windows does not retain a read
/// handle over the destination.
/// </summary>
internal static class PdfDocumentLifetime
{
    internal static PdfDocument OpenInputForOutput(
        string inputPath,
        string outputPath,
        string? userPassword = null)
    {
        if (PathsReferToSameFile(inputPath, outputPath))
        {
            var bytes = File.ReadAllBytes(inputPath);
            return userPassword is null
                ? PdfDocument.Open(bytes)
                : PdfDocument.Open(bytes, userPassword);
        }

        return userPassword is null
            ? PdfDocument.Open(inputPath)
            : PdfDocument.Open(inputPath, userPassword);
    }

    internal static bool PathsReferToSameFile(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
