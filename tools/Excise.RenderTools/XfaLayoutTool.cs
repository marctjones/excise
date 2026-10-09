using System.CommandLine;
using System.Text.Json;
using Excise.Core.Document;
using Excise.Core.Xfa;

namespace Excise.RenderTools;

partial class Program
{
    /// <summary>
    /// <c>Excise.RenderTools xfa-layout &lt;pdf-or-dir&gt; --out-dir D [--flatten] [--no-widgets]</c>
    ///
    /// <para>#1547: lay out every dynamic XFA form found, write the result, and
    /// print one JSON line per input. The written files are what the corpus
    /// verification reads with independent tools (mutool text and renders),
    /// so excise's layout is never checked by excise. <c>--flatten</c> also
    /// removes the XFA form, the static-copy path. <c>--no-widgets</c> writes the Phase 2
    /// rendition (values on the page, no generated AcroForm fields, #2028).</para>
    /// </summary>
    static Command CreateXfaLayoutCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "PDF file or directory of PDFs" };
        var outDirOption = new Option<DirectoryInfo>("--out-dir") { Description = "Where laid-out PDFs are written", Required = true };
        var flattenOption = new Option<bool>("--flatten") { Description = "Remove the XFA form from the written copy" };
        var noWidgetsOption = new Option<bool>("--no-widgets") { Description = "Do not generate AcroForm fields (#2028); values stay on the page" };

        var command = new Command("xfa-layout", "Lay out dynamic XFA forms into ordinary pages (#1547)")
        {
            inputArg,
            outDirOption,
            flattenOption,
            noWidgetsOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var outDir = parseResult.GetValue(outDirOption)!;
            var flatten = parseResult.GetValue(flattenOption);
            var noWidgets = parseResult.GetValue(noWidgetsOption);
            Directory.CreateDirectory(outDir.FullName);

            foreach (var pdf in ResolvePdfInputs(input))
                Console.WriteLine(JsonSerializer.Serialize(LayOutOne(pdf, outDir.FullName, flatten, noWidgets)));
        });

        return command;
    }

    private static Dictionary<string, object?> LayOutOne(string pdf, string outDir, bool flatten, bool noWidgets = false)
    {
        var row = new Dictionary<string, object?> { ["file"] = pdf };
        try
        {
            using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
            var kind = document.DetectXfaForm();
            row["xfa"] = kind.ToString();
            row["originalPages"] = document.PageCount;
            if (kind != PdfXfaFormKind.Dynamic)
                return row;

            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = !noWidgets });
            row["ms"] = started.ElapsedMilliseconds;
            row["status"] = result.Status.ToString();
            row["pages"] = result.PageCount;
            row["reason"] = result.FailureReason;
            row["omissions"] = result.Omissions;
            row["scripts"] = result.ScriptsNotRun;
            row["scriptsRun"] = result.ScriptsRun;
            row["scriptFailures"] = result.ScriptFailures;
            row["fieldsWrittenByScripts"] = result.FieldsWrittenByScripts;
            row["generatedFields"] = result.GeneratedFieldCount;   // #2028

            if (result.ShowsForm)
            {
                if (flatten)
                    document.RemoveXfaForm();
                var name = Path.GetFileNameWithoutExtension(pdf);
                var parent = Path.GetFileName(Path.GetDirectoryName(pdf)) ?? string.Empty;
                var output = Path.Combine(outDir, $"{parent}__{name}.pdf");
                document.Save(output);
                row["output"] = output;
            }
        }
        catch (Exception ex)
        {
            row["error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        return row;
    }
}
