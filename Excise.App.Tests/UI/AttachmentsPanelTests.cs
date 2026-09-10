using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1414 — attachment disclosure, save, and stripping from the GUI.
/// </summary>
/// <remarks>
/// <para>
/// Excise.Core could already read and remove embedded files, and both were
/// tested; the GUI had no surface for either. A user could not see that a
/// document carried an attachment, could not get it out, and could not take it
/// out. That matters because attachments are invisible on the page and can
/// carry the very data the page was redacted of (ZUGFeRD/Factur-X embed a full
/// XML copy of the invoice).
/// </para>
/// <para>
/// <b>The strip is verified with qpdf, not with excise.</b> "The attachment is
/// gone" is a REMOVAL claim, and this repo's rule is that a tool must not be
/// its own oracle for the property it exists to guarantee — asserting
/// <c>GetEmbeddedFiles().Count == 0</c> with the same reader that produced the
/// list proves only that excise is self-consistent. <c>qpdf
/// --list-attachments</c> is an independent implementation reading the saved
/// bytes.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class AttachmentsPanelTests : IDisposable
{
    private const string AttachmentMarker = "ZUGFERD-CANARY-9F3A2B";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-attachments-{Guid.NewGuid():N}");

    public AttachmentsPanelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    /// <summary>A PDF carrying one embedded XML file with a findable marker.</summary>
    private string PdfWithAttachment(string name = "with-attachment.pdf")
    {
        var basePath = Path.Combine(_tempDir, "base.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(basePath, "Invoice body text");

        var outPath = Path.Combine(_tempDir, name);
        using (var document = PdfDocument.Open(basePath))
        {
            document.AddEmbeddedFile(
                "invoice.xml",
                Encoding.UTF8.GetBytes($"<invoice><secret>{AttachmentMarker}</secret></invoice>"),
                mimeType: "application/xml",
                description: "Factur-X invoice data");
            document.Save(outPath);
        }
        return outPath;
    }

    private string PdfWithoutAttachment(string name = "plain.pdf")
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, "Nothing embedded here");
        return path;
    }

    /// <summary>Independent oracle: what qpdf sees in the saved file.</summary>
    private static string QpdfListAttachments(string pdfPath)
    {
        var psi = new ProcessStartInfo("qpdf", $"--list-attachments \"{pdfPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        return stdout + stderr;
    }

    private static bool QpdfAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("qpdf", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningADocumentWithAttachments_ListsThem()
    {
        var pdf = PdfWithAttachment();
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(pdf);

        vm.HasAttachments.Should().BeTrue("the fixture embeds one file");
        vm.Attachments.Should().ContainSingle();
        vm.Attachments[0].FileName.Should().Be("invoice.xml");
        vm.Attachments[0].Description.Should().Be("Factur-X invoice data");
        vm.Attachments[0].MimeType.Should().Be("application/xml");
        vm.Attachments[0].SizeInBytes.Should().BeGreaterThan(0,
            "size comes from the DECODED bytes, so a zero here means we never read the stream");
        vm.AttachmentsSummary.Should().Contain("1 attachment");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningADocumentWithoutAttachments_ListsNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithoutAttachment());

        vm.HasAttachments.Should().BeFalse();
        vm.Attachments.Should().BeEmpty();
        vm.AttachmentsSummary.Should().Contain("no attachments");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SaveAttachment_WritesTheOriginalBytesToDisk()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        var outPath = Path.Combine(_tempDir, "extracted.xml");
        var saved = await vm.SaveAttachmentAsync(vm.Attachments[0], outPath);

        saved.Should().BeTrue();
        File.Exists(outPath).Should().BeTrue();
        (await File.ReadAllTextAsync(outPath)).Should().Contain(AttachmentMarker,
            "saving must write the embedded file's real decoded content");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SaveSelectedAttachment_UsesThePickerAndWritesTheSelectedRow()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        var outPath = Path.Combine(_tempDir, "picked.xml");
        string? suggested = null;
        vm.PickAttachmentSavePathOverride = name =>
        {
            suggested = name;
            return Task.FromResult<string?>(outPath);
        };
        vm.SelectedAttachment = vm.Attachments[0];

        await vm.SaveSelectedAttachmentAsync();

        suggested.Should().Be("invoice.xml", "the picker must suggest the attachment's own name");
        (await File.ReadAllTextAsync(outPath)).Should().Contain(AttachmentMarker);
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SaveSelectedAttachment_PickerCancelled_WritesNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        vm.PickAttachmentSavePathOverride = _ => Task.FromResult<string?>(null);
        vm.SelectedAttachment = vm.Attachments[0];

        await vm.SaveSelectedAttachmentAsync();

        Directory.GetFiles(_tempDir, "*.xml").Should().BeEmpty(
            "a cancelled picker must not write anything anywhere");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task StripAttachments_MarksTheDocumentDirty_ButDoesNotTouchTheOriginalFile()
    {
        var pdf = PdfWithAttachment();
        var originalBytes = await File.ReadAllBytesAsync(pdf);

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(pdf);

        vm.StripAllAttachments();

        vm.HasAttachments.Should().BeFalse("the in-memory document no longer carries them");
        vm.HasUnsavedDocumentChanges.Should().BeTrue(
            "stripping is a pending edit, so the save routing and the close guard apply (#1233)");
        (await File.ReadAllBytesAsync(pdf)).Should().Equal(originalBytes,
            "stripping must NOT rewrite the source file — destroying data in place to protect it " +
            "is the failure mode this guards against");
    }

    /// <summary>
    /// The removal claim, checked by a tool that is not excise.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task StripThenSave_RemovesTheAttachment_VerifiedByQpdf()
    {
        Assert.SkipWhen(!QpdfAvailable(), "qpdf is not installed [requires: tool:qpdf]");

        var pdf = PdfWithAttachment();
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(pdf);

        // Establish the oracle can SEE the attachment before we remove it,
        // otherwise "qpdf lists nothing" afterwards proves nothing at all.
        var before = QpdfListAttachments(pdf);
        before.Should().Contain("invoice.xml",
            "the independent oracle must detect the attachment BEFORE the strip, " +
            "or its silence afterwards is meaningless");

        vm.StripAllAttachments();

        var strippedPath = Path.Combine(_tempDir, "stripped.pdf");
        await vm.SaveFileAsAsync(strippedPath);

        var after = QpdfListAttachments(strippedPath);
        after.Should().NotContain("invoice.xml",
            "qpdf — not excise — must confirm the embedded file is gone from the saved bytes");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task AttachmentsCommand_OpensTheSurface_AndRefreshesTheList()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        var opened = 0;
        vm.ShowAttachmentsDialogOverride = () => { opened++; return Task.CompletedTask; };

        await vm.AttachmentsCommand.Execute();

        opened.Should().Be(1, "the menu command must actually reach the attachments surface");
        vm.Attachments.Should().ContainSingle("the command re-reads the document rather than trusting stale state");

        window.Close();
    }

    /// <summary>
    /// The dialog is a real window bound to real data — not just a ViewModel
    /// with no view behind it (the exact failure this milestone is about).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task AttachmentsDialog_ShowsARowPerAttachment()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        var dialog = new AttachmentsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();

        var list = global::Avalonia.Controls.NameScopeExtensions
            .Find<global::Avalonia.Controls.ListBox>(dialog, "AttachmentsList");
        list.Should().NotBeNull("the dialog must actually contain the attachments list");
        list!.ItemCount.Should().Be(1, "the bound list must render a row for the embedded file");

        dialog.Close();
    }
}
