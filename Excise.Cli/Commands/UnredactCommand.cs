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
        var jsonOption = new Option<bool>("--json") { Description = "Machine-readable JSON output" };
        var ocrOption = new Option<bool>("--ocr")
        {
            Description = "certain mode: also run the OCR differential (scanned redactions). Needs tesseract.",
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
            jsonOption, ocrOption, noCorroborationOption,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var dictionary = parseResult.GetValue(dictOption);
            var input = new UnredactCommandInput(
                file.FullName,
                parseResult.GetValue(modeOption) ?? "certain",
                dictionary?.FullName,
                parseResult.GetValue(toleranceOption),
                parseResult.GetValue(maxOption),
                parseResult.GetValue(ocrOption),
                parseResult.GetValue(noCorroborationOption));

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
