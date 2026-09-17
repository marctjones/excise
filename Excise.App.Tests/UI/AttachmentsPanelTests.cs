using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1414 / #1563 — the Attachments pane: disclosure, save, stripping.
/// </summary>
/// <remarks>
/// <para>
/// Attachments are invisible on the page and can carry the very data the page
/// was redacted of (ZUGFeRD/Factur-X embed a full XML copy of the invoice).
/// #1563 moved them from a dialog into a sidebar pane that is visible by
/// default. The fixture carries BOTH carriers: a document-level
/// <c>/Names/EmbeddedFiles</c> entry and a page-level <c>/FileAttachment</c>
/// annotation, which reach the reader by different routes.
/// </para>
/// <para>
/// <b>The strip is verified with qpdf, not with excise.</b> "The attachment is
/// gone" is a REMOVAL claim, and this repo's rule is that a tool must not be
/// its own oracle for the property it exists to guarantee.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class AttachmentsPanelTests : IDisposable
{
    private const string AttachmentMarker = "ZUGFERD-CANARY-9F3A2B";
    private const string AnnotationFileName = "scan-source.bin";
    private const string AnnotationModDate = "D:20240102030405+00'00'";

    /// <summary>Every byte value, so "exact bytes" cannot pass on a text-mode round trip.</summary>
    private static readonly byte[] AnnotationPayload = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();

    private static readonly byte[] DocumentPayload =
        Encoding.UTF8.GetBytes($"<invoice><secret>{AttachmentMarker}</secret></invoice>");

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-attachments-{Guid.NewGuid():N}");

    public AttachmentsPanelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    /// <summary>A PDF carrying one document-level embedded XML file.</summary>
    private string PdfWithAttachment(string name = "with-attachment.pdf")
    {
        var basePath = Path.Combine(_tempDir, "base-" + name);
        TestPdfGenerator.CreateSimpleTextPdf(basePath, "Invoice body text");

        var outPath = Path.Combine(_tempDir, name);
        using (var document = PdfDocument.Open(basePath))
        {
            AddDocumentAttachment(document);
            document.Save(outPath);
        }
        return outPath;
    }

    /// <summary>
    /// Two pages: a document-level attachment AND a <c>/FileAttachment</c>
    /// annotation on page 2 carrying its own file specification.
    /// </summary>
    private string PdfWithBothCarriers(string name = "both-carriers.pdf", bool documentLevel = true)
    {
        var basePath = Path.Combine(_tempDir, "base-" + name);
        TestPdfGenerator.CreateSimpleTextPdf(basePath, pageCount: 2);

        var outPath = Path.Combine(_tempDir, name);
        using (var document = PdfDocument.Open(basePath))
        {
            if (documentLevel)
                AddDocumentAttachment(document);
            AddAnnotationAttachment(document, pageNumber: 2);
            document.Save(outPath);
        }
        return outPath;
    }

    private static void AddDocumentAttachment(PdfDocument document) =>
        document.AddEmbeddedFile(
            "invoice.xml",
            DocumentPayload,
            mimeType: "application/xml",
            description: "Factur-X invoice data");

    private static void AddAnnotationAttachment(PdfDocument document, int pageNumber)
    {
        var fileDict = new PdfDictionary();
        fileDict.SetName("Type", "EmbeddedFile");
        fileDict.SetInt("Length", AnnotationPayload.Length);
        var paramsDict = new PdfDictionary();
        paramsDict.SetString("ModDate", AnnotationModDate);
        fileDict["Params"] = paramsDict;
        var streamRef = document.AddIndirectObject(new PdfStream(fileDict, AnnotationPayload));

        var ef = new PdfDictionary();
        ef["F"] = streamRef;
        var fileSpec = new PdfDictionary();
        fileSpec.SetName("Type", "Filespec");
        fileSpec.SetString("F", AnnotationFileName);
        fileSpec.SetString("UF", AnnotationFileName);
        fileSpec["EF"] = ef;

        var annot = new PdfDictionary();
        annot.SetName("Type", "Annot");
        annot.SetName("Subtype", "FileAttachment");
        annot["Rect"] = new PdfArray(new PdfReal(72), new PdfReal(600), new PdfReal(92), new PdfReal(620));
        annot["FS"] = document.AddIndirectObject(fileSpec);
        document.GetPage(pageNumber).Dictionary["Annots"] = new PdfArray(document.AddIndirectObject(annot));
    }

    private string PdfWithoutAttachment(string name = "plain.pdf")
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, "Nothing embedded here");
        return path;
    }

    private string SaveEncrypted(string plainPath, string name, long permissions)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = PdfDocument.Open(File.ReadAllBytes(plainPath));
        doc.Save(path, new PdfEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = "owner-1563",
            Permissions = permissions,
        });
        return path;
    }

    private static (MainWindowViewModel Vm, List<ToastService.ToastEventArgs> Toasts) CreateWithToasts()
    {
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        return (MainWindowViewModelTestFactory.Create(toastService: toastService), toasts);
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
        // #925/#1516: drain both redirected pipes CONCURRENTLY, or a full
        // stderr buffer deadlocks the single headless dispatcher thread.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new TimeoutException(
                "qpdf --list-attachments did not exit within 30s; killed it rather than hanging the suite (#1516).");
        }
        return stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
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

    private static async Task PumpAsync(Window window)
    {
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    // ── listing ──────────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningADocumentWithAttachments_ListsThem()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        vm.HasAttachments.Should().BeTrue("the fixture embeds one file");
        vm.Attachments.Should().ContainSingle();
        vm.Attachments[0].FileName.Should().Be("invoice.xml");
        vm.Attachments[0].Description.Should().Be("Factur-X invoice data");
        vm.Attachments[0].MimeType.Should().Be("application/xml");
        vm.Attachments[0].SizeInBytes.Should().Be(DocumentPayload.Length,
            "size comes from the DECODED bytes, so anything else means we never read the stream");
        vm.Attachments[0].PageNumber.Should().BeNull("a /Names/EmbeddedFiles entry belongs to no page");
        vm.AttachmentsSummary.Should().Contain("1 attachment");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task BothCarriers_AreListed_AndTheAnnotationOneKnowsItsPageAndDate()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());

        vm.Attachments.Should().HaveCount(2, "a document-level entry and a page annotation are two attachments");

        var documentLevel = vm.Attachments.Single(a => a.FileName == "invoice.xml");
        documentLevel.PageNumber.Should().BeNull();
        documentLevel.AutomationName.Should().Be($"invoice.xml, {DocumentPayload.Length} B");

        var onPage = vm.Attachments.Single(a => a.FileName == AnnotationFileName);
        onPage.PageNumber.Should().Be(2, "the /FileAttachment annotation sits on page 2");
        onPage.SizeInBytes.Should().Be(AnnotationPayload.Length);
        onPage.ModifiedDate.Should().Be(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
        onPage.DetailText.Should().Be("Modified 2024-01-02 · Page 2");
        onPage.AutomationName.Should().Be($"{AnnotationFileName}, 512 B, on page 2");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SelectingAnAnnotationAttachment_JumpsToItsPage()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.CurrentPageIndex.Should().Be(0);

        vm.SelectedAttachment = vm.Attachments.Single(a => a.PageNumber == 2);
        vm.CurrentPageIndex.Should().Be(1, "selecting a page attachment navigates to that page");

        vm.CurrentPageIndex = 0;
        vm.SelectedAttachment = vm.Attachments.Single(a => a.PageNumber == null);
        vm.CurrentPageIndex.Should().Be(0, "a document-level attachment has nowhere to jump to");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public void AFileNameWithABidiOverride_IsShownWithTheControlVisible()
    {
        var entry = new AttachmentEntry("k", "invoice‮fdp.exe", null, null, 10);

        entry.DisplayName.Should().NotContain("‮",
            "a right-to-left override would make an executable read as a PDF");
        entry.DisplayName.Should().Contain("U+202E");
        entry.AutomationName.Should().NotContain("‮");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningADocumentWithoutAttachments_ListsNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithoutAttachment());

        vm.HasAttachments.Should().BeFalse();
        vm.Attachments.Should().BeEmpty();
        vm.AttachmentsSummary.Should().Contain("no attachments");
        vm.AttachmentsEmptyText.Should().Be("No attachments");
    }

    // ── the pane ─────────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Pane_IsVisibleByDefault_WithARowPerAttachment_AnnouncedByNameAndSize()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(PdfWithBothCarriers());
            await PumpAsync(window);

            vm.IsAttachmentsSidebarVisible.Should().BeTrue("#1563: the pane shows without any user action");
            window.FindControl<Control>("LeftSidebarHost")!.IsVisible.Should().BeTrue();
            window.FindControl<Control>("AttachmentsPanel")!.IsEffectivelyVisible.Should().BeTrue();

            var list = window.FindControl<ListBox>("AttachmentsList")!;
            list.IsEffectivelyVisible.Should().BeTrue();
            list.ItemCount.Should().Be(2, "both carriers must render as rows");
            window.FindControl<Control>("AttachmentsEmptyText")!.IsVisible.Should().BeFalse();

            var containers = Enumerable.Range(0, list.ItemCount)
                .Select(i => list.ContainerFromIndex(i))
                .OfType<ListBoxItem>()
                .ToList();
            containers.Should().HaveCount(2, "rows must be realized to be announced");
            containers.Select(AutomationProperties.GetName).Should().BeEquivalentTo(
                vm.Attachments.Select(a => a.AutomationName),
                "a screen reader must hear each row's name and size (#1563)");

            window.FindControl<Button>("SaveAllAttachmentsButton")!.IsEffectivelyVisible.Should().BeTrue();
            window.FindControl<Button>("SaveAttachmentButton")!.IsEffectivelyEnabled.Should().BeFalse(
                "Save needs a selected row");
            vm.SelectedAttachment = vm.Attachments[0];
            await PumpAsync(window);
            window.FindControl<Button>("SaveAttachmentButton")!.IsEffectivelyEnabled.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Real mouse input on every pane control — a row, Save…, Save All…,
    /// Remove All — through the headless window, so a collapsed, covered or
    /// unbound control fails here rather than passing a Command.Execute sweep.
    /// This is also what the GUI interaction-coverage gate counts.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task PaneControls_WorkWithRealMouseClicks()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(PdfWithBothCarriers());
            await PumpAsync(window);

            var list = window.FindControl<ListBox>("AttachmentsList")!;
            var pageRowIndex = vm.Attachments.IndexOf(vm.Attachments.Single(a => a.PageNumber == 2));
            var row = (Control)list.ContainerFromIndex(pageRowIndex)!;
            await ClickAsync(window, row);
            vm.SelectedAttachment.Should().Be(vm.Attachments[pageRowIndex], "clicking a row selects it");
            vm.CurrentPageIndex.Should().Be(1, "and a page attachment's row jumps to its page");

            var savedPath = Path.Combine(_tempDir, "clicked.bin");
            vm.PickAttachmentSavePathOverride = _ => Task.FromResult<string?>(savedPath);
            await ClickAsync(window, window.FindControl<Button>("SaveAttachmentButton")!);
            await WaitForAsync(() => File.Exists(savedPath));
            (await File.ReadAllBytesAsync(savedPath)).Should().Equal(AnnotationPayload);

            var folder = Directory.CreateDirectory(Path.Combine(_tempDir, "clicked-all")).FullName;
            vm.PickFolderOverride = () => Task.FromResult<string?>(folder);
            await ClickAsync(window, window.FindControl<Button>("SaveAllAttachmentsButton")!);
            await WaitForAsync(() => Directory.GetFiles(folder).Length == 2);

            await ClickAsync(window, window.FindControl<Button>("RemoveAllAttachmentsButton")!);
            // #1572: Remove All takes the page annotation's file too.
            await WaitForAsync(() => vm.Attachments.Count == 0);
            vm.HasUnsavedDocumentChanges.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ClickAsync(Window window, Control control)
    {
        control.IsEffectivelyVisible.Should().BeTrue($"{control.Name ?? control.GetType().Name} must be visible to click");
        var center = new global::Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var inWindow = control.TranslatePoint(center, window);
        inWindow.Should().NotBeNull($"{control.Name ?? control.GetType().Name} must be in the window's visual tree");
        global::Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, inWindow!.Value, global::Avalonia.Input.MouseButton.Left);
        global::Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, inWindow.Value, global::Avalonia.Input.MouseButton.Left);
        await PumpAsync(window);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
            await KeyboardTestHelpers.FlushDispatcherAsync();
        }
        condition().Should().BeTrue("the clicked command should have completed");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Pane_StaysVisibleWhenEmpty_AndSaysSo()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await PumpAsync(window);
            var empty = window.FindControl<TextBlock>("AttachmentsEmptyText")!;
            empty.IsEffectivelyVisible.Should().BeTrue();
            empty.Text.Should().Be("No document open");

            await vm.LoadDocumentAsync(PdfWithoutAttachment());
            await PumpAsync(window);

            window.FindControl<Control>("AttachmentsPanel")!.IsEffectivelyVisible.Should().BeTrue(
                "the pane stays visible by default even when there is nothing to list (#1563)");
            empty.IsEffectivelyVisible.Should().BeTrue();
            empty.Text.Should().Be("No attachments");
            window.FindControl<ListBox>("AttachmentsList")!.IsVisible.Should().BeFalse();
            window.FindControl<Button>("SaveAllAttachmentsButton")!.IsEffectivelyVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ViewMenuToggle_HidesAndShowsThePane_AndLeavesTheOtherPanes()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await PumpAsync(window);
            var item = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Name == "ViewAttachmentsMenuItem");
            item.ToggleType.Should().Be(MenuItemToggleType.CheckBox);
            item.IsChecked.Should().BeTrue();

            item.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await PumpAsync(window);

            vm.IsAttachmentsSidebarVisible.Should().BeFalse();
            item.IsChecked.Should().BeFalse();
            window.FindControl<Control>("AttachmentsPanel")!.IsVisible.Should().BeFalse();
            window.FindControl<Control>("OutlinePanel")!.IsVisible.Should().BeTrue();
            window.FindControl<Control>("ThumbnailsPanel")!.IsVisible.Should().BeTrue();

            vm.IsOutlineSidebarVisible = false;
            vm.IsThumbnailsSidebarVisible = false;
            await PumpAsync(window);
            window.FindControl<Control>("LeftSidebarHost")!.IsVisible.Should().BeFalse(
                "with every pane hidden the sidebar collapses");

            vm.ToggleAttachmentsCommand.Execute().Subscribe();
            await PumpAsync(window);
            window.FindControl<Control>("LeftSidebarHost")!.IsVisible.Should().BeTrue(
                "the Attachments pane alone keeps the sidebar open");
            window.FindControl<Control>("AttachmentsPanel")!.IsVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task PaneVisibility_IsRestoredFromSettings_AndPersistedOnClose()
    {
        var store = new InMemorySettingsStore(new WindowSettings { AttachmentsSidebarVisible = false });
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false, settingsStore: store);
        var window = new MainWindow(store) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        await PumpAsync(window);

        vm.IsAttachmentsSidebarVisible.Should().BeFalse("a hidden pane stays hidden across sessions (#1563)");
        window.FindControl<Control>("AttachmentsPanel")!.IsVisible.Should().BeFalse();

        vm.ToggleAttachmentsSidebar();
        window.Close();
        store.Current.AttachmentsSidebarVisible.Should().BeTrue("closing the window writes the current choice");

        var reopened = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false, settingsStore: store);
        var second = new MainWindow(store) { DataContext = reopened, Width = 1200, Height = 900 };
        second.Show();
        try
        {
            await PumpAsync(second);
            reopened.IsAttachmentsSidebarVisible.Should().BeTrue();
            reopened.ToggleAttachmentsSidebar();
        }
        finally
        {
            second.Close();
        }
        store.Current.AttachmentsSidebarVisible.Should().BeFalse();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public void NewWindowSettings_ShowThePaneByDefault()
    {
        new WindowSettings().AttachmentsSidebarVisible.Should().BeTrue(
            "a window.json written before #1563 has no such field and must show the pane");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task AttachmentsCommand_RevealsAHiddenPane_RefreshesTheList_AndAsksForFocus()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(PdfWithAttachment());
            vm.IsAttachmentsSidebarVisible = false;
            vm.Attachments.Clear();

            var focusRequests = 0;
            vm.AttachmentsPaneFocusRequested += (_, _) => focusRequests++;
            var observed = 0;
            vm.ShowAttachmentsPaneOverride = () => { observed++; return Task.CompletedTask; };

            await vm.AttachmentsCommand.Execute();
            await PumpAsync(window);

            observed.Should().Be(1, "the menu command must actually run");
            vm.IsAttachmentsSidebarVisible.Should().BeTrue("Document ▸ Attachments shows the pane");
            vm.Attachments.Should().ContainSingle("the command re-reads the document rather than trusting stale state");
            focusRequests.Should().Be(1);
            var focused = window.FocusManager?.GetFocusedElement();
            window.FindControl<ListBox>("AttachmentsList")!.IsKeyboardFocusWithin.Should().BeTrue(
                $"the view moves keyboard focus into the pane (focused: {focused?.GetType().Name ?? "nothing"})");
        }
        finally
        {
            window.Close();
        }
    }

    // ── close / open lifecycle ───────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CloseDocument_ClearsTheList()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.SelectedAttachment = vm.Attachments[0];

        await vm.CloseDocumentCommand.Execute();

        vm.Attachments.Should().BeEmpty("a closed document's attachments must not stay listed");
        vm.HasAttachments.Should().BeFalse();
        vm.SelectedAttachment.Should().BeNull();
        vm.AttachmentsEmptyText.Should().Be("No document open");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task FailedOpen_ClearsThePreviousDocumentsList()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.Attachments.Should().HaveCount(2, "precondition");

        // A real file that is not a PDF: ValidateDocumentPath passes, the parse fails.
        var broken = Path.Combine(_tempDir, "broken.pdf");
        await File.WriteAllTextAsync(broken, "this is not a pdf at all");
        await vm.LoadDocumentAsync(broken);

        vm.IsDocumentLoaded.Should().BeFalse("precondition: the open failed");
        vm.Attachments.Should().BeEmpty("a failed open must not leave the previous document's attachments listed");
        vm.HasAttachments.Should().BeFalse();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningAnotherDocument_ReplacesTheList()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.Attachments.Should().HaveCount(2);

        await vm.LoadDocumentAsync(PdfWithoutAttachment());
        vm.Attachments.Should().BeEmpty();

        await vm.LoadDocumentAsync(PdfWithAttachment());
        vm.Attachments.Should().ContainSingle(a => a.FileName == "invoice.xml");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task OpeningADocumentWithAttachments_WarnsAndPointsAtThePane()
    {
        var (vm, toasts) = CreateWithToasts();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());

        toasts.Should().ContainSingle(t => t.Message == "Document has attachments")
            .Which.Details.Should().Be("2 embedded files travel with this PDF. See the Attachments pane.");
    }

    // ── save ─────────────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SaveAttachment_WritesTheExactDecodedBytes_ForBothCarriers()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());

        var documentOut = Path.Combine(_tempDir, "extracted.xml");
        (await vm.SaveAttachmentAsync(vm.Attachments.Single(a => a.PageNumber == null), documentOut)).Should().BeTrue();
        (await File.ReadAllBytesAsync(documentOut)).Should().Equal(DocumentPayload);

        var annotationOut = Path.Combine(_tempDir, "extracted.bin");
        (await vm.SaveAttachmentAsync(vm.Attachments.Single(a => a.PageNumber == 2), annotationOut)).Should().BeTrue();
        (await File.ReadAllBytesAsync(annotationOut)).Should().Equal(AnnotationPayload,
            "saving must write the embedded file's decoded content byte for byte");
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

        await vm.SaveSelectedAttachmentCommand.Execute();

        suggested.Should().Be("invoice.xml", "the picker must suggest the attachment's own name");
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(DocumentPayload);
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
    public async Task SaveAll_WritesEveryAttachment_WithoutOverwritingOrLeavingTheFolder()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());

        var folder = Directory.CreateDirectory(Path.Combine(_tempDir, "out")).FullName;
        var existing = Path.Combine(folder, "invoice.xml");
        await File.WriteAllTextAsync(existing, "already here");
        vm.PickFolderOverride = () => Task.FromResult<string?>(folder);

        var saved = await vm.SaveAllAttachmentsCommand.Execute();

        saved.Should().Be(2);
        (await File.ReadAllTextAsync(existing)).Should().Be("already here", "Save All never overwrites");
        (await File.ReadAllBytesAsync(Path.Combine(folder, "invoice (2).xml"))).Should().Equal(DocumentPayload);
        (await File.ReadAllBytesAsync(Path.Combine(folder, AnnotationFileName))).Should().Equal(AnnotationPayload);
        Directory.GetFiles(_tempDir).Should().NotContain(f => f.EndsWith(".xml") || f.EndsWith(".bin"),
            "nothing may be written outside the chosen folder");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task SaveAll_PickerCancelled_WritesNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.PickFolderOverride = () => Task.FromResult<string?>(null);

        (await vm.SaveAllAttachmentsAsync()).Should().Be(0);
    }

    // ── /P permissions ───────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Save_IsRefused_WhenTheDocumentForbidsExtraction()
    {
        // Bit 5 (value 16) cleared: copying or otherwise extracting content is not allowed.
        var restricted = SaveEncrypted(PdfWithBothCarriers("restricted-src.pdf"), "restricted.pdf", -4 & ~16L);
        var (vm, toasts) = CreateWithToasts();
        await vm.LoadDocumentAsync(restricted);
        vm.IsDocumentLoaded.Should().BeTrue("an empty user password opens without a prompt");
        vm.Attachments.Should().HaveCount(2, "listing is disclosure, not extraction, and stays allowed");

        var direct = Path.Combine(_tempDir, "direct.xml");
        (await vm.SaveAttachmentAsync(vm.Attachments[0], direct)).Should().BeFalse();
        File.Exists(direct).Should().BeFalse();

        var pickerCalls = 0;
        vm.PickAttachmentSavePathOverride = _ => { pickerCalls++; return Task.FromResult<string?>(direct); };
        vm.SelectedAttachment = vm.Attachments[0];
        await vm.SaveSelectedAttachmentAsync();
        pickerCalls.Should().Be(0, "a refused save must not first ask where to put the file");

        var folderCalls = 0;
        vm.PickFolderOverride = () => { folderCalls++; return Task.FromResult<string?>(_tempDir); };
        (await vm.SaveAllAttachmentsAsync()).Should().Be(0);
        folderCalls.Should().Be(0);

        toasts.Count(t => t.Message == "Blocked by document permissions").Should().Be(3,
            "each refusal is visible, never a silent no-op");
        File.Exists(direct).Should().BeFalse();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Save_IsAllowed_WhenOnlyOtherPermissionsAreRestricted()
    {
        // Bit 3 (print) cleared, bit 5 kept: saving an attachment is still allowed.
        var restricted = SaveEncrypted(PdfWithAttachment("noprint-src.pdf"), "noprint.pdf", -4 & ~4L);
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(restricted);

        var outPath = Path.Combine(_tempDir, "allowed.xml");
        (await vm.SaveAttachmentAsync(vm.Attachments[0], outPath)).Should().BeTrue();
        (await File.ReadAllBytesAsync(outPath)).Should().Equal(DocumentPayload,
            "an encrypted document's attachment is saved decrypted and exact");
    }

    // ── remove ───────────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task StripAttachments_MarksTheDocumentDirty_ButDoesNotTouchTheOriginalFile()
    {
        var pdf = PdfWithAttachment();
        var originalBytes = await File.ReadAllBytesAsync(pdf);

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(pdf);

        await vm.RemoveAllAttachmentsCommand.Execute();

        vm.HasAttachments.Should().BeFalse("the in-memory document no longer carries them");
        vm.HasUnsavedDocumentChanges.Should().BeTrue(
            "stripping is a pending edit, so the save routing and the close guard apply (#1233)");
        (await File.ReadAllBytesAsync(pdf)).Should().Equal(originalBytes,
            "stripping must NOT rewrite the source file — destroying data in place to protect it " +
            "is the failure mode this guards against");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task StripAttachments_IsUndoableAndRedoable()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(PdfWithAttachment());

        vm.StripAllAttachments();
        vm.Attachments.Should().BeEmpty();
        vm.CanUndo.Should().BeTrue();

        await vm.UndoCommand.Execute();
        vm.Attachments.Should().ContainSingle(a => a.FileName == "invoice.xml", "undo puts the attachment back");
        vm.HasUnsavedDocumentChanges.Should().BeFalse("undoing the only edit leaves the document clean");

        var restoredOut = Path.Combine(_tempDir, "after-undo.xml");
        (await vm.SaveAttachmentAsync(vm.Attachments[0], restoredOut)).Should().BeTrue();
        (await File.ReadAllBytesAsync(restoredOut)).Should().Equal(DocumentPayload,
            "the restored attachment is the same file, not an empty shell");

        await vm.RedoCommand.Execute();
        vm.Attachments.Should().BeEmpty("redo strips it again");
        vm.HasUnsavedDocumentChanges.Should().BeTrue();
    }

    /// <summary>
    /// #1572: Remove All used to strip only the document-level tree and left a
    /// page's /FileAttachment file in place. It now removes both, and the
    /// toast counts what the strip removed.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task StripAttachments_RemovesPageAnnotationAttachmentsToo()
    {
        var (vm, toasts) = CreateWithToasts();
        await vm.LoadDocumentAsync(PdfWithBothCarriers());
        vm.Attachments.Should().HaveCount(2, "precondition");
        toasts.Clear();

        vm.StripAllAttachments();

        vm.Attachments.Should().BeEmpty("the page annotation's file is an attachment like any other (#1572)");
        toasts.Should().ContainSingle().Which.Message.Should().StartWith("2 attachments removed");

        await vm.UndoCommand.Execute();
        vm.Attachments.Should().HaveCount(2, "undo restores the annotation as well as the document-level entry");
        vm.Attachments.Should().ContainSingle(a => a.PageNumber == 2);
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task StripAttachments_WithOnlyAnnotationAttachments_RemovesThem()
    {
        var (vm, toasts) = CreateWithToasts();
        await vm.LoadDocumentAsync(PdfWithBothCarriers("annotation-only.pdf", documentLevel: false));
        vm.Attachments.Should().ContainSingle("precondition");
        toasts.Clear();

        vm.StripAllAttachments();

        vm.Attachments.Should().BeEmpty();
        vm.HasUnsavedDocumentChanges.Should().BeTrue();
        vm.CanUndo.Should().BeTrue();
        toasts.Should().ContainSingle(t => t.Message.StartsWith("1 attachment removed"));
    }

    /// <summary>
    /// #1572, checked by tools that are not excise: poppler's pdfdetach lists
    /// annotation attachments (qpdf's --list-attachments does not), and the
    /// saved-byte scanner reads inside compressed streams.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task StripThenSave_RemovesAnnotationAttachment_VerifiedByPdfdetach()
    {
        Assert.SkipWhen(PdfdetachList(PdfWithoutAttachment("probe.pdf")) == null,
            "pdfdetach (poppler) is not installed [requires: tool:pdfdetach]");

        var pdf = PdfWithBothCarriers("both-for-pdfdetach.pdf");
        PdfdetachList(pdf).Should().Contain(AnnotationFileName,
            "the independent oracle must see the annotation attachment BEFORE the strip");
        Excise.TestSupport.SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(pdf), AnnotationFileName)
            .Should().NotBeEmpty("guard: the scanner must see the file name before the strip");

        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(pdf);
        vm.StripAllAttachments();
        var strippedPath = Path.Combine(_tempDir, "stripped-both.pdf");
        await vm.SaveFileAsAsync(strippedPath);

        PdfdetachList(strippedPath).Should().Contain("0 embedded files",
            "pdfdetach — not excise — must find no attachment of either kind");
        Excise.TestSupport.SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(strippedPath), AnnotationFileName)
            .Should().BeEmpty();
        Excise.TestSupport.SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(strippedPath), AttachmentMarker)
            .Should().BeEmpty();
    }

    private static string? PdfdetachList(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pdfdetach", $"-list \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            // #925/#1516: drain both pipes concurrently and bound the wait.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
                throw new TimeoutException(
                    "pdfdetach -list did not exit within 30s; killed it rather than hanging the suite (#1516).");
            }
            _ = stderrTask.GetAwaiter().GetResult();
            var output = stdoutTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
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
}
