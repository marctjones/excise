using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// "Attachments" dialog (#1414): list the document's embedded files, save one
/// to disk, or strip them all.
/// </summary>
/// <remarks>
/// Its DataContext is the <see cref="MainWindowViewModel"/> itself rather than
/// a dedicated dialog view model, because every operation here mutates the OPEN
/// DOCUMENT's state — stripping attachments has to dirty the same
/// <c>FileState</c> the save routing and the unsaved-changes guard read
/// (#1233). A separate view model would have to marshal all of that back.
/// This code-behind holds no logic: each handler forwards to one view-model
/// method.
/// </remarks>
public partial class AttachmentsDialog : Window
{
    public AttachmentsDialog()
    {
        InitializeComponent();
    }

    private async void OnSaveAttachment(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            await viewModel.SaveSelectedAttachmentAsync();
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would tear down the process.
            System.Diagnostics.Debug.WriteLine($"Save attachment failed: {ex}");
        }
    }

    private void OnRemoveAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.StripAllAttachments();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
