using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Where the fillable-field overlays sit against the field /Rect they stand for, in the
/// default (continuous) view. The expected box is worked out here by hand from the file's
/// /Rect and the page panel the overlay lives in, not through <c>PdfCoordinateMapper</c>,
/// so a mapping defect cannot agree with itself.
/// </summary>
[Collection("AvaloniaTests")]
public class FormOverlayFitTests
{
    private const string Irs1040 = "test-pdfs/smoke/irs-1040.pdf";
    private const double SizeTolerance = 2.5;      // dips: measured drift is under 1% of the field width
    private const double PositionTolerance = 2.5;  // dips: the page panel sits about a dip inside its border

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task EveryOverlayOnPage1_SitsOnItsFieldRect()
    {
        var path = TestRepoLayout.FindFile(Irs1040);
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("smoke corpus", Irs1040));

        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        // Ground truth is mutool's widget bounds (top-left origin, points), not excise's parse.
        var expectedFields = MutoolWidgetBounds(path!);
        var pageW = 612.0;
        expectedFields.Should().NotBeEmpty("irs-1040 has fillable fields on page 1");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path!);

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        List<Control> inputs = new();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            window.UpdateLayout();
            inputs = viewer.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("continuous-form-field") && c.Bounds.Height > 0).ToList();
            if (inputs.Count >= 10) break;
            await Task.Delay(200);
        }
        inputs.Should().NotBeEmpty("the default view shows overlays for the fields on page 1");

        var rows = new List<string> { "field\texpX\texpY\texpW\texpH\tgotX\tgotY\tgotW\tgotH\tdX\tdY\tdW\tdH" };
        var off = 0; var inflated = 0; var matched = 0;
        foreach (var input in inputs)
        {
            var tip = ToolTip.GetTip(input) as string ?? "";
            var hit = expectedFields.Where(f => tip.Contains(f.Name, StringComparison.Ordinal))
                .OrderByDescending(f => f.Name.Length).FirstOrDefault();
            if (hit.Name == null) continue;
            var panel = input.GetVisualAncestors().OfType<Panel>().First();
            var u = panel.Bounds.Width / pageW;                      // dips per point on this page
            var exp = new Rect(hit.Rect.X * u, hit.Rect.Y * u, hit.Rect.Width * u, hit.Rect.Height * u);
            var got = new Rect(Canvas.GetLeft(input), Canvas.GetTop(input), input.Bounds.Width, input.Bounds.Height);
            matched++;
            var bad = Math.Abs(got.X - exp.X) > PositionTolerance || Math.Abs(got.Y - exp.Y) > PositionTolerance
                   || Math.Abs(got.Width - exp.Width) > SizeTolerance || Math.Abs(got.Height - exp.Height) > SizeTolerance;
            if (bad) off++;
            if (exp.Width < 12 || exp.Height < 12) inflated++;
            rows.Add(string.Join('\t', hit.Name, F(exp.X), F(exp.Y), F(exp.Width), F(exp.Height),
                F(got.X), F(got.Y), F(got.Width), F(got.Height),
                F(got.X - exp.X), F(got.Y - exp.Y), F(got.Width - exp.Width), F(got.Height - exp.Height)));
        }
        var outPath = Environment.GetEnvironmentVariable("EXCISE_FIT_OUT") ?? Path.Combine(Path.GetTempPath(), "overlay-fit.tsv");
        File.WriteAllLines(outPath, rows);
        Console.WriteLine($"FIT matched={matched} of {expectedFields.Count} fields, off={off}, tiny={inflated}, table={outPath}");

        matched.Should().BeGreaterThan(expectedFields.Count / 2, "most fields must be found to measure fit");
        off.Should().Be(0, $"every overlay must sit on its field rect (position {PositionTolerance}, size {SizeTolerance} dips); see {outPath}");
    }

    private static List<(string Name, Rect Rect)> MutoolWidgetBounds(string pdf)
    {
        var js = Path.Combine(Path.GetTempPath(), $"excise-widgets-{Guid.NewGuid():N}.js");
        File.WriteAllText(js, "var d=Document.openDocument(" + System.Text.Json.JsonSerializer.Serialize(pdf) +
            ");var p=d.loadPage(0);var w=p.getWidgets();for(var i=0;i<w.length;i++){var b=w[i].getBounds();" +
            "print(w[i].getName()+'\\t'+b[0]+'\\t'+b[1]+'\\t'+b[2]+'\\t'+b[3]);}");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("mutool", $"run \"{js}\"")
            { RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r').Split('\t')).Where(c => c.Length == 5)
                .Select(c => (c[0], new Rect(double.Parse(c[1], ci), double.Parse(c[2], ci),
                    double.Parse(c[3], ci) - double.Parse(c[1], ci), double.Parse(c[4], ci) - double.Parse(c[2], ci))))
                .ToList();
        }
        finally { try { File.Delete(js); } catch (IOException) { } }
    }

    private static string F(double v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
