using System.CommandLine;
using System.Text.Json.Serialization;
using Excise.Core.Document;

namespace Excise.Cli.Commands;

internal static class AuditCommand
{
    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file to audit" };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit machine-readable JSON instead of the human-readable report",
            DefaultValueFactory = _ => false,
        };
        var deepOption = new Option<bool>("--deep")
        {
            Description =
                "Also run differential OCR: render the page twice (with and " +
                "without overlays stripped), OCR both, and report words " +
                "recoverable from the underlying image but hidden in the " +
                "displayed render. Catches rasterized-leak cases the " +
                "structural detector can't see. Requires `tesseract` on PATH.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "audit",
            "Detect text hidden behind opaque overlays (black-box redaction audit)")
        {
            fileArgument,
            jsonOption,
            deepOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return 1;
            }

            try
            {
                var result = AuditInspectionHandler.Execute(new AuditInspectionRequest(
                    file.FullName,
                    Password: null,
                    Deep: parseResult.GetValue(deepOption)));

                if (parseResult.GetValue(jsonOption))
                    WriteJson(result);
                else
                    WriteHuman(result);
                return result.TotalHitCount == 0 ? 0 : 2;
            }
            catch (DeepAuditUnavailableException)
            {
                Console.Error.WriteLine(
                    "--deep requires tesseract on PATH. Install with `apt install tesseract-ocr`.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void WriteHuman(AuditInspectionResult result)
    {
        if (result.TotalHitCount == 0)
        {
            Console.WriteLine(result.DeepRun
                ? "✓ No hidden text detected (structural + differential OCR clean)."
                : "✓ No hidden text detected.");
            return;
        }

        if (result.StructuralHits.Count > 0)
        {
            Console.WriteLine($"✗ {result.StructuralHits.Count} structural hidden-text leak(s):");
            foreach (var hit in result.StructuralHits)
            {
                Console.WriteLine(
                    $"  Page {hit.PageNumber} at ({hit.BoundingBox.Left:F1}, {hit.BoundingBox.Bottom:F1}): " +
                    $"\"{hit.Text}\" covered by {hit.HiddenBy}");
            }
        }

        if (result.DifferentialOcrHits.Count > 0)
        {
            Console.WriteLine(
                $"✗ {result.DifferentialOcrHits.Count} differential-OCR leak(s) " +
                "(text in raster, hidden by overlay):");
            foreach (var hit in result.DifferentialOcrHits)
            {
                Console.WriteLine(
                    $"  Page {hit.PageNumber} at ({hit.BoundingBox.Left:F1}, {hit.BoundingBox.Bottom:F1}) " +
                    $"[conf {hit.Confidence:F2}]: \"{hit.Text}\"");
            }
        }
    }

    private static void WriteJson(AuditInspectionResult result)
    {
        var report = new AuditJsonReport(
            Structural: result.StructuralHits.Select(hit => new StructuralHitJson(
                Page: hit.PageNumber,
                Text: hit.Text,
                BoundingBox: Box(hit.BoundingBox),
                HiddenBy: hit.HiddenBy)).ToArray(),
            DifferentialOcr: result.DifferentialOcrHits.Select(hit => new DifferentialHitJson(
                Page: hit.PageNumber,
                Text: hit.Text,
                BoundingBox: Box(hit.BoundingBox),
                Confidence: hit.Confidence)).ToArray());
        Console.WriteLine(CliJson.Serialize(report));
    }

    private static double[] Box(PdfRectangle rectangle)
        => [rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top];

    private sealed record AuditJsonReport(
        IReadOnlyList<StructuralHitJson> Structural,
        [property: JsonPropertyName("differential_ocr")]
        IReadOnlyList<DifferentialHitJson> DifferentialOcr);

    private sealed record StructuralHitJson(
        int Page,
        string Text,
        [property: JsonPropertyName("bbox")] double[] BoundingBox,
        [property: JsonPropertyName("hidden_by")] string HiddenBy);

    private sealed record DifferentialHitJson(
        int Page,
        string Text,
        [property: JsonPropertyName("bbox")] double[] BoundingBox,
        float Confidence);
}
