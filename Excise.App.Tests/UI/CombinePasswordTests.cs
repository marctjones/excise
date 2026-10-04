using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Security;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>Combine's password prompt must not replace the open document or write on failure (#1947).</summary>
[Collection("AvaloniaTests")]
public sealed class CombinePasswordTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"excise-combine-password-{Guid.NewGuid():N}");
    private readonly PdfDocumentService _service = new(NullLogger<PdfDocumentService>.Instance);

    public CombinePasswordTests() => Directory.CreateDirectory(_directory);

    private string Source(string name, string password, long permissions = -4)
    {
        var plain = Path.Combine(_directory, name + "-plain.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(plain, name);
        var encrypted = Path.Combine(_directory, name + ".pdf");
        using var document = PdfDocument.Open(plain);
        document.Save(encrypted, new PdfEncryptionOptions
        {
            UserPassword = password,
            OwnerPassword = "combine-owner-1947",
            Algorithm = PdfEncryptionAlgorithm.Aes128,
            Permissions = permissions,
        });
        return encrypted;
    }

    [FixedAvaloniaFact]
    public async Task CombineDocumentsCommand_PromptsForEachSource_AndPreservesFirstPassword()
    {
        var first = Source("FIRST_PASSWORD_SOURCE", "first-password");
        var second = Source("SECOND_PASSWORD_SOURCE", "second-password");
        var dialog = new PasswordDialog("first-password", "second-password");
        var output = Path.Combine(_directory, "combined.pdf");
        var loaded = LoadEditedDocument();
        var notifications = new List<ToastService.ToastEventArgs>();
        var vm = CreateViewModel(dialog, new[] { first, second }, output, notifications);

        await vm.CombineDocumentsCommand.Execute();

        dialog.Messages.Should().HaveCount(2);
        dialog.Messages[0].Should().Contain(Path.GetFileName(first));
        dialog.Messages[1].Should().Contain(Path.GetFileName(second));
        notifications.Should().ContainSingle().Which.Severity.Should().Be(ToastService.ToastSeverity.Success);
        _service.GetCurrentDocument().Should().BeSameAs(loaded);
        loaded.GetPage(1).Rotation.Should().Be(90, "Combine must preserve unsaved edits");
        var unprotectedOpen = () => PdfDocument.Open(output);
        unprotectedOpen.Should().Throw<PdfEncryptionNotSupportedException>();
        using var combined = PdfDocument.Open(output, new PdfOpenOptions { UserPassword = "first-password" });
        combined.PageCount.Should().Be(2);
        combined.GetPage(1).Text.Should().Contain("FIRST_PASSWORD_SOURCE");
        combined.GetPage(2).Text.Should().Contain("SECOND_PASSWORD_SOURCE");

        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        QpdfReferenceTool.RequiresPassword(output).Should().Be(QpdfPasswordStatus.PasswordRequired);
        QpdfReferenceTool.RequiresPassword(output, "first-password").Should().Be(QpdfPasswordStatus.PasswordCorrect);
    }

    [FixedAvaloniaFact]
    public async Task CombineDocumentsCommand_CancelledPassword_PreservesExistingOutputAndUnsavedEdits()
        => await AssertFailedCommandPreservesDestination(null, ToastService.ToastSeverity.Informational, "Combine cancelled");

    [FixedAvaloniaFact]
    public async Task CombineDocumentsCommand_WrongPassword_PreservesExistingOutputAndUnsavedEdits()
        => await AssertFailedCommandPreservesDestination("wrong-password", ToastService.ToastSeverity.Error, "Failed to combine documents");

    private async Task AssertFailedCommandPreservesDestination(
        string? password, ToastService.ToastSeverity severity, string message)
    {
        var first = Source("FIRST", "first-password");
        var second = Source("SECOND", "second-password");
        var output = Path.Combine(_directory, "existing.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(output, "EXISTING_DESTINATION");
        var originalBytes = File.ReadAllBytes(output);
        var loaded = LoadEditedDocument();
        var dialog = new PasswordDialog("first-password", password);
        var notifications = new List<ToastService.ToastEventArgs>();
        var vm = CreateViewModel(dialog, new[] { first, second }, output, notifications);

        await vm.CombineDocumentsCommand.Execute();

        dialog.Messages.Should().HaveCount(2, "failure on a later source must preserve the entire destination");
        File.ReadAllBytes(output).Should().Equal(originalBytes);
        _service.GetCurrentDocument().Should().BeSameAs(loaded);
        loaded.GetPage(1).Rotation.Should().Be(90);
        notifications.Should().ContainSingle().Which.Severity.Should().Be(severity);
        notifications[0].Message.Should().Be(message);
    }

    [Fact]
    public void MergeDocumentsToPdf_WrongPasswordOnAliasedSource_DoesNotOverwriteSource()
    {
        var first = Source("FIRST", "first-password");
        var second = Source("SECOND", "second-password");
        var originalBytes = File.ReadAllBytes(first);
        var act = () => _service.MergeDocumentsToPdf(new[] { first, second }, first,
            userPasswordForSource: path => path == first ? "first-password" : "wrong-password");

        act.Should().Throw<PdfEncryptionNotSupportedException>();

        File.ReadAllBytes(first).Should().Equal(originalBytes);
    }

    [Fact]
    public void MergeDocumentsToPdf_PasswordResolver_DoesNotBypassAssemblePermissions()
    {
        var source = Source("DENIED", "source-password", permissions: -4 & ~1024L);
        var output = Path.Combine(_directory, "refused-output.pdf");
        var act = () => _service.MergeDocumentsToPdf(new[] { source }, output,
            userPasswordForSource: _ => "source-password");

        act.Should().Throw<InvalidOperationException>();
        File.Exists(output).Should().BeFalse();
    }

    private PdfDocument LoadEditedDocument()
    {
        var path = Path.Combine(_directory, "currently-edited.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(path, "UNSAVED_DOCUMENT");
        _service.LoadDocument(path);
        var document = _service.GetCurrentDocument()!;
        document.GetPage(1).Rotation = 90;
        return document;
    }

    private Excise.App.ViewModels.MainWindowViewModel CreateViewModel(
        PasswordDialog dialog, IReadOnlyList<string> paths, string output, List<ToastService.ToastEventArgs> notifications)
    {
        var toasts = new ToastService();
        toasts.ToastRequested += (_, args) => notifications.Add(args);
        var vm = MainWindowViewModelTestFactory.Create(
            documentService: _service, dialogService: dialog, toastService: toasts);
        vm.PickPdfFilesOverride = _ => Task.FromResult(paths);
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(output);
        return vm;
    }

    private sealed class PasswordDialog(params string?[] responses) : IUserDialogService
    {
        private readonly Queue<string?> _responses = new(responses);
        internal List<string> Messages { get; } = new();
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PromptPasswordAsync(string title, string message)
        {
            title.Should().Be("Password Required", "Combine uses the ordinary-open password dialog");
            Messages.Add(message);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    public void Dispose()
    {
        _service.CloseDocument();
        Directory.Delete(_directory, recursive: true);
    }
}
