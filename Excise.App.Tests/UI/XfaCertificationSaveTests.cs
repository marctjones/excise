using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2024 (decision 19): the GUI says, before and after, that saving a laid-out certified XFA form
/// removes its certification. Before: a sentence in the XFA banner. After: a warning toast naming
/// each removal, on Save As (Save, Save Flattened Form Copy and Reduce File Size share
/// <c>ReportCertificationRemovals</c>). The saved bytes are read with the inflating byte scanner.
/// </summary>
[Collection("AvaloniaTests")]
public class XfaCertificationSaveTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"excise-xfa-cert-{Guid.NewGuid():N}");

    public XfaCertificationSaveTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private sealed class ConfirmingDialogService : IUserDialogService
    {
        public List<string> Confirmed { get; } = new();
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            Confirmed.Add(title);
            return Task.FromResult(true);
        }
    }

    private static (MainWindowViewModel Vm, ConfirmingDialogService Dialog, List<ToastService.ToastEventArgs> Toasts) Create()
    {
        var dialog = new ConfirmingDialogService();
        var toasts = new ToastService();
        var seen = new List<ToastService.ToastEventArgs>();
        toasts.ToastRequested += (_, e) => seen.Add(e);
        var vm = MainWindowViewModelTestFactory.Create(toastService: toasts, dialogService: dialog);
        return (vm, dialog, seen);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [FixedAvaloniaFact]
    public async Task CertifiedLaidOutForm_BannerSaysSavingRemovesIt_AndSaveAsToastsEachRemoval()
    {
        var path = Write("certified.pdf", CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm()));
        var (vm, dialog, toasts) = Create();

        await vm.LoadDocumentAsync(path);

        vm.IsXfaFormLaidOut.Should().BeTrue();
        vm.XfaNoticeMessage.Should().EndWith(MainWindowViewModel.CertifiedXfaNoticeSuffix,
            "the user is told before saving that a save removes the certification");

        var output = Path.Combine(_tempDir, "saved.pdf");
        await vm.SaveFileAsAsync(output);

        dialog.Confirmed.Should().ContainSingle(t => t.Contains("Signed"),
            "#1415 still asks: #2025's whole-tree walk finds the certification field under its subform");
        var warning = toasts.Should().ContainSingle(t => t.Message == MainWindowViewModel.CertificationRemovedToastTitle).Subject;
        warning.Severity.Should().Be(ToastService.ToastSeverity.Warning);
        warning.Details.Should().Contain("/Perms /DocMDP").And.Contain("/Perms /UR3").And.Contain("/Legal");

        var saved = File.ReadAllBytes(output);
        foreach (var marker in new[] { CertifiedFormFixtures.DocMdpSigner, CertifiedFormFixtures.Ur3Signer, CertifiedFormFixtures.LegalMarker })
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
        vm.XfaNoticeMessage.Should().NotContain(MainWindowViewModel.CertifiedXfaNoticeSuffix.Trim(),
            "the reopened copy is no longer certified");
    }

    [FixedAvaloniaFact]
    public async Task UncertifiedLaidOutForm_NoBannerSentence_NoToast()
    {
        var path = Write("plain.pdf", CertifiedFormFixtures.DynamicForm());
        var (vm, _, toasts) = Create();

        await vm.LoadDocumentAsync(path);
        vm.XfaNoticeMessage.Should().Be(MainWindowViewModel.LaidOutXfaNoticeMessage);
        await vm.SaveFileAsAsync(Path.Combine(_tempDir, "saved.pdf"));

        toasts.Should().NotContain(t => t.Message == MainWindowViewModel.CertificationRemovedToastTitle);
    }
}
