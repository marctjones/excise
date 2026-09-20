using System.CommandLine;

using Excise.Core.Redaction.Recovery;

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
        // #1690 — the deferred channels are OPT-IN, not gone. Without this
        // flag `covered-image` and `image-layer` are declared skipped with
        // their reason; with it they run exactly as before.
        var includeDeferredOption = new Option<bool>(RecoveryChannelTiers.OptInFlag)
        {
            Description =
                "Also run the DEFERRED channels (#1690): raster content under a mark and " +
                "orphaned/masked image originals. Off by default — they report presence, not a " +
                "value, so nothing grades them. The OCR differential has its own flag (--ocr).",
        };
        // #1707 — the caller picks the threshold instead of inheriting ours.
        var failOnOption = new Option<string>("--fail-on")
        {
            Description =
                "Exit non-zero at or above this evidence strength about the redacted VALUE: " +
                "any (default), text, constrained, present. Exit 3 = text recovered, " +
                "4 = constrained under a mark, 5 = material survives under a mark undecoded, " +
                "0 = nothing under any mark.",
        };
        var noCorroborationOption = new Option<bool>("--no-corroboration")
        {
            Description = "residue mode: report width candidates WITHOUT independent (mutool) corroboration",
        };

        var command = new Command(
            "unredact",
            "Recover or estimate TEXT a redaction leaked (audit; reports constraints, not asserted secrets). " +
            "#1690: focused on text recovery — the image channels are deferred behind " +
            RecoveryChannelTiers.OptInFlag + " and the OCR differential behind --ocr.")
        {
            fileArg, modeOption, dictOption, toleranceOption, maxOption,
            jsonOption, ocrOption, noCorroborationOption, restoreOption,
            carriersOption, verboseOption, includeDeferredOption, failOnOption,
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
                IncludeVisibleCarriers: carriers == "all" || parseResult.GetValue(verboseOption),
                IncludeDeferred: parseResult.GetValue(includeDeferredOption),
                FailOn: parseResult.GetValue(failOnOption));

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
