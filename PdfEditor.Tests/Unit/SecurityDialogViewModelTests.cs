using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AwesomeAssertions;
using Pdfe.Core.Security;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.Unit;

/// <summary>
/// Wiring tests for the "Document &gt; Security..." dialog (#641): command
/// enablement, password verification gating, and — most importantly — that
/// Apply and Remove Protection are genuinely separate paths so blank
/// password fields can never silently strip encryption (the #638 failure
/// mode re-created on this surface). All against injected fake delegates,
/// so these run without a window, storage provider, or real crypto — the
/// actual encryption writer is covered by
/// <c>Pdfe.Core.Tests/Writing/PdfDocumentWriterEncryptionTests.cs</c> and
/// the qpdf/mutool/Ghostscript interop suite.
/// </summary>
public class SecurityDialogViewModelTests
{
    private static SecurityDialogViewModel Make(
        bool isEncrypted,
        Func<string?, bool>? verify = null,
        Func<string?, string?, PdfEncryptionAlgorithm, Task<string?>>? apply = null,
        Func<Task<string?>>? remove = null)
        => new(
            isEncrypted,
            verify ?? (_ => true),
            apply ?? ((_, _, _) => Task.FromResult<string?>("/tmp/out.pdf")),
            remove ?? (() => Task.FromResult<string?>("/tmp/out.pdf")));

    [Fact]
    public void UnencryptedDocument_ApplyDisabledUntilAPasswordIsEntered()
    {
        var vm = Make(isEncrypted: false);

        vm.CanApply.Should().BeFalse("with no password entered there is nothing to protect");
        ((ICommand)vm.ApplyCommand).CanExecute(null).Should().BeFalse();

        vm.NewUserPassword = "secret";
        vm.CanApply.Should().BeTrue();
        ((ICommand)vm.ApplyCommand).CanExecute(null).Should().BeTrue();

        vm.NewUserPassword = "";
        vm.NewOwnerPassword = "owner-only";
        vm.CanApply.Should().BeTrue("an owner-only password is a valid configuration");
    }

    [Fact]
    public void UnencryptedDocument_RemoveProtectionIsNeverAvailable()
    {
        var vm = Make(isEncrypted: false);
        vm.CanRemove.Should().BeFalse("there is no protection to remove");
        ((ICommand)vm.RemoveProtectionCommand).CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task EncryptedDocument_BlankNewPasswords_ApplyStillEncrypts_NeverStripsProtection()
    {
        // THE core #638-shaped guarantee of this dialog: on an encrypted
        // document, clearing the new-password fields and clicking Apply
        // re-encrypts (empty passwords are a valid "no open prompt"
        // configuration) — it must never route to the remove-protection
        // path as an emergent side effect of empty text boxes.
        var applyCalled = false;
        var removeCalled = false;
        var vm = Make(
            isEncrypted: true,
            apply: (user, owner, _) =>
            {
                applyCalled = true;
                user.Should().BeNull();
                owner.Should().BeNull();
                return Task.FromResult<string?>("/tmp/out.pdf");
            },
            remove: () => { removeCalled = true; return Task.FromResult<string?>("/tmp/out.pdf"); });

        vm.CanApply.Should().BeTrue("an already-encrypted document can always be re-encrypted, even with blank fields");

        await vm.ApplyCommand.Execute();

        applyCalled.Should().BeTrue();
        removeCalled.Should().BeFalse("blank fields + Apply must NOT invoke the remove-protection path");
        vm.IsDone.Should().BeTrue();
    }

    [Fact]
    public async Task EncryptedDocument_WrongCurrentPassword_BlocksApplyWithoutInvokingTheDelegate()
    {
        var applyCalled = false;
        var vm = Make(
            isEncrypted: true,
            verify: candidate => candidate == "right",
            apply: (_, _, _) => { applyCalled = true; return Task.FromResult<string?>("/tmp/out.pdf"); });

        vm.CurrentPassword = "wrong";
        await vm.ApplyCommand.Execute();

        applyCalled.Should().BeFalse("a wrong current password must stop the operation before anything is written");
        vm.ErrorMessage.Should().Be("Current password is incorrect.");
        vm.IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task EncryptedDocument_WrongCurrentPassword_BlocksRemoveWithoutInvokingTheDelegate()
    {
        var removeCalled = false;
        var vm = Make(
            isEncrypted: true,
            verify: candidate => candidate == "right",
            remove: () => { removeCalled = true; return Task.FromResult<string?>("/tmp/out.pdf"); });

        vm.CurrentPassword = "wrong";
        await vm.RemoveProtectionCommand.Execute();

        removeCalled.Should().BeFalse();
        vm.ErrorMessage.Should().Be("Current password is incorrect.");
    }

    [Fact]
    public async Task Apply_PassesFieldValuesAndAlgorithmThrough()
    {
        (string? User, string? Owner, PdfEncryptionAlgorithm Algorithm)? received = null;
        var vm = Make(
            isEncrypted: false,
            apply: (user, owner, algorithm) =>
            {
                received = (user, owner, algorithm);
                return Task.FromResult<string?>("/tmp/protected.pdf");
            });

        vm.NewUserPassword = "u-pass";
        vm.NewOwnerPassword = "o-pass";
        vm.Algorithm = PdfEncryptionAlgorithm.Aes128;

        await vm.ApplyCommand.Execute();

        received.Should().NotBeNull();
        received!.Value.User.Should().Be("u-pass");
        received.Value.Owner.Should().Be("o-pass");
        received.Value.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes128);
        vm.ResultMessage.Should().Contain("/tmp/protected.pdf");
    }

    [Fact]
    public async Task Apply_UserCancelledPicker_IsNotAnErrorAndNotDone()
    {
        var vm = Make(isEncrypted: false, apply: (_, _, _) => Task.FromResult<string?>(null));
        vm.NewUserPassword = "secret";

        await vm.ApplyCommand.Execute();

        vm.ErrorMessage.Should().BeNull();
        vm.ResultMessage.Should().BeNull();
        vm.IsDone.Should().BeFalse("cancelling the save picker should leave the dialog open and editable");
        vm.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task Apply_DelegateThrows_SurfacesErrorAndStaysEditable()
    {
        var vm = Make(
            isEncrypted: false,
            apply: (_, _, _) => Task.FromException<string?>(new InvalidOperationException("disk full")));
        vm.NewUserPassword = "secret";

        await vm.ApplyCommand.Execute();

        vm.ErrorMessage.Should().Contain("disk full");
        vm.IsDone.Should().BeFalse();
        vm.CanEditOptions.Should().BeTrue();
    }

    [Fact]
    public async Task Completed_FiresWithTheOutputPath_AndCommandsDisableAfterSuccess()
    {
        string? completedPath = null;
        var vm = Make(isEncrypted: true);
        vm.Completed += (_, path) => completedPath = path;

        await vm.ApplyCommand.Execute();

        completedPath.Should().Be("/tmp/out.pdf");
        vm.CanApply.Should().BeFalse("the operation is done; re-running against a stale dialog state is not offered");
        vm.CanRemove.Should().BeFalse();
    }

    [Fact]
    public void DefaultAlgorithm_IsAes256()
    {
        Make(isEncrypted: false).Algorithm.Should().Be(
            PdfEncryptionAlgorithm.Aes256,
            "AES-256 (PDF 2.0 native) is the modern default; AES-128 exists only for older-reader compatibility");
    }
}
