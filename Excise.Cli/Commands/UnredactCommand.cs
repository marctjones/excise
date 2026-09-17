using System.CommandLine;

namespace Excise.Cli.Commands;

/// <summary>
/// Command-line composition for the delivery-neutral recovery engines used by
/// <c>excise unredact</c>.
/// </summary>
internal static class UnredactCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF suspected of a weak redaction" };
        var modeOption = new Option<string>("--mode")
        {
            Description = "certain (recover text actually present) | residue (recover from the width leak) | both",
            DefaultValueFactory = _ => "certain",
        };
        var dictOption = new Option<FileInfo?>("--dictionary")
        {
            Description = "Wordlist (one candidate per line) for residue mode",
        };
        var toleranceOption = new Option<double>("--tolerance")
        {
            Description = "Width-fit tolerance in points (residue mode)",
            DefaultValueFactory = _ => 0.5,
        };
        var maxOption = new Option<int>("--max-candidates")
        {
            Description = "Cap candidates per gap (residue mode)",
            DefaultValueFactory = _ => 200,
        };
        var restoreOption = new Option<FileInfo?>("--restore")
        {
            Description =
                "Write a rebuilt PDF with recovered material drawn back in place " +
                "(green = certain, amber = candidate). ⚠️ The output CONTAINS the " +
                "recovered text by design. Refuses to overwrite the input.",
        };
        var jsonOption = new Option<bool>("--json") { Description = "Machine-readable JSON output" };
        var ocrOption = new Option<bool>("--ocr")
        {
            Description = "certain mode: also run the OCR differential (scanned redactions). Needs tesseract.",
        };
        var carriersOption = new Option<string>("--carriers")
        {
            Description = "certain mode: hidden (default: only carrier text a reader cannot already see) | " +
                          "all (also list carriers that restate visible text, e.g. a filled field's value or the title)",
            DefaultValueFactory = _ => "hidden",
        };
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Same as --carriers all",
        };
        var noCorroborationOption = new Option<bool>("--no-corroboration")
        {
            Description = "residue mode: report width candidates WITHOUT independent (mutool) corroboration",
        };

        var command = new Command(
            "unredact",
            "Recover or estimate text a redaction leaked (audit; reports constraints, not asserted secrets)")
        {
            fileArg, modeOption, dictOption, toleranceOption, maxOption,
            jsonOption, ocrOption, noCorroborationOption, restoreOption,
            carriersOption, verboseOption,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var carriers = (parseResult.GetValue(carriersOption) ?? "hidden").ToLowerInvariant();
            if (carriers is not ("hidden" or "all"))
            {
                Console.Error.WriteLine("--carriers must be hidden or all");
                return Task.FromResult(2);
            }
            var dictionary = parseResult.GetValue(dictOption);
            var input = new UnredactCommandInput(
                file.FullName,
                parseResult.GetValue(modeOption) ?? "certain",
                dictionary?.FullName,
                parseResult.GetValue(toleranceOption),
                parseResult.GetValue(maxOption),
                parseResult.GetValue(ocrOption),
                parseResult.GetValue(noCorroborationOption),
                parseResult.GetValue(restoreOption)?.FullName,
                IncludeVisibleCarriers: carriers == "all" || parseResult.GetValue(verboseOption));

            var outcome = UnredactCommandHandler.Execute(input, cancellationToken);
            UnredactCommandOutput.Write(
                outcome,
                parseResult.GetValue(jsonOption),
                Console.Out,
                Console.Error);
            return Task.FromResult(outcome.ExitCode);
        });

        return command;
    }
}
