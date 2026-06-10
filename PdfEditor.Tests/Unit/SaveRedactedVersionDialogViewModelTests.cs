using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using AwesomeAssertions;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.Unit;

public class SaveRedactedVersionDialogViewModelTests
{
    [Fact]
    public void PendingCountText_UsesSingularAndPluralLabels()
    {
        var viewModel = new SaveRedactedVersionDialogViewModel("/tmp/output.pdf", 1);

        viewModel.PendingCountText.Should().Be("1 area will be redacted");

        viewModel.PendingCount = 2;

        viewModel.PendingCountText.Should().Be("2 areas will be redacted");
    }

    [Fact]
    public void PendingCount_SetterRaisesPendingCountTextChanged()
    {
        var viewModel = new SaveRedactedVersionDialogViewModel("/tmp/output.pdf", 1);
        var raised = false;
        void Handler(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SaveRedactedVersionDialogViewModel.PendingCountText))
            {
                raised = true;
            }
        }

        viewModel.PropertyChanged += Handler;

        try
        {
            viewModel.PendingCount = 3;
        }
        finally
        {
            viewModel.PropertyChanged -= Handler;
        }

        raised.Should().BeTrue();
    }

    [Fact]
    public void SaveCommand_TracksSaveFilePathValidity()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdfe-save-dialog-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var validPath = Path.Combine(root, "document_REDACTED.pdf");
            var missingDirectoryPath = Path.Combine(root, "missing", "document_REDACTED.pdf");

            var viewModel = new SaveRedactedVersionDialogViewModel(validPath, 1);
            var saveCommand = (ICommand)viewModel.SaveCommand;

            saveCommand.CanExecute(null).Should().BeTrue();

            viewModel.SaveFilePath = "";
            saveCommand.CanExecute(null).Should().BeFalse();

            viewModel.SaveFilePath = missingDirectoryPath;
            saveCommand.CanExecute(null).Should().BeFalse();

            viewModel.SaveFilePath = validPath;
            saveCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
