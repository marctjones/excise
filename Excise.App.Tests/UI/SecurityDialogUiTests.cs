using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.Core.Security;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Drives the real <see cref="SecurityDialog"/> window (#641) through
/// headless REAL mouse clicks (pointer press/release through the window,
/// not RaiseEvent or direct command invocation — the discipline that caught
/// a real CanExecute→IsEnabled wiring bug in #658's dialog). The
/// crypto/save-picker delegates are faked; the encryption writer itself is
/// covered by <c>PdfDocumentWriterEncryptionTests</c> and the
/// qpdf/mutool/Ghostscript interop suite.
/// </summary>
[Collection("AvaloniaTests")]
public class SecurityDialogUiTests
{
    [FixedAvaloniaFact]
    public async Task ApplyClick_OnUnencryptedDocumentWithPassword_RunsApplyAndShowsResult()
    {
        var applied = false;
        var vm = new SecurityDialogViewModel(
            isEncrypted: false,
            verifyCurrentPassword: _ => true,
            applyAsync: (user, _, _) =>
            {
                applied = true;
                user.Should().Be("secret");
                return Task.FromResult<string?>("/tmp/protected.pdf");
            },
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var applyButton = FindButtonByName(window, "Apply Security Settings");
        applyButton.Should().NotBeNull();
        applyButton!.IsEnabled.Should().BeFalse("no password entered yet — nothing to protect");

        vm.NewUserPassword = "secret";
        await KeyboardTestHelpers.FlushDispatcherAsync();
        applyButton.IsEnabled.Should().BeTrue("a password is now entered");

        await ClickAsync(window, applyButton);

        applied.Should().BeTrue("a real click on Apply must reach the apply delegate");
        vm.ResultMessage.Should().Contain("/tmp/protected.pdf");

        window.UpdateLayout();
        FindTextByContent(window, vm.ResultMessage!).Should().NotBeNull(
            "the result must actually render in the window, not just live on the view model");
    }

    [FixedAvaloniaFact]
    public async Task RemoveProtectionButton_IsHiddenOnUnencryptedDocuments()
    {
        var vm = new SecurityDialogViewModel(
            isEncrypted: false,
            verifyCurrentPassword: _ => true,
            applyAsync: (_, _, _) => Task.FromResult<string?>(null),
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var removeButton = FindButtonByName(window, "Remove Password Protection");
        removeButton.Should().NotBeNull();
        removeButton!.IsVisible.Should().BeFalse("an unencrypted document has no protection to remove");
    }

    [FixedAvaloniaFact]
    public async Task BlankFieldsThenApplyClick_OnEncryptedDocument_ReEncrypts_DoesNotStripProtection()
    {
        // The #638-shaped guarantee, exercised through the real UI: on an
        // already-encrypted document, clearing the password fields and
        // clicking Apply must invoke the APPLY (re-encrypt) delegate, never
        // the remove-protection one.
        var applied = false;
        var removed = false;
        var vm = new SecurityDialogViewModel(
            isEncrypted: true,
            verifyCurrentPassword: _ => true,
            applyAsync: (user, owner, _) =>
            {
                applied = true;
                user.Should().BeNull("blank field means empty password, still encrypted");
                owner.Should().BeNull();
                return Task.FromResult<string?>("/tmp/still-protected.pdf");
            },
            removeProtectionAsync: () => { removed = true; return Task.FromResult<string?>("/tmp/unprotected.pdf"); });

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var applyButton = FindButtonByName(window, "Apply Security Settings");
        applyButton.Should().NotBeNull();
        applyButton!.IsEnabled.Should().BeTrue("an encrypted document can always be re-encrypted");

        await ClickAsync(window, applyButton);

        applied.Should().BeTrue();
        removed.Should().BeFalse("blank fields + Apply must never silently strip protection");
    }

    [FixedAvaloniaFact]
    public async Task RemoveProtectionClick_OnEncryptedDocument_InvokesOnlyTheRemovalDelegate()
    {
        var applied = false;
        var removed = false;
        var vm = new SecurityDialogViewModel(
            isEncrypted: true,
            verifyCurrentPassword: _ => true,
            applyAsync: (_, _, _) => { applied = true; return Task.FromResult<string?>("/tmp/x.pdf"); },
            removeProtectionAsync: () => { removed = true; return Task.FromResult<string?>("/tmp/unprotected.pdf"); });

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var removeButton = FindButtonByName(window, "Remove Password Protection");
        removeButton.Should().NotBeNull();
        removeButton!.IsVisible.Should().BeTrue();
        removeButton.IsEnabled.Should().BeTrue();

        await ClickAsync(window, removeButton);

        removed.Should().BeTrue("a real click on Remove Protection must reach the removal delegate");
        applied.Should().BeFalse();
        vm.ResultMessage.Should().Contain("/tmp/unprotected.pdf");
    }

    [FixedAvaloniaFact]
    public async Task WrongCurrentPasswordThenApplyClick_ShowsErrorInTheWindow()
    {
        var vm = new SecurityDialogViewModel(
            isEncrypted: true,
            verifyCurrentPassword: candidate => candidate == "right",
            applyAsync: (_, _, _) => Task.FromResult<string?>("/tmp/x.pdf"),
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPassword = "wrong";
        var applyButton = FindButtonByName(window, "Apply Security Settings")!;
        await ClickAsync(window, applyButton);

        vm.ErrorMessage.Should().Be("Current password is incorrect.");
        window.UpdateLayout();
        FindTextByContent(window, "Current password is incorrect.").Should().NotBeNull(
            "the error must actually render in the window");
    }

    [FixedAvaloniaFact]
    public async Task CloseClick_ClosesTheWindow()
    {
        var vm = new SecurityDialogViewModel(
            isEncrypted: false,
            verifyCurrentPassword: _ => true,
            applyAsync: (_, _, _) => Task.FromResult<string?>(null),
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var closeButton = FindButtonByName(window, "Close Security Dialog")!;
        await ClickAsync(window, closeButton);

        closed.Should().BeTrue("the code-behind must wire CloseRequested to actually closing the window");
    }

    /// <summary>
    /// Real headless click: pointer press + release routed through the
    /// window, mirroring <c>MakeSearchableDialogUiTests.ClickAsync</c> —
    /// raising Button.ClickEvent alone does not run ButtonBase's
    /// OnClick/Command-execution logic.
    /// </summary>
    private static async Task ClickAsync(Window window, Control control)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var pointInWindow = control.TranslatePoint(center, window) ?? default;
        window.MouseDown(pointInWindow, MouseButton.Left);
        window.MouseUp(pointInWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static Button? FindButtonByName(global::Avalonia.LogicalTree.ILogical root, string automationName) =>
        CollectControls(root)
            .OfType<Button>()
            .FirstOrDefault(button => AutomationProperties.GetName(button) == automationName);

    private static TextBlock? FindTextByContent(global::Avalonia.LogicalTree.ILogical root, string text) =>
        CollectControls(root)
            .OfType<TextBlock>()
            .FirstOrDefault(tb => string.Equals(tb.Text, text, StringComparison.Ordinal));

    private static System.Collections.Generic.IEnumerable<Control> CollectControls(global::Avalonia.LogicalTree.ILogical root)
    {
        var stack = new System.Collections.Generic.Stack<global::Avalonia.LogicalTree.ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is Control control)
                yield return control;

            foreach (var child in node.LogicalChildren)
                stack.Push(child);
        }
    }
}
