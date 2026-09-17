using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Xfa;
using FluentAvalonia.UI.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1547 — opening an XFA form shows a banner that says what excise
/// can and cannot do with it. A dynamic XFA form otherwise shows only its
/// placeholder page with no explanation.
/// </summary>
[Collection("AvaloniaTests")]
public class XfaFormNoticeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-xfa-{Guid.NewGuid():N}");

    public XfaFormNoticeTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private enum Shape { Plain, Static, Dynamic }

    private string Pdf(Shape shape)
    {
        var basePath = Path.Combine(_tempDir, $"base-{shape}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(basePath, "Please wait...");
        if (shape == Shape.Plain)
            return basePath;

        var path = Path.Combine(_tempDir, $"{shape}.pdf");
        using (var doc = PdfDocument.Open(basePath))
        {
            if (shape == Shape.Static)
                doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "name");
            else
                doc.Catalog["NeedsRendering"] = PdfBoolean.True;

            if (doc.Resolve(doc.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acroForm)
            {
                acroForm = new PdfDictionary { ["Fields"] = new PdfArray() };
                doc.Catalog["AcroForm"] = doc.AddIndirectObject(acroForm);
            }
            acroForm["XFA"] = doc.AddIndirectObject(new PdfStream(Encoding.UTF8.GetBytes(
                "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template/></xdp:xdp>")));
            doc.Save(path);
        }
        return path;
    }

    [FixedAvaloniaFact]
    public async Task DynamicXfa_ShowsWarningBannerInTheWindow()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(Pdf(Shape.Dynamic));
            Dispatcher.UIThread.RunJobs();

            vm.XfaFormKind.Should().Be(PdfXfaFormKind.Dynamic);
            vm.IsXfaNoticeOpen.Should().BeTrue();

            var banner = window.GetVisualDescendants().OfType<FAInfoBar>()
                .Single(b => b.Name == "XfaFormInfoBar");
            banner.IsOpen.Should().BeTrue("the banner must be visible, not just the view-model flag");
            banner.Severity.Should().Be(FAInfoBarSeverity.Warning);
            banner.Title.Should().Be(MainWindowViewModel.DynamicXfaNoticeTitle);
            banner.Message.Should().Contain("Adobe Acrobat Reader").And.Contain("Firefox");

            // Closable: the user's close writes back to the view model.
            banner.IsOpen = false;
            vm.IsXfaNoticeOpen.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task LaidOutDynamicXfa_ShowsTheForm_AndAnInformationalNotice()
    {
        var path = Path.Combine(_tempDir, "laid-out.pdf");
        File.WriteAllBytes(path, Excise.TestSupport.XfaTestForms.BuildPdf(
            Excise.TestSupport.XfaTestForms.PositionedTemplate()));
        var vm = MainWindowViewModelTestFactory.Create();

        await vm.LoadDocumentAsync(path);

        vm.XfaFormKind.Should().Be(PdfXfaFormKind.Dynamic);
        vm.IsXfaFormLaidOut.Should().BeTrue();
        vm.PdfCoreDocument!.HasXfaLayoutPages().Should().BeTrue(
            "the view model shows the laid-out pages, not the placeholder");
        vm.IsXfaNoticeOpen.Should().BeTrue();
        vm.XfaNoticeSeverity.Should().Be(FAInfoBarSeverity.Informational);
        vm.XfaNoticeTitle.Should().Be(MainWindowViewModel.LaidOutXfaNoticeTitle);
        vm.XfaNoticeMessage.Should().Be(MainWindowViewModel.LaidOutXfaNoticeMessage);
        vm.XfaNoticeMessage.Should().Contain("scripts don't run");
        vm.HasUnsavedDocumentChanges.Should().BeFalse("laying out the form is not an edit");
    }

    [FixedAvaloniaFact]
    public async Task StaticXfa_ShowsInformationalNotice()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(Pdf(Shape.Static));

        vm.XfaFormKind.Should().Be(PdfXfaFormKind.Static);
        vm.IsXfaNoticeOpen.Should().BeTrue();
        vm.XfaNoticeSeverity.Should().Be(FAInfoBarSeverity.Informational);
        vm.XfaNoticeTitle.Should().Be(MainWindowViewModel.StaticXfaNoticeTitle);
    }

    [FixedAvaloniaFact]
    public async Task PlainPdf_ShowsNoNotice_AndOpeningOneClearsAPreviousNotice()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(Pdf(Shape.Dynamic));
        vm.IsXfaNoticeOpen.Should().BeTrue("precondition");

        await vm.LoadDocumentAsync(Pdf(Shape.Plain));

        vm.XfaFormKind.Should().Be(PdfXfaFormKind.None);
        vm.IsXfaNoticeOpen.Should().BeFalse("the notice describes the previous document");
    }

    [FixedAvaloniaFact]
    public async Task CloseDocument_ClearsTheNotice()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(Pdf(Shape.Dynamic));
        vm.IsXfaNoticeOpen.Should().BeTrue("precondition");

        await vm.CloseDocumentCommand.Execute();

        vm.IsXfaNoticeOpen.Should().BeFalse();
        vm.XfaFormKind.Should().Be(PdfXfaFormKind.None);
    }
}
