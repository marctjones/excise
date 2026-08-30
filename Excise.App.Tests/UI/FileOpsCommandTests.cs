using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Excise.Core.Document;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #816 batch 2: execute the actual file-ops toolbar/menu COMMANDS
/// (SaveFileCommand, SaveAsCommand, SaveFlattenedFormCopyCommand,
/// OpenFileCommand, LoadRecentFileCommand, ExportCurrentPageCommand,
/// ExportPagesCommand, PrintCommand) and assert the real effect — not the
/// underlying async method with an already-known path/target, which is all
/// the existing suite (EncryptedDocumentSaveWarningTests, FormWorkflowTests,
/// DocumentPermissionEnforcementTests, ...) exercises. A command mis-wired to
/// the wrong handler, or wired to nothing, would pass every one of those
/// tests and only show up here.
///
/// Open/SaveAs/SaveFlattenedFormCopy/ExportCurrentPage/ExportPages all read
/// their target path/folder from a native file dialog via
/// <c>GetStorageProvider()</c>, which resolves the classic-desktop-lifetime
/// MainWindow's <see cref="IStorageProvider"/>. The headless test host has no
/// desktop lifetime (Avalonia.Headless <c>SetupWithoutStarting</c>), so that
/// path always returned null and none of these commands could previously be
/// driven end to end — the only reachable seam was the underlying method.
/// <see cref="MainWindowViewModel.StorageProviderOverride"/> is a new test
/// seam (#816) that <c>GetStorageProvider()</c> now prefers when set;
/// <c>CreateStorageProviderStub</c> below (a Moq mock — see its own doc comment
/// for why) answers picker calls with real temp-dir paths.
/// </summary>
[Collection("AvaloniaTests")]
public class FileOpsCommandTests
{
    // ── OpenFileCommand ──────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task OpenFileCommand_Execute_StubbedDialog_LoadsDocument()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 3);

        var vm = MainWindowViewModelTestFactory.Create();
        vm.StorageProviderOverride = CreateStorageProviderStub(openFiles: new[] { sourcePath });

        vm.IsDocumentLoaded.Should().BeFalse();
        await vm.OpenFileCommand.Execute();

        vm.IsDocumentLoaded.Should().BeTrue("the Open command must actually load the file the dialog returned");
        vm.TotalPages.Should().Be(3);

        Cleanup(tempDir);
    }

    // ── LoadRecentFileCommand ────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task LoadRecentFileCommand_Execute_LoadsTheGivenRecentPath()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 2);

        var vm = MainWindowViewModelTestFactory.Create();

        await vm.LoadRecentFileCommand.Execute(sourcePath);

        vm.IsDocumentLoaded.Should().BeTrue();
        vm.TotalPages.Should().Be(2);

        Cleanup(tempDir);
    }

    // ── SaveAsCommand ────────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task SaveAsCommand_Execute_StubbedDialog_PersistsChangeToNewPath()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);
        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await vm.RotatePageRightCommand.Execute();

        vm.StorageProviderOverride = CreateStorageProviderStub(saveFile: outputPath);

        await vm.SaveAsCommand.Execute();

        vm.DocumentName.Should().Be(Path.GetFileName(outputPath),
            "Save As must switch the current document to the dialog's chosen path");
        vm.FileState.HasUnsavedChanges.Should().BeFalse();
        File.Exists(outputPath).Should().BeTrue("the Save As command must actually write the file, not just close the dialog");

        using var reopened = PdfDocument.Open(outputPath);
        reopened.GetPage(1).Rotation.Should().Be((before + 90) % 360,
            "the rotation made before Save As must be in the bytes written to disk");

        Cleanup(tempDir);
    }

    // ── SaveFileCommand ──────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task SaveFileCommand_Execute_OnNonOriginalFile_PersistsChangeDirectly()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);

        // Escape the "original file" branch of SaveFileCommand (which redirects
        // to Save As / the redaction workflow) the same way a real user would:
        // Save As once first.
        vm.StorageProviderOverride = CreateStorageProviderStub(saveFile: outputPath);
        await vm.SaveAsCommand.Execute();
        vm.FileState.IsOriginalFile.Should().BeFalse("Save As must make the current path distinct from the original");

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePageRightCommand.Execute();
        vm.FileState.HasUnsavedChanges.Should().BeTrue();

        await vm.SaveFileCommand.Execute();

        vm.FileState.HasUnsavedChanges.Should().BeFalse("SaveFileCommand must clear dirty state on a real save");
        using var reopened = PdfDocument.Open(outputPath);
        reopened.GetPage(1).Rotation.Should().Be((before + 90) % 360,
            "SaveFileCommand must write the post-Save-As rotation to the SAME path, proving it executed a real save " +
            "rather than being a no-op or misrouted to Save As again");

        Cleanup(tempDir);
    }

    // ── SaveFlattenedFormCopyCommand ─────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task SaveFlattenedFormCopyCommand_Execute_StubbedDialog_BakesValueAndDropsAcroForm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ExciseFileOpsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "form.pdf");
        var outputPath = Path.Combine(tempDir, "form_flattened.pdf");
        File.WriteAllBytes(sourcePath, BuildFormPdf());

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);

        var field = vm.PdfCoreDocument!.GetAcroForm()!.FindField("Name")!;
        field.SetValue("Dana");
        vm.OnFormFieldEdited("Name", "Dana");

        vm.StorageProviderOverride = CreateStorageProviderStub(saveFile: outputPath);

        await vm.SaveFlattenedFormCopyCommand.Execute();

        File.Exists(outputPath).Should().BeTrue("the command must actually write the flattened copy the dialog targeted");
        using var reopened = PdfDocument.Open(outputPath);
        reopened.GetAcroForm().Should().BeNull("a flattened copy must not retain interactive form fields");
        Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes())
            .Should().Contain("(Dana) Tj", "the filled value must be baked into the page content stream");
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty();

        Cleanup(tempDir);
    }

    // ── ExportCurrentPageCommand ─────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task ExportCurrentPageCommand_Execute_StubbedDialog_WritesNonEmptyPng()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Export me");
        var outputPath = Path.Combine(tempDir, "page1.png");

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);

        vm.StorageProviderOverride = CreateStorageProviderStub(saveFile: outputPath);

        await vm.ExportCurrentPageCommand.Execute();

        File.Exists(outputPath).Should().BeTrue("the Export Current Page command must write to the dialog's chosen path");
        new FileInfo(outputPath).Length.Should().BeGreaterThan(0);

        Cleanup(tempDir);
    }

    // ── ExportPagesCommand ───────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task ExportPagesCommand_Execute_StubbedDialog_WritesOnePngPerPage()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 3);
        var exportDir = Path.Combine(tempDir, "exported");
        Directory.CreateDirectory(exportDir);

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);

        vm.StorageProviderOverride = CreateStorageProviderStub(folder: exportDir);

        await vm.ExportPagesCommand.Execute();

        for (int i = 1; i <= 3; i++)
        {
            var expected = Path.Combine(exportDir, $"page_{i:D3}.png");
            File.Exists(expected).Should().BeTrue($"page {i} must be exported to the dialog's chosen folder");
            new FileInfo(expected).Length.Should().BeGreaterThan(0);
        }

        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task CloseDocumentCommand_CanBeRepeatedAfterResettingWorkspaceState()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Close me");
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(sourcePath);
        vm.SearchText = "Close";
        vm.IsSearchVisible = true;

        await vm.CloseDocumentCommand.Execute();

        vm.IsDocumentLoaded.Should().BeFalse();
        vm.PdfCoreDocument.Should().BeNull();
        vm.SearchText.Should().BeEmpty();
        vm.IsSearchVisible.Should().BeFalse();
        vm.CurrentPageIndex.Should().Be(0);

        var secondClose = async () => await vm.CloseDocumentCommand.Execute();
        await secondClose.Should().NotThrowAsync();

        Cleanup(tempDir);
    }

    // ── PrintCommand ─────────────────────────────────────────────────────
    // #621: excise deliberately does not print. The command's real effect is
    // showing that explanation via IUserDialogService — verify it actually
    // does that (as opposed to silently no-op'ing) rather than asserting
    // print output that was never meant to exist.
    [FixedAvaloniaFact]
    public async Task PrintCommand_Execute_DocumentLoaded_ShowsPrintNotSupportedMessage()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Print me");

        var dialog = new RecordingUserDialogService();
        var vm = CreateViewModelWithDialogSpy(dialog);
        await vm.LoadDocumentAsync(sourcePath);

        await vm.PrintCommand.Execute();

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].title.Should().Be("Print");
        dialog.Messages[0].message.Should().Contain("doesn't print directly",
            "the command must surface the real, deliberate #621 explanation, not a generic/blank message");

        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task PrintCommand_Execute_NoDocumentLoaded_ShowsOpenPdfFirstMessage()
    {
        var dialog = new RecordingUserDialogService();
        var vm = CreateViewModelWithDialogSpy(dialog);

        await vm.PrintCommand.Execute();

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].message.Should().Contain("Open a PDF before printing");

        await Task.CompletedTask;
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a Moq-backed <see cref="IStorageProvider"/> that answers picker
    /// calls with a stubbed <see cref="IStorageFile"/>/<see cref="IStorageFolder"/>
    /// whose <c>Path</c> resolves to a real temp-dir path, so the command under
    /// test round-trips through the exact same <c>IStorageFile.Path.LocalPath</c>
    /// extraction the production code uses. Mocked rather than hand-implemented
    /// (and using a mocked file/folder rather than Avalonia's own internal
    /// <c>BclStorageFile</c>/<c>BclStorageFolder</c>) because Avalonia marks
    /// these storage interfaces <c>[NotClientImplementable]</c> — a Roslyn
    /// analyzer rejects a source-level "class X : IStorageProvider", and the
    /// concrete BCL wrappers are internal to Avalonia.Base. Moq's
    /// runtime-generated proxy is unaffected by the analyzer since no such
    /// class appears in source. Only the members
    /// OpenFileCommand/SaveAsCommand/SaveFlattenedFormCopyCommand/
    /// ExportCurrentPageCommand/ExportPagesCommand actually call are stubbed.
    /// </summary>
    private static IStorageProvider CreateStorageProviderStub(
        string[]? openFiles = null, string? saveFile = null, string? folder = null)
    {
        var mock = new Mock<IStorageProvider>();

        IReadOnlyList<IStorageFile> files = (openFiles ?? Array.Empty<string>())
            .Select(MockStorageFile)
            .ToList();
        mock.Setup(p => p.OpenFilePickerAsync(It.IsAny<FilePickerOpenOptions>()))
            .ReturnsAsync(files);

        IStorageFile? saveResult = saveFile is null ? null : MockStorageFile(saveFile);
        mock.Setup(p => p.SaveFilePickerAsync(It.IsAny<FilePickerSaveOptions>()))
            .ReturnsAsync(saveResult);

        IReadOnlyList<IStorageFolder> folders = folder is null
            ? Array.Empty<IStorageFolder>()
            : new List<IStorageFolder> { MockStorageFolder(folder) };
        mock.Setup(p => p.OpenFolderPickerAsync(It.IsAny<FolderPickerOpenOptions>()))
            .ReturnsAsync(folders);

        return mock.Object;
    }

    private static IStorageFile MockStorageFile(string path)
    {
        var mock = new Mock<IStorageFile>();
        mock.Setup(f => f.Path).Returns(new Uri(new FileInfo(path).FullName));
        mock.Setup(f => f.Name).Returns(Path.GetFileName(path));
        return mock.Object;
    }

    private static IStorageFolder MockStorageFolder(string path)
    {
        var mock = new Mock<IStorageFolder>();
        mock.Setup(f => f.Path).Returns(new Uri(new DirectoryInfo(path).FullName));
        mock.Setup(f => f.Name).Returns(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        return mock.Object;
    }

    private sealed class RecordingUserDialogService : IUserDialogService
    {
        public List<(string title, string message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    }

    private static MainWindowViewModel CreateViewModelWithDialogSpy(IUserDialogService dialog)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return MainWindowViewModelTestFactory.Create(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────

    /// <summary>Minimal hand-built single-field AcroForm PDF (mirrors FormWorkflowTests' fixture).</summary>
    private static byte[] BuildFormPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Alice) /Rect [72 700 300 720] /P 3 0 R >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ExciseFileOpsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (Path.Combine(tempDir, "source.pdf"), Path.Combine(tempDir, "output.pdf"), tempDir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
