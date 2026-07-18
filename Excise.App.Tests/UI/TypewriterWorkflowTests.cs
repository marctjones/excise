using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class TypewriterWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_FlattensPendingTypewriterTextIntoSavedPdf()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppTypewriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "source.pdf");
        var outputPath = Path.Combine(tempDir, "output.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var operationId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(operationId, "Saved typewriter note", 1);

        await vm.SaveFileAsAsync(outputPath);

        using var saved = PdfDocument.Open(outputPath);
        saved.GetPage(1).Text.Should().Contain("Saved typewriter note");
        vm.TypewriterTextOperations.Should().BeEmpty();
        vm.FileState.TypewriterEditsCount.Should().Be(0);

        window.Close();
    }
}
