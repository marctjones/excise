using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2015: Delete, save (a save that keeps the undo history), Undo must leave the widget in the form's field tree, not only on the page.
/// The save cuts every reference to a deleted annotation (#2012), including its /Fields entry; the
/// undo of the delete used to put back only the page's /Annots. Judged by qpdf's JSON dump, not by
/// excise's own parser.
/// </summary>
[Collection("AvaloniaTests")]
public class DeleteWidgetUndoKeepsFieldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-delundo-{Guid.NewGuid():N}");
    public DeleteWidgetUndoKeepsFieldTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string QpdfJson(string pdf)
    {
        var psi = new ProcessStartInfo("qpdf", $"--json=2 \"{pdf}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qpdf did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>Names (/T) of the objects the AcroForm /Fields array lists, and of the page's /Annots.</summary>
    private static (string[] Fields, string[] Annots) FieldTreeOf(string pdf)
    {
        using var json = JsonDocument.Parse(QpdfJson(pdf));
        var objects = json.RootElement.GetProperty("qpdf")[1];
        JsonElement Resolve(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s && s.EndsWith(" 0 R", StringComparison.Ordinal))
                return objects.GetProperty("obj:" + s).GetProperty("value");
            return e;
        }
        string NameOf(JsonElement e) => Resolve(e).TryGetProperty("/T", out var t) ? t.GetString()!.Replace("u:", "") : "?";

        var root = Resolve(objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        var fields = root.TryGetProperty("/AcroForm", out var acro) && Resolve(acro).TryGetProperty("/Fields", out var f)
            ? Resolve(f).EnumerateArray().Select(NameOf).ToArray()
            : [];
        var page = Resolve(Resolve(Resolve(root.GetProperty("/Pages")).GetProperty("/Kids")).EnumerateArray().First());
        var annots = page.TryGetProperty("/Annots", out var a) ? Resolve(a).EnumerateArray().Select(NameOf).ToArray() : [];
        return (fields, annots);
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DeleteWidget_Save_Undo_KeepsTheFieldInTheFieldTree()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        var path = Path.Combine(_dir, "form.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
        {
            doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "keepfield");
            doc.AddTextField(1, new PdfRectangle(72, 650, 300, 670), "deletedfield");
            doc.Save(path);
        }

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(path);
        var widget = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.RawDictionary.GetOptional("T") is PdfString { Value: "deletedfield" });

        await vm.DeleteAnnotationAsync(1, widget);
        // A user Save clears the undo history (#782); print, Reduce File Size and the form export save the
        // live document and keep it, which is how a save can sit between a delete and its undo.
        var deleted = Path.Combine(_dir, "deleted.pdf");
        File.WriteAllBytes(deleted, vm.PdfCoreDocument!.SaveToBytes());
        FieldTreeOf(deleted).Fields.Should().Equal("keepfield");

        await vm.UndoCommand.Execute();
        var restored = Path.Combine(_dir, "restored.pdf");
        await vm.SaveFileAsAsync(restored);

        var (fields, annots) = FieldTreeOf(restored);
        annots.Should().Equal("keepfield", "deletedfield");
        fields.Should().Equal(["keepfield", "deletedfield"], "undo of a delete after a save must put the field back in /Fields, in its place");
    }
}
