using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.Core.Security;
using Excise.Ocr;
using Excise.App.Tests.UI.InteractionCoverage;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Drives the dialog INPUT controls (#1087) — SecurityDialog's password fields
/// and algorithm combo, MakeSearchableDialog's language field, preset combo and
/// re-OCR checkbox — with real keyboard/pointer input, then asserts the value
/// the user entered was carried through to the delegate the dialog runs.
///
/// <para>Their BUTTONS were already covered (<c>SecurityDialogUiTests</c>,
/// <c>MakeSearchableDialogUiTests</c>), so tests clicked Apply/Start on fields
/// nobody had typed into — the one state where the input handling cannot be
/// wrong. These fill the fields first. Full qpdf-verified encryption of the
/// resulting file is covered by the encryption writer + interop suite; here the
/// guarantee is that the TYPED password reaches the apply/encrypt delegate.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class DialogInputInteractionTests
{
    [FixedAvaloniaFact]
    public async Task SecurityDialog_TypedPasswordsAndChosenAlgorithm_AreCarriedToTheApplyDelegate()
    {
        string? appliedUser = null;
        string? appliedOwner = null;
        PdfEncryptionAlgorithm appliedAlgo = PdfEncryptionAlgorithm.Aes256;

        var vm = new SecurityDialogViewModel(
            isEncrypted: false,
            verifyCurrentPassword: _ => true,
            applyAsync: (user, owner, algo) =>
            {
                appliedUser = user;
                appliedOwner = owner;
                appliedAlgo = algo;
                return Task.FromResult<string?>("/tmp/protected.pdf");
            },
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        await TypeInto(window, "NewUserPasswordBox", "open-sesame");
        vm.NewUserPassword.Should().Be("open-sesame",
            "real text input into the user-password field must flow through the binding");

        await TypeInto(window, "NewOwnerPasswordBox", "owner-key");
        vm.NewOwnerPassword.Should().Be("owner-key");

        // Move the algorithm combo off its default with a real Down keypress.
        var combo = FindByName<ComboBox>(window, "AlgorithmComboBox");
        vm.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes256, "default before any input");
        RaiseKeyDown(combo, Key.Down);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes128,
            "a real Down keypress on the algorithm combo must move the selection to the next algorithm");

        var applyButton = FindByAutomationName<Button>(window, "Apply Security Settings")!;
        await Click(window, applyButton);

        appliedUser.Should().Be("open-sesame", "the typed user password must reach the encrypt delegate");
        appliedOwner.Should().Be("owner-key", "the typed owner password must reach the encrypt delegate");
        appliedAlgo.Should().Be(PdfEncryptionAlgorithm.Aes128, "the chosen algorithm must reach the encrypt delegate");

        AssertRecorded("SecurityDialog/TextBox:NewUserPasswordBox");
        AssertRecorded("SecurityDialog/TextBox:NewOwnerPasswordBox");
        AssertRecorded("SecurityDialog/ComboBox:AlgorithmComboBox");

        window.Close();
    }

    [FixedAvaloniaFact]
    public async Task SecurityDialog_TypedCurrentPassword_IsVerifiedOnApply()
    {
        string? verified = null;
        var vm = new SecurityDialogViewModel(
            isEncrypted: true,
            verifyCurrentPassword: candidate => { verified = candidate; return candidate == "the-current-one"; },
            applyAsync: (_, _, _) => Task.FromResult<string?>("/tmp/x.pdf"),
            removeProtectionAsync: () => Task.FromResult<string?>(null));

        var window = new SecurityDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        await TypeInto(window, "CurrentPasswordBox", "the-current-one");
        vm.CurrentPassword.Should().Be("the-current-one",
            "real text input into the current-password field must flow through the binding");

        var applyButton = FindByAutomationName<Button>(window, "Apply Security Settings")!;
        await Click(window, applyButton);

        verified.Should().Be("the-current-one",
            "the typed current password must be the one the dialog verifies on Apply");
        vm.ErrorMessage.Should().NotBe("Current password is incorrect.",
            "the correct current password was typed, so verification must pass");

        AssertRecorded("SecurityDialog/TextBox:CurrentPasswordBox");

        window.Close();
    }

    [FixedAvaloniaFact]
    public async Task MakeSearchableDialog_TypedLanguageAndReOcrToggle_AreCarriedToTheOcrRun()
    {
        string? runLanguage = null;
        bool? runForce = null;

        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (language, force, progress, _) =>
            {
                runLanguage = language;
                runForce = force;
                progress.Report((1, 1));
                return Task.FromResult(new SearchableDocumentResult(1, 0, 3, 0, Array.Empty<SearchablePageResult>()));
            });

        var window = new MakeSearchableDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        // Pick a preset with a real Down keypress on the preset combo (its
        // SelectedItem binds Language), moving off the default "eng".
        var presetCombo = FindByName<ComboBox>(window, "LanguagePresetComboBox");
        vm.Language.Should().Be("eng", "default before any input");
        RaiseKeyDown(presetCombo, Key.Down);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.Language.Should().Be("deu", "a real Down keypress on the preset combo must select the next preset language");

        // Then replace it entirely via real text input (a combined-language string).
        await TypeInto(window, "LanguageTextBox", "eng+deu", clearFirst: true);
        vm.Language.Should().Be("eng+deu", "real text input into the language field must flow through the binding");

        // Toggle the "Re-OCR" checkbox with a real click.
        var reocr = window.GetLogicalDescendants().OfType<CheckBox>()
            .FirstOrDefault(c => (c.Content as string) == "Re-OCR pages that already have text");
        reocr.Should().NotBeNull("the dialog must host the Re-OCR checkbox");
        window.UpdateLayout();
        await Click(window, reocr!);
        vm.Force.Should().BeTrue("a real click on the Re-OCR checkbox must set the Force option");

        var startButton = FindByAutomationName<Button>(window, "Start Make Searchable")!;
        await Click(window, startButton);

        vm.IsDone.Should().BeTrue("the fake OCR delegate completes synchronously");
        runLanguage.Should().Be("eng+deu", "the typed language must reach the OCR run");
        runForce.Should().BeTrue("the checked Re-OCR option must reach the OCR run");

        AssertRecorded("MakeSearchableDialog/TextBox:LanguageTextBox");
        AssertRecorded("MakeSearchableDialog/ComboBox:LanguagePresetComboBox");
        AssertRecorded("MakeSearchableDialog/CheckBox:Re-OCR pages that already have text");

        window.Close();
    }

    private static void AssertRecorded(string id) =>
        GuiInteractionRecorder.ObservedIds.Should().Contain(
            observed => observed.StartsWith(id + "\t", StringComparison.Ordinal),
            $"real input into {id} must reach the interaction-coverage recorder under its inventory id");

    private static async Task TypeInto(Window window, string controlName, string text, bool clearFirst = false)
    {
        var box = FindByName<TextBox>(window, controlName);
        box.Focus();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        if (clearFirst)
        {
            box.Text = string.Empty;
            await KeyboardTestHelpers.FlushDispatcherAsync();
        }
        window.KeyTextInput(text);
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static async Task Click(Window window, Control control)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var inWindow = control.TranslatePoint(center, window) ?? default;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static void RaiseKeyDown(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Bubble,
            Key = key,
        });

    private static T FindByName<T>(ILogical root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static T? FindByAutomationName<T>(ILogical root, string automationName) where T : Control =>
        root.GetLogicalDescendants().OfType<T>()
            .FirstOrDefault(c => global::Avalonia.Automation.AutomationProperties.GetName(c) == automationName);
}
