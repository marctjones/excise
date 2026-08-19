using System.Linq;
using AwesomeAssertions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1021 — the annotation display controls, exercised through the real window
/// rather than by calling the view model directly.
///
/// <para><b>Why through the window.</b> A view-model property that nothing binds
/// to is a setting the user cannot reach, and this repo has shipped exactly that
/// twice: <c>view.toggleAnnotations</c> went unregistered and failed two release
/// gates after t0 was green, and <c>verify-lazy-startup.sh</c> was wired into
/// nothing — twice. Asserting on the view model alone would reproduce that class
/// of bug rather than catch it.</para>
///
/// <para>So each control is checked end to end: a menu item exists, it carries
/// the automation id, its command is bound and executes, and executing it moves
/// the property the viewer is bound to.</para>
/// </summary>
public class AnnotationDisplayControlTests
{
    private static (MainWindow Window, MainWindowViewModel Vm) Open()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        window.UpdateLayout();
        return (window, vm);
    }

    private static MenuItem? FindMenuItem(MainWindow w, string name) =>
        w.GetLogicalDescendants().OfType<MenuItem>().FirstOrDefault(m => m.Name == name);

    public static TheoryData<string, string> Controls() => new()
    {
        { "ViewCommentAnnotationsMenuItem",      "view.toggleCommentAnnotations" },
        { "ViewFieldAndLinkAnnotationsMenuItem", "view.toggleFieldAndLinkAnnotations" },
        { "ViewHighlightFormFieldsMenuItem",     "view.toggleFormFieldHighlighting" },
        { "ViewAnnotationAuditMenuItem",         "view.toggleAnnotationAuditMode" },
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(Controls))]
    public void EachDisplayControl_ExistsAndItsCommandExecutes(string menuName, string commandId)
    {
        var (window, _) = Open();

        var item = FindMenuItem(window, menuName);
        item.Should().NotBeNull($"{menuName} must exist in the View menu — a setting with no menu " +
                                "item is one the user cannot reach");
        item!.Command.Should().NotBeNull($"{menuName} must have a bound command");
        item.Command!.CanExecute(null).Should().BeTrue($"{menuName}'s command must be executable");

        var id = Excise.App.Automation.CommandAccessibility.GetCommandId(item);
        id.Should().Be(commandId,
            "the automation id is how scripting and accessibility reach this control");
    }

    [FixedAvaloniaFact]
    public void TheTwoVisibilityGroupsToggleIndependently()
    {
        var (window, vm) = Open();

        vm.AreCommentAnnotationsVisible.Should().BeTrue("comments show by default");
        vm.AreFieldAndLinkAnnotationsVisible.Should().BeTrue("fields and links show by default");

        FindMenuItem(window, "ViewCommentAnnotationsMenuItem")!.Command!.Execute(null);
        vm.AreCommentAnnotationsVisible.Should().BeFalse("the comments toggle must move its own property");
        vm.AreFieldAndLinkAnnotationsVisible.Should().BeTrue(
            "hiding comments must NOT hide form fields — that separation is the whole point of two groups");

        FindMenuItem(window, "ViewFieldAndLinkAnnotationsMenuItem")!.Command!.Execute(null);
        vm.AreFieldAndLinkAnnotationsVisible.Should().BeFalse();
        vm.AreCommentAnnotationsVisible.Should().BeFalse("and the two must not interfere in the other direction");
    }

    [FixedAvaloniaFact]
    public void TheChromeControlsAreOffByDefault()
    {
        var (_, vm) = Open();

        // The load-bearing half of both decisions. Audit mode draws what no
        // conforming viewer shows; the field tint is ink nothing in the file
        // asks for. A redaction tool must open a document showing the page as
        // it really is.
        vm.IsAnnotationAuditModeEnabled.Should().BeFalse(
            "audit mode reveals annotations the file marks Hidden — never a default");
        vm.AreFormFieldsHighlighted.Should().BeFalse(
            "field highlighting is viewer chrome — never a default");
    }

    [FixedAvaloniaFact]
    public void TheChromeControlsToggleWhenInvoked()
    {
        var (window, vm) = Open();

        FindMenuItem(window, "ViewAnnotationAuditMenuItem")!.Command!.Execute(null);
        vm.IsAnnotationAuditModeEnabled.Should().BeTrue();

        FindMenuItem(window, "ViewHighlightFormFieldsMenuItem")!.Command!.Execute(null);
        vm.AreFormFieldsHighlighted.Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public void TheViewerControlIsBoundToEveryDisplayProperty()
    {
        // Without this, the toggles would move view-model state that never
        // reaches the renderer — the setting "does nothing" while every
        // view-model assertion passes.
        var (window, vm) = Open();
        var viewer = window.GetLogicalDescendants().OfType<PdfViewerControl>().FirstOrDefault();
        viewer.Should().NotBeNull("the window must host a PdfViewerControl");

        vm.AreCommentAnnotationsVisible = false;
        vm.AreFieldAndLinkAnnotationsVisible = false;
        vm.IsAnnotationAuditModeEnabled = true;
        vm.AreFormFieldsHighlighted = true;
        window.UpdateLayout();

        viewer!.ShowCommentAnnotations.Should().BeFalse("ShowCommentAnnotations must be bound");
        viewer.ShowFieldAndLinkAnnotations.Should().BeFalse("ShowFieldAndLinkAnnotations must be bound");
        viewer.RevealHiddenAnnotations.Should().BeTrue("RevealHiddenAnnotations must be bound");
        viewer.HighlightFormFields.Should().BeTrue("HighlightFormFields must be bound");
    }
}
