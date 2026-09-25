using Excise.Core.Document;
using Excise.Core.Writing;
using Excise.Rendering;

namespace Excise.Cli.Commands;

/// <summary>
/// <c>excise optimize</c> (#1550): write a smaller copy of a PDF, independently
/// of CLI parsing so it can be tested without System.CommandLine.
/// </summary>
internal static class OptimizeCommandHandler
{
    internal static OptimizeCommandResult Execute(
        OptimizeCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);

        var outputPath = Path.GetFullPath(request.OutputPath);
        // Reduce File Size always writes a copy: the lossy presets are not
        // reversible, and the original is the only way back.
        if (PdfDocumentLifetime.PathsReferToSameFile(input.FullName, outputPath))
            throw new InvalidOperationException(
                "The output path is the input file. Reduce File Size always writes a new file; choose another path.");

        var diagnostics = new List<string>();
        byte[] saved;
        Excise.Core.Security.PdfEncryptionOptions? reEncryption;
        using (var document = request.Password is null
                   ? Excise.Core.Document.PdfDocument.Open(input.FullName)
                   : Excise.Core.Document.PdfDocument.Open(input.FullName, request.Password))
        {
            reEncryption = request.AllowDecrypt ? null : document.GetReEncryptionOptions(request.Password);
            if (document.IsEncrypted && request.AllowDecrypt)
            {
                diagnostics.Add(
                    "Warning: --allow-decrypt was passed — output will NOT be encrypted, even though " +
                    "the source was. Anyone with the file can read it without a password.");
            }
            else if (reEncryption != null)
            {
                diagnostics.Add(
                    "Note: source is encrypted; output is re-encrypted with the same permissions and " +
                    "the same password (#643). Encrypted files cannot use object streams, so they " +
                    "shrink less.");
            }

            // The optimizer works on what an ordinary save writes, never on the
            // source file's own object table.
            saved = document.SaveToBytes();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = PdfOptimizationOptions.ForPreset(request.Preset, PdfImageCodecs.CreateJpegCodec());
        var result = PdfDocumentOptimizer.SaveOptimizedCopy(
            saved, outputPath, options, reEncryption, cancellationToken);
        diagnostics.AddRange(result.Warnings.Select(w => "Warning: " + w));

        return new OptimizeCommandResult(input.FullName, outputPath, input.Length, result, diagnostics);
    }
}

internal readonly record struct OptimizeCommandRequest(
    string InputPath,
    string OutputPath,
    PdfOptimizationPreset Preset,
    string? Password = null,
    bool AllowDecrypt = false);

internal sealed record OptimizeCommandResult(
    string InputPath,
    string OutputPath,
    long InputSizeBytes,
    PdfOptimizationResult Optimization,
    IReadOnlyList<string> Diagnostics)
{
    internal long OutputSizeBytes => Optimization.OutputSizeBytes;

    /// <summary>Output size as a fraction of the input size.</summary>
    internal double Ratio => InputSizeBytes == 0 ? 1.0 : (double)OutputSizeBytes / InputSizeBytes;
}
