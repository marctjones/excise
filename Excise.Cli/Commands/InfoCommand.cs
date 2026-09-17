using System.CommandLine;
using Excise.Core.Automation;

using Excise.Cli;

namespace Excise.Cli.Commands;

internal static class InfoCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file")
        {
            Description = "PDF file to analyze",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write document information as JSON",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };
        var command = new Command("info", "Show PDF document information")
        {
            fileArgument,
            jsonOption,
            passwordOption,
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
                var result = InfoCommandHandler.Execute(new DocumentInfoRequest(
                    file.FullName,
                    parseResult.GetValue(passwordOption)));
                if (parseResult.GetValue(jsonOption))
                    WriteJson(result);
                else
                    WriteHuman(result);
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

    private static void WriteJson(DocumentInfoResult result)
    {
        var report = new DocumentInfoJsonReport(
            SchemaVersion: 1,
            Command: PdfCommandIds.DocumentInfo,
            Status: "PASS",
            File: result.FilePath,
            SizeBytes: result.SizeBytes,
            Version: result.Version,
            PageCount: result.PageCount,
            Encrypted: result.Encrypted,
            XfaForm: result.XfaForm,
            Metadata: result.Metadata,
            Pages: result.Pages);
        Console.WriteLine(CliJson.Serialize(report, CliJsonContext.Default.DocumentInfoJsonReport));
    }

    private static void WriteHuman(DocumentInfoResult result)
    {
        Console.WriteLine($"File: {result.FileName}");
        Console.WriteLine($"Size: {result.SizeBytes:N0} bytes");
        Console.WriteLine();
        Console.WriteLine("=== Document Info ===");
        Console.WriteLine($"PDF Version: {result.Version}");
        Console.WriteLine($"Page Count: {result.PageCount}");
        Console.WriteLine($"Encrypted: {result.Encrypted}");
        // #1547: excise does not render XFA; say so where a user looks first.
        Console.WriteLine(result.XfaForm switch
        {
            "dynamic" => "XFA Form: dynamic (excise cannot display it; use Adobe Acrobat Reader or Firefox)",
            "static" => "XFA Form: static (excise fills the standard AcroForm fields, not the XFA data)",
            _ => "XFA Form: none",
        });
        Console.WriteLine();

        // #1205: every one of these is document-authored text printed to a
        // terminal, which honours bidi controls. A /Title carrying U+202E makes
        // the printed metadata read as something other than what the file holds.
        // Escaped only HERE, in the human display -- the --json path stays
        // byte-faithful (System.Text.Json escapes non-ASCII to \uXXXX already).
        static string Safe(string v) => Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(v);
        if (result.Metadata.Title != null) Console.WriteLine($"Title: {Safe(result.Metadata.Title)}");
        if (result.Metadata.Author != null) Console.WriteLine($"Author: {Safe(result.Metadata.Author)}");
        if (result.Metadata.Subject != null) Console.WriteLine($"Subject: {Safe(result.Metadata.Subject)}");
        if (result.Metadata.Creator != null) Console.WriteLine($"Creator: {Safe(result.Metadata.Creator)}");
        if (result.Metadata.Producer != null) Console.WriteLine($"Producer: {Safe(result.Metadata.Producer)}");

        Console.WriteLine();
        Console.WriteLine("=== Pages ===");
        foreach (var page in result.Pages)
        {
            Console.WriteLine(
                $"  Page {page.PageNumber}: {page.Width:F0} x {page.Height:F0} pts " +
                $"({page.Width / 72:F1}\" x {page.Height / 72:F1}\")");
        }
        if (result.PageCount > result.Pages.Count)
            Console.WriteLine($"  ... and {result.PageCount - result.Pages.Count} more pages");
    }

    internal sealed record DocumentInfoJsonReport(
        int SchemaVersion,
        string Command,
        string Status,
        string File,
        long SizeBytes,
        string Version,
        int PageCount,
        bool Encrypted,
        string XfaForm,
        DocumentMetadataInfo Metadata,
        IReadOnlyList<DocumentPageInfo> Pages);
}
