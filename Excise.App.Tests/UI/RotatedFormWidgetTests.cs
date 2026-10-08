using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1986: AcroForm widget editors on rotated pages. A text field and a checkbox whose
/// appearances are flat colours, so a MuPDF render says where each widget is displayed.
///
/// <para>Checked against tools that are not excise:</para>
/// <list type="bullet">
///   <item>Each editor control, measured in the visual tree, covers the widget region
///   MuPDF renders.</item>
///   <item>A real click at the centre of MuPDF's widget region reaches that field: the
///   text box takes focus, the checkbox toggles.</item>
///   <item>After Save As and reopen, qpdf reads <c>/V</c> (and <c>/AS</c>) in the field
///   tree, and MuPDF draws the typed value inside the widget.</item>
///   <item>Editing again on the reopened file: Undo and Redo of a checkbox toggle, then
///   a second save, read back by qpdf.</item>
/// </list>
/// <para>The widgets have no <c>/MK /R</c>, so they turn with the page: the value is
/// drawn along the widget's own axis, which on a quarter-turned page runs down the
/// screen. That is the specified result for these widgets, not a defect.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class RotatedFormWidgetTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "excise-1986-" + Guid.NewGuid().ToString("N"));
    private const int Dpi = 144;
    private const string Typed = "ROTVALUE";

    public RotatedFormWidgetTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly Dictionary<string, RotationScenario> Scenarios = new[]
    {
        new RotationScenario("f-r0", "probe-form-r0", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("f-r90", "probe-form-r90", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("f-r0-ui90-z150", "probe-form-r0", 1, RotationView.SinglePage, 1.5, 1),
        new RotationScenario("f-r0-ui180", "probe-form-r0", 2, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("f-r0-ui270-dpr2", "probe-form-r0", 3, RotationView.SinglePage, 1.0, 2),
        new RotationScenario("f-r90-cont", "probe-form-r90", 0, RotationView.Continuous, 1.0, 1),
        new RotationScenario("f-r0-ui270-cont", "probe-form-r0", 3, RotationView.Continuous, 1.0, 1),
    }.ToDictionary(s => s.Id);

    public static IEnumerable<object[]> Ids() => Scenarios.Keys.Select(k => new object[] { k });

    [FixedAvaloniaTheory(Timeout = 120000)]
    [MemberData(nameof(Ids))]
    public async Task WidgetEditors_AlignWithRenderedWidgets_AndSavedValuesMatchTheFieldTree(string scenarioId)
    {
        Assert.SkipWhen(!MutoolStextGeometry.IsAvailable || !MutoolReferenceRenderer.IsAvailable,
            "mutool is not installed; it is the widget-region and value oracle.");
        Assert.SkipWhen(!QpdfReferenceTool.IsAvailable, "qpdf is not installed; it reads the saved field tree.");

        var s = Scenarios[scenarioId];
        var source = System.IO.Path.Combine(_tempDir, "source.pdf");
        await File.WriteAllBytesAsync(source, RotationFixtures.TryLoad(s.Fixture, out _)!);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 1000 });
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        if (s.Dpr != 1) viewer.RenderScalingOverride = s.Dpr;
        await vm.LoadDocumentAsync(source);
        vm.ViewMode = s.View == RotationView.SinglePage ? PdfViewMode.SinglePage : PdfViewMode.Continuous;
        for (int i = 0; i < s.UiQuarterTurns; i++) await vm.RotatePageRightCommand.Execute();
        if (s.Zoom > 0) vm.ZoomLevel = s.Zoom;
        _out.WriteLine($"{s.Id}: final rotation {s.FinalRotation}, view {s.View}");

        // Where MuPDF draws the two widgets on the page as it now stands.
        var (textRegion, checkRegion, pageW, pageH) = WidgetRegions(vm.PdfCoreDocument!.SaveToBytes());
        _out.WriteLine($"MuPDF: text widget {textRegion}, checkbox {checkRegion}, page {pageW}x{pageH}");

        // 1) The editors cover the rendered widgets.
        var (page, textBox, checkBox) = await EditorsAsync(window, viewer, s.View, pageW / pageH);
        AssertCovers(textBox, page, textRegion, pageW, pageH, "text field editor");
        AssertCovers(checkBox, page, checkRegion, pageW, pageH, "checkbox editor");

        // 2) A click at MuPDF's widget centre reaches the field; type, commit, tick.
        Point W(VisualRegion r) => page.TranslatePoint(new Point(
            r.CenterX / pageW * page.Bounds.Width, r.CenterY / pageH * page.Bounds.Height), window)!.Value;
        Reveal(window, textBox);
        Click(window, W(textRegion));
        textBox.IsFocused.Should().BeTrue("a click at the rendered text widget must focus its editor");
        window.KeyTextInput(Typed);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Reveal(window, checkBox);
        Click(window, W(checkRegion));
        await WaitAsync(() => vm.PdfCoreDocument!.GetAcroForm()!.FindField("agree")!.Value == "Yes",
            "the click at the rendered checkbox to tick it");
        vm.PdfCoreDocument!.GetAcroForm()!.FindField("name")!.Value.Should().Be(Typed);

        // Tab from the text field reaches the other field.
        Reveal(window, textBox);
        Click(window, W(textRegion));
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        checkBox.IsFocused.Should().BeTrue("Tab from the text field moves focus to the checkbox");

        // 3) Save As, reopen: field tree (qpdf) and value appearance (MuPDF).
        var first = System.IO.Path.Combine(_tempDir, "first.pdf");
        await vm.SaveFileAsAsync(first);
        AssertFieldTree(first, Typed, "Yes");
        AssertValueDrawnInWidget(first, textRegion);
        PopplerGeometry.Info(first, 1).Rotation.Should().Be(s.FinalRotation);
        await vm.LoadDocumentAsync(first);
        vm.ViewMode = s.View == RotationView.SinglePage ? PdfViewMode.SinglePage : PdfViewMode.Continuous;

        // 4) Edit again on the reopened file: untick, Undo, Redo, save.
        (page, textBox, checkBox) = await EditorsAsync(window, viewer, s.View, pageW / pageH);
        AssertCovers(checkBox, page, checkRegion, pageW, pageH, "checkbox editor after reopening");
        textBox.Text.Should().Be(Typed, "the reopened editor shows the saved value");
        Reveal(window, checkBox);
        Click(window, W(checkRegion));
        string? Agree() => vm.PdfCoreDocument!.GetAcroForm()!.FindField("agree")!.Value;
        await WaitAsync(() => Agree() == "Off", "the click to untick the checkbox");
        await vm.UndoCommand.Execute();
        Agree().Should().Be("Yes", "Undo restores the tick");
        await vm.RedoCommand.Execute();
        Agree().Should().Be("Off", "Redo removes it again");
        var second = System.IO.Path.Combine(_tempDir, "second.pdf");
        await vm.SaveFileAsAsync(second);
        AssertFieldTree(second, Typed, "Off");
    }

    private (VisualRegion Text, VisualRegion Check, double W, double H) WidgetRegions(byte[] pdf)
    {
        var path = System.IO.Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var stext = MutoolStextGeometry.Read(path, 1);
            using var bmp = MutoolReferenceRenderer.RenderPage(path, 1, Dpi)!;
            return (ColourRegion(bmp, RotationProbes.TextWidgetFill, stext.Width),
                ColourRegion(bmp, RotationProbes.CheckWidgetFill, stext.Width), stext.Width, stext.Height);
        }
        finally { File.Delete(path); }
    }

    private static VisualRegion ColourRegion(SKBitmap bmp, (byte R, byte G, byte B) c, double pageWidthPt)
    {
        double scale = bmp.Width / pageWidthPt;
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.Red - c.R) + Math.Abs(p.Green - c.G) + Math.Abs(p.Blue - c.B) > 12) continue;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); b = Math.Max(b, y + 1);
            }
        if (l == int.MaxValue) throw new InvalidOperationException($"MuPDF drew no pixel of colour {c}");
        return new VisualRegion(l / scale, t / scale, r / scale, b / scale);
    }

    private async Task<(Visual Page, TextBox Text, CheckBox Check)> EditorsAsync(
        Window window, PdfViewerControl viewer, RotationView view, double aspect)
    {
        Visual? page = null; TextBox? text = null; CheckBox? check = null;
        await WaitAsync(() =>
        {
            window.UpdateLayout();
            Visual? host;
            if (view == RotationView.SinglePage)
            {
                host = viewer.SinglePagePart.FormFieldsLayer;
                page = viewer.SinglePagePart.PdfImage;
            }
            else
            {
                var container = viewer.ContinuousPart.ContinuousItems?.GetRealizedContainers()
                    .FirstOrDefault(c => (c.DataContext as PdfPageSlot)?.PageNumber == 1);
                host = container;
                page = container?.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            }
            text = host?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            check = host?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            return !viewer.IsLoading && page is { Bounds.Height: > 1 } && text != null && check != null &&
                   Math.Abs(page.Bounds.Width / page.Bounds.Height - aspect) < 0.01;
        }, "the page and its two field editors at MuPDF's displayed aspect");
        return (page!, text!, check!);
    }

    private void AssertCovers(Control editor, Visual page, VisualRegion widget, double pageW, double pageH, string what)
    {
        var tl = editor.TranslatePoint(new Point(0, 0), page)!.Value;
        var br = editor.TranslatePoint(new Point(editor.Bounds.Width, editor.Bounds.Height), page)!.Value;
        double sx = pageW / page.Bounds.Width, sy = pageH / page.Bounds.Height;
        var drawn = new VisualRegion(tl.X * sx, tl.Y * sy, br.X * sx, br.Y * sy);
        _out.WriteLine($"{what}: editor {drawn}, MuPDF widget {widget}");
        drawn.Left.Should().BeApproximately(widget.Left, 2.5, $"{what} {drawn} vs rendered widget {widget}");
        drawn.Top.Should().BeApproximately(widget.Top, 2.5, $"{what} {drawn} vs rendered widget {widget}");
        drawn.Right.Should().BeApproximately(widget.Right, 2.5, $"{what} {drawn} vs rendered widget {widget}");
        drawn.Bottom.Should().BeApproximately(widget.Bottom, 2.5, $"{what} {drawn} vs rendered widget {widget}");
    }

    /// <summary>qpdf's reading of the saved field tree: the text field's /V and the checkbox's /V and /AS.</summary>
    private void AssertFieldTree(string path, string text, string state)
    {
        var json = MutoolStextGeometry.RunTool(MutoolStextGeometry.FindOnPath("qpdf")!,
            new[] { "--json=1", "--json-key=objects", path });
        using var doc = JsonDocument.Parse(json);
        string? Str(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var dicts = doc.RootElement.GetProperty("objects").EnumerateObject()
            .Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object).ToList();
        var name = dicts.Single(d => Str(d, "/T") is "name" or "u:name");
        var agree = dicts.Single(d => Str(d, "/T") is "agree" or "u:agree");
        _out.WriteLine($"qpdf {System.IO.Path.GetFileName(path)}: name /V={Str(name, "/V")} agree /V={Str(agree, "/V")} /AS={Str(agree, "/AS")}");
        var v = Str(name, "/V") ?? "";
        (v.StartsWith("u:", StringComparison.Ordinal) ? v[2..] : v).Should().Be(text, "qpdf reads the typed value in the field tree");
        Str(agree, "/V").Should().Be("/" + state, "qpdf reads the checkbox value in the field tree");
        Str(agree, "/AS").Should().Be("/" + state, "the widget's appearance state matches the field value");
    }

    private void AssertValueDrawnInWidget(string path, VisualRegion widget)
    {
        var stext = MutoolStextGeometry.Read(path, 1);
        var hits = stext.Find(Typed);
        hits.Should().NotBeEmpty("MuPDF draws the saved value");
        var dir = stext.DirectionOf(Typed);
        _out.WriteLine($"MuPDF value {hits[0]} dir ({dir.X},{dir.Y}) in widget {widget}");
        hits[0].FractionCoveredBy(widget.Inflate(2)).Should().BeGreaterThan(0.9,
            $"the value {hits[0]} is drawn inside the widget {widget}");
    }

    /// <summary>Scroll the field into the viewport first: at zoom 1 a quarter-turned page is wider than the window.</summary>
    private static void Reveal(Window window, Control editor)
    {
        editor.BringIntoView();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, Point p)
    {
        new Rect(window.ClientSize).Contains(p).Should().BeTrue($"the click {p} must be inside the window {window.ClientSize}");
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitAsync(Func<bool> ready, string what, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }
}
