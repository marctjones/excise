using System.Reactive.Linq;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.Core.Document;
using Excise.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>All GUI page mutations use the shared assemble authority before doing work (#1946).</summary>
public class PageOrganizationPermissionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"excise-page-permissions-{Guid.NewGuid():N}");

    public PageOrganizationPermissionTests() => Directory.CreateDirectory(_tempDir);

    public static IEnumerable<object[]> PageOperations()
    {
        string[] operations =
        [
            "RemoveCurrentPageCommand", "RemoveSelectedPagesCommand",
            "AddPagesCommand", "InsertPagesBeforeCurrentCommand", "InsertPagesAfterCurrentCommand",
            "AddPagesFromFileAsync", "InsertPagesFromFileAsync",
            "MoveCurrentPageEarlierCommand", "MoveCurrentPageLaterCommand",
            "MoveSelectedPagesEarlierCommand", "MoveSelectedPagesLaterCommand",
            "MovePageAsync", "MoveCurrentPageAsync", "MoveSelectedPagesAsync",
            "RotatePageLeftCommand", "RotatePageRightCommand", "RotatePage180Command",
        ];
        foreach (var operation in operations)
        {
            yield return [operation, true, false];
            yield return [operation, false, false];
            yield return [operation, true, true];
        }
    }

    [FixedAvaloniaTheory]
    [MemberData(nameof(PageOperations))]
    public async Task GuiPageOperation_FollowsAssemblePermission_AndPreservesAllowedBehavior(
        string operation, bool assembleDenied, bool ignorePermissions)
    {
        var source = Path.Combine(_tempDir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(source, pageCount: 4);
        var protectedSource = Path.Combine(_tempDir, "protected.pdf");
        using (var document = PdfDocument.Open(source))
        {
            document.Save(protectedSource, new PdfEncryptionOptions
            {
                UserPassword = "",
                OwnerPassword = "page-operation-owner",
                Permissions = assembleDenied ? -4 & ~1024L : -4,
            });
        }
        var insertSource = Path.Combine(_tempDir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertSource, "INSERTED_MARKER");
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        var vm = MainWindowViewModelTestFactory.Create(
            documentService: documentService,
            toastService: toastService,
            thumbnailPrewarmEnabled: false,
            settingsStore: new InMemorySettingsStore(),
            recentFilesStore: new InMemoryRecentFilesStore());
        try
        {
            await vm.LoadDocumentAsync(protectedSource);
            vm.IsDocumentLoaded.Should().BeTrue();
            vm.PageThumbnails.Should().HaveCount(4);
            vm.CurrentPageIndex = 1;
            vm.MarkPageForOperation(1, true);
            vm.MarkPageForOperation(2, true);
            vm.IgnoreDocumentPermissions = ignorePermissions;
            var pickerCalls = 0;
            vm.PickPdfFilesOverride = _ =>
            {
                pickerCalls++;
                return Task.FromResult<IReadOnlyList<string>>([insertSource]);
            };
            var preserveRequests = 0;
            vm.PreserveReadingPositionRequested += (_, _) => preserveRequests++;
            toasts.Clear();
            var before = Arrangement(documentService.GetCurrentDocument()!);

            await RunAsync(vm, operation, insertSource);

            var blocked = assembleDenied && !ignorePermissions;
            if (blocked)
            {
                Arrangement(documentService.GetCurrentDocument()!).Should().Equal(before,
                    "a denied operation must preserve page content, order, count and rotation");
                vm.CurrentPageIndex.Should().Be(1);
                vm.PageThumbnails.Where(t => t.IsMarkedForPageOperation).Select(t => t.PageIndex)
                    .Should().Equal(1, 2);
                vm.CanUndo.Should().BeFalse();
                vm.FileState.HasUnsavedChanges.Should().BeFalse();
                pickerCalls.Should().Be(0, "denied insertion must be explained before asking for a source file");
                preserveRequests.Should().Be(0, "a refusal must not change the reading-position workflow");
                toasts.Should().ContainSingle().Which.Should().Match<ToastService.ToastEventArgs>(t =>
                    t.Message == "Blocked by document permissions"
                    && t.Severity == ToastService.ToastSeverity.Warning
                    && t.Details != null && t.Details.Contains("/P bit 11"));
            }
            else
            {
                Arrangement(documentService.GetCurrentDocument()!).Should().Equal(ExpectedArrangement(operation));
                vm.CanUndo.Should().BeTrue("an admitted mutation remains undoable");
                vm.FileState.HasUnsavedChanges.Should().BeTrue();
                toasts.Should().NotContain(t => t.Severity == ToastService.ToastSeverity.Error
                    || t.Message == "Blocked by document permissions");
            }
        }
        finally
        {
            vm.FileState.MarkSaved();
            await vm.CloseDocumentCommand.Execute();
        }
    }

    private static async Task RunAsync(MainWindowViewModel vm, string operation, string insertSource)
    {
        switch (operation)
        {
            case "RemoveCurrentPageCommand": await vm.RemoveCurrentPageCommand.Execute(); break;
            case "RemoveSelectedPagesCommand": await vm.RemoveSelectedPagesCommand.Execute(); break;
            case "AddPagesCommand": await vm.AddPagesCommand.Execute(); break;
            case "InsertPagesBeforeCurrentCommand": await vm.InsertPagesBeforeCurrentCommand.Execute(); break;
            case "InsertPagesAfterCurrentCommand": await vm.InsertPagesAfterCurrentCommand.Execute(); break;
            case "AddPagesFromFileAsync": await vm.AddPagesFromFileAsync(insertSource); break;
            case "InsertPagesFromFileAsync": await vm.InsertPagesFromFileAsync(insertSource, 1); break;
            case "MoveCurrentPageEarlierCommand": await vm.MoveCurrentPageEarlierCommand.Execute(); break;
            case "MoveCurrentPageLaterCommand": await vm.MoveCurrentPageLaterCommand.Execute(); break;
            case "MoveSelectedPagesEarlierCommand": await vm.MoveSelectedPagesEarlierCommand.Execute(); break;
            case "MoveSelectedPagesLaterCommand": await vm.MoveSelectedPagesLaterCommand.Execute(); break;
            case "MovePageAsync": await vm.MovePageAsync(0, 3); break;
            case "MoveCurrentPageAsync": await vm.MoveCurrentPageAsync(3); break;
            case "MoveSelectedPagesAsync": await vm.MoveSelectedPagesAsync(-1); break;
            case "RotatePageLeftCommand": await vm.RotatePageLeftCommand.Execute(); break;
            case "RotatePageRightCommand": await vm.RotatePageRightCommand.Execute(); break;
            case "RotatePage180Command": await vm.RotatePage180Command.Execute(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static string[] Arrangement(PdfDocument document) =>
        Enumerable.Range(1, document.PageCount)
            .Select(page => $"{document.GetPage(page).Text.Trim()} @{document.GetPage(page).Rotation}").ToArray();

    private static string[] ExpectedArrangement(string operation)
    {
        string Page(int number, int rotation = 0) => $"Page {number} Content\nSecret on Page {number} @{rotation}";
        const string inserted = "INSERTED_MARKER @0";
        return operation switch
        {
            "RemoveCurrentPageCommand" => [Page(1), Page(3), Page(4)],
            "RemoveSelectedPagesCommand" => [Page(1), Page(4)],
            "AddPagesCommand" or "AddPagesFromFileAsync" => [Page(1), Page(2), Page(3), Page(4), inserted],
            "InsertPagesBeforeCurrentCommand" or "InsertPagesFromFileAsync" => [Page(1), inserted, Page(2), Page(3), Page(4)],
            "InsertPagesAfterCurrentCommand" => [Page(1), Page(2), inserted, Page(3), Page(4)],
            "MoveCurrentPageEarlierCommand" => [Page(2), Page(1), Page(3), Page(4)],
            "MoveCurrentPageLaterCommand" => [Page(1), Page(3), Page(2), Page(4)],
            "MoveSelectedPagesEarlierCommand" or "MoveSelectedPagesAsync" => [Page(2), Page(3), Page(1), Page(4)],
            "MoveSelectedPagesLaterCommand" => [Page(1), Page(4), Page(2), Page(3)],
            "MovePageAsync" => [Page(2), Page(3), Page(4), Page(1)],
            "MoveCurrentPageAsync" => [Page(1), Page(3), Page(4), Page(2)],
            "RotatePageLeftCommand" => [Page(1), Page(2, 270), Page(3), Page(4)],
            "RotatePageRightCommand" => [Page(1), Page(2, 90), Page(3), Page(4)],
            "RotatePage180Command" => [Page(1), Page(2, 180), Page(3), Page(4)],
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
