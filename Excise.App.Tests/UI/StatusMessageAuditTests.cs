using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class StatusMessageAuditTests
{
    [FixedAvaloniaFact]
    public async Task LoadDocumentAsync_ClearsOpeningStatus_WhenDocumentIsReady()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-status-audit-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow
            {
                DataContext = vm,
                Width = 1280,
                Height = 900,
            };
            window.Show();

            await vm.LoadDocumentAsync(path);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            vm.IsDocumentLoaded.Should().BeTrue();
            vm.OperationStatus.Should().NotBe("Opening PDF…",
                "the status bar should return to document status once the PDF is usable");

            window.Close();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task ClearingSearchText_CancelsSearchStatus()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1280,
            Height = 900,
        };
        window.Show();

        vm.SearchText = "Page";
        vm.OperationStatus.Should().Be("Searching…");

        vm.SearchText = string.Empty;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        vm.OperationStatus.Should().BeEmpty();
        vm.IsSearching.Should().BeFalse();
        vm.SearchProgressText.Should().BeEmpty();

        window.Close();
    }
}
