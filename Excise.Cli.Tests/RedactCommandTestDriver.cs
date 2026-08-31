using Excise.Cli.Commands;

namespace Excise.Cli.Tests;

/// <summary>
/// Concise test fixture adapter for the typed redaction handler. It preserves
/// diagnostic presentation in tests that assert the old command-root output
/// while keeping production workflow methods off <c>Program</c>.
/// </summary>
internal static class RedactCommandTestDriver
{
    internal static int RunRedact(
        string inputPath,
        string outputPath,
        string text,
        bool caseSensitive,
        bool allowDecrypt = false,
        bool strict = false,
        bool allowLowConfidence = false,
        string? password = null,
        bool closeWidth = false,
        bool drawBox = true,
        (double R, double G, double B)? boxColor = null,
        bool ocrImageText = false)
        => RunRedactWithNotes(
            inputPath,
            outputPath,
            text,
            caseSensitive,
            allowDecrypt,
            strict,
            allowLowConfidence,
            password,
            closeWidth,
            drawBox,
            boxColor,
            ocrImageText).Count;

    internal static (int Count, IReadOnlyList<string> CarrierNotes) RunRedactWithNotes(
        string inputPath,
        string outputPath,
        string text,
        bool caseSensitive,
        bool allowDecrypt = false,
        bool strict = false,
        bool allowLowConfidence = false,
        string? password = null,
        bool closeWidth = false,
        bool drawBox = true,
        (double R, double G, double B)? boxColor = null,
        bool ocrImageText = false,
        Action<int, int>? progress = null)
    {
        var result = RedactCommandHandler.Execute(new RedactCommandRequest(
            inputPath,
            outputPath,
            text,
            caseSensitive,
            allowDecrypt,
            strict,
            allowLowConfidence,
            password,
            closeWidth,
            drawBox,
            boxColor,
            ocrImageText),
            progress);

        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic);
        return (result.Count, result.CarrierNotes);
    }
}
