using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class RedactCommand
{
    /// <summary>
    /// Build the <c>excise redact</c> adapter. It owns argument validation,
    /// console presentation, and exit-code translation; the handler owns the
    /// mutation workflow.
    /// </summary>
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArgument = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var textArgument = new Argument<string>("text") { Description = "Text to remove (all occurrences)" };
        var caseSensitiveOption = new Option<bool>("--case-sensitive")
        {
            Description = "Match case exactly (default: case-insensitive)",
            DefaultValueFactory = _ => false,
        };
        var closeWidthOption = new Option<bool>("--close-width")
        {
            Description = "Close the width gap so the removed text's width can't be recovered (moves surviving text)",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs. The output is re-encrypted with this " +
                "password by default (see --allow-decrypt).",
        };
        var allowDecryptOption = new Option<bool>("--allow-decrypt")
        {
            Description = "Write UNENCRYPTED output from an encrypted source. By default (#643) an " +
                "encrypted source is re-encrypted with the same algorithm and permissions (RC4 " +
                "sources are upgraded to AES-256) and the same password it was opened with; this " +
                "flag is the explicit opt-out that drops the protection instead.",
            DefaultValueFactory = _ => false,
        };
        var strictOption = new Option<bool>("--strict")
        {
            Description = "Require an independent extraction-confidence check (mutool or tesseract) " +
                "to run at all — fail rather than proceed unverified when neither is on PATH. " +
                "Mirrors `audit --deep`'s posture.",
            DefaultValueFactory = _ => false,
        };
        var allowLowConfidenceOption = new Option<bool>("--allow-low-confidence")
        {
            Description = "Proceed even when the extraction-confidence check (#650) finds excise's own " +
                "text extraction disagrees sharply with an independent oracle on this document — the " +
                "same signature as a real redaction leak. Without this flag, that case fails closed.",
            DefaultValueFactory = _ => false,
        };
        var noBoxOption = new Option<bool>("--no-box")
        {
            Description = "Remove the text but draw NO covering rectangle (glyph removal is unchanged).",
            DefaultValueFactory = _ => false,
        };
        var boxColorOption = new Option<string?>("--box-color")
        {
            Description = "Covering-box fill color: 'black' (default), 'white', or 'R,G,B' with each " +
                "component 0-255. Ignored with --no-box.",
        };
        var ocrImageTextOption = new Option<bool>("--ocr-image-text")
        {
            Description = "OCR every page first so text baked into images can be found and redacted. Requires tesseract; writes a temporary invisible OCR layer before structural redaction.",
            DefaultValueFactory = _ => false,
        };
        var flattenOcrOption = new Option<bool>("--flatten-ocr")
        {
            Description = "Create a fresh image-only PDF: rasterize every page, OCR the requested visible term, black out its pixels, and discard all original PDF carriers. Requires tesseract; intentionally removes selectable text, forms, links, and metadata.",
            DefaultValueFactory = _ => false,
        };
        var progressOption = new Option<bool>("--progress")
        {
            Description = "Write page-based overall completion to stderr (0% through 100%).",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "redact",
            "Remove text from a PDF (glyph-level removal; text extraction will not find it)")
        {
            inputArgument,
            outputArgument,
            textArgument,
            caseSensitiveOption,
            closeWidthOption,
            passwordOption,
            allowDecryptOption,
            strictOption,
            allowLowConfidenceOption,
            noBoxOption,
            boxColorOption,
            ocrImageTextOption,
            flattenOcrOption,
            progressOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputArgument)!;
            var text = parseResult.GetValue(textArgument)!;
            var noBox = parseResult.GetValue(noBoxOption);
            var boxColorSpec = parseResult.GetValue(boxColorOption);
            var flattenOcr = parseResult.GetValue(flattenOcrOption);
            var ocrImageText = parseResult.GetValue(ocrImageTextOption);
            var closeWidth = parseResult.GetValue(closeWidthOption);
            var strict = parseResult.GetValue(strictOption);
            var allowLowConfidence = parseResult.GetValue(allowLowConfidenceOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            if (string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("Redaction text must not be empty.");
                return 1;
            }

            if (noBox && boxColorSpec != null)
            {
                Console.Error.WriteLine(
                    "--no-box and --box-color are mutually exclusive: --no-box draws no box to colour.");
                return 1;
            }

            if (flattenOcr &&
                (ocrImageText || noBox || boxColorSpec != null || closeWidth || strict || allowLowConfidence))
            {
                Console.Error.WriteLine(
                    "--flatten-ocr cannot be combined with structural-redaction box, width, confidence, or OCR-layer options.");
                return 1;
            }

            (double R, double G, double B)? boxColor = null;
            if (boxColorSpec != null && !TryParseBoxColor(boxColorSpec, out boxColor, out var colorError))
            {
                Console.Error.WriteLine($"Invalid --box-color '{boxColorSpec}': {colorError}");
                return 1;
            }

            try
            {
                var progressReporter = parseResult.GetValue(progressOption)
                    ? new PageProgressReporter(Console.Error)
                    : null;
                Action<int, int>? progress = progressReporter == null
                    ? null
                    : progressReporter.Report;
                var result = RedactCommandHandler.Execute(new RedactCommandRequest(
                    input.FullName,
                    output.FullName,
                    text,
                    parseResult.GetValue(caseSensitiveOption),
                    parseResult.GetValue(allowDecryptOption),
                    strict,
                    allowLowConfidence,
                    parseResult.GetValue(passwordOption),
                    closeWidth,
                    DrawBox: !noBox,
                    boxColor,
                    ocrImageText,
                    flattenOcr),
                    progress);

                foreach (var diagnostic in result.Diagnostics)
                    Console.Error.WriteLine(diagnostic);

                if (result.Flattened)
                    Console.WriteLine($"Flattened and redacted {result.Count} OCR occurrence(s) of '{result.Text}'");
                else
                    Console.WriteLine($"Redacted {result.Count} occurrence(s) of '{result.Text}'");

                foreach (var note in result.CarrierNotes)
                    Console.WriteLine($"  note: {note}");
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

    /// <summary>Parse a box color into PDF <c>rg</c> components (0..1).</summary>
    internal static bool TryParseBoxColor(
        string spec,
        out (double R, double G, double B)? color,
        out string? error)
    {
        color = null;
        error = null;
        var trimmed = (spec ?? "").Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "black":
                color = (0.0, 0.0, 0.0);
                return true;
            case "white":
                color = (1.0, 1.0, 1.0);
                return true;
        }

        var parts = trimmed.Split(',');
        if (parts.Length != 3)
        {
            error = "expected 'black', 'white', or three comma-separated components 'R,G,B' (0-255)";
            return false;
        }

        var channels = new double[3];
        for (var index = 0; index < channels.Length; index++)
        {
            if (!int.TryParse(parts[index].Trim(), out var value) || value is < 0 or > 255)
            {
                error = "each of R,G,B must be an integer 0-255";
                return false;
            }

            channels[index] = value / 255.0;
        }

        color = (channels[0], channels[1], channels[2]);
        return true;
    }

    /// <summary>
    /// Stable, line-oriented progress for non-interactive callers. Stderr keeps
    /// successful-result stdout usable in pipelines.
    /// </summary>
    private sealed class PageProgressReporter(TextWriter writer)
    {
        private int _lastPercent = -1;

        internal void Report(int completed, int total)
        {
            var percent = total <= 0
                ? 100
                : Math.Clamp((int)Math.Floor(100.0 * completed / total), 0, 100);
            if (percent == _lastPercent)
                return;

            _lastPercent = percent;
            writer.WriteLine($"Progress: {percent}% ({completed}/{total} pages)");
        }
    }
}
