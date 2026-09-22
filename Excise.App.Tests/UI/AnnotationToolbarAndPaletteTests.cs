using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1789 — two optional, independently-toggleable, OFF-by-default surfaces
/// for the same 15 annotation commands the Annotate menu already exposes: a
/// second toolbar row (<c>AnnotationToolbarBorder</c>, an in-window region
/// bound to <c>IsAnnotationToolbarVisible</c>) and a floating owned palette
/// window (<see cref="AnnotationPaletteWindow"/>, opened/closed by
/// <c>MainWindow.SetAnnotationPaletteVisibility</c>). Neither replaces the
/// Annotate menu, which this file does not touch.
/// </summary>
[Collection("AvaloniaTests")]
public class AnnotationToolbarAndPaletteTests
{
    [FixedAvaloniaFact]
    public async Task BothSurfaces_AreOffByDefault()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.IsAnnotationToolbarVisible.Should().BeFalse("opt-in convenience, not a default surface");
            vm.IsAnnotationPaletteVisible.Should().BeFalse("opt-in convenience, not a default surface");
            window.FindControl<Control>("AnnotationToolbarBorder")!.IsVisible.Should().BeFalse();
            window.AnnotationPalette.Should().BeNull("the palette window must not exist until toggled on");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task ToggleAnnotationToolbarCommand_FlipsOnlyTheToolbarRow_NotThePalette()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var border = window.FindControl<Control>("AnnotationToolbarBorder")!;

            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsAnnotationToolbarVisible.Should().BeTrue();
            border.IsVisible.Should().BeTrue();
            window.AnnotationPalette.Should().BeNull("the toolbar toggle must not open the separate palette window");

            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsAnnotationToolbarVisible.Should().BeFalse();
            border.IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task ToggleAnnotationPaletteCommand_OpensAndClosesAnOwnedNonModalWindow_WithoutTouchingTheToolbar()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var toolbarBorder = window.FindControl<Control>("AnnotationToolbarBorder")!;

            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.IsAnnotationPaletteVisible.Should().BeTrue();
            window.AnnotationPalette.Should().NotBeNull("the toggle must create the palette window");
            window.AnnotationPalette!.Owner.Should().Be(window,
                "the palette must be OWNED by the main window (#1789) — not an independent top-level window");
            window.AnnotationPalette!.ShowInTaskbar.Should().BeFalse();
            toolbarBorder.IsVisible.Should().BeFalse("the palette toggle is independent of the toolbar row");

            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsAnnotationPaletteVisible.Should().BeFalse();
            window.AnnotationPalette.Should().BeNull("toggling off must close the window, not just hide it");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task BothSurfaces_CanBeOnTogetherOrEitherAlone()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var border = window.FindControl<Control>("AnnotationToolbarBorder")!;

            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            border.IsVisible.Should().BeTrue();
            window.AnnotationPalette.Should().NotBeNull("both may be on at once");

            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            border.IsVisible.Should().BeFalse();
            window.AnnotationPalette.Should().NotBeNull("turning the toolbar off must leave the palette alone");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The palette's own title-bar close (the user closing the tool window
    /// directly rather than going through the View menu) must clear the VM
    /// flag too, so the menu's checked state does not lie.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ClosingThePaletteWindowDirectly_ClearsTheViewModelFlag()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var palette = window.AnnotationPalette;
            palette.Should().NotBeNull();

            palette!.Close();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.IsAnnotationPaletteVisible.Should().BeFalse(
                "the window's own close must flip the flag back, or the View menu checkbox would stay checked");
            window.AnnotationPalette.Should().BeNull();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Preferences_PersistBothTogglesAndThePalettePosition_AcrossSessions()
    {
        var store = new InMemorySettingsStore();
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false, settingsStore: store);
        var window = new MainWindow(store) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
        vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.AnnotationPalette!.Position = new PixelPoint(321, 87);

        window.Close();

        store.Current.AnnotationToolbarVisible.Should().BeTrue("closing the window writes the current choice");
        store.Current.AnnotationPaletteVisible.Should().BeTrue(
            "the palette was open at close time — its own Closing handler flips the VM flag, " +
            "but PersistWindowStateOnClose must persist the pre-close value, not 'closed'");
        store.Current.AnnotationPaletteX.Should().Be(321);
        store.Current.AnnotationPaletteY.Should().Be(87);

        var reopened = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false, settingsStore: store);
        var second = new MainWindow(store) { DataContext = reopened, Width = 1200, Height = 900 };
        second.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();

            reopened.IsAnnotationToolbarVisible.Should().BeTrue("restored from window.json");
            reopened.IsAnnotationPaletteVisible.Should().BeTrue("restored from window.json");
            second.AnnotationPalette.Should().NotBeNull("a persisted-open palette reopens at startup");
            second.AnnotationPalette!.Position.X.Should().Be(321, "the saved position is restored, not re-defaulted");
            second.AnnotationPalette!.Position.Y.Should().Be(87);
        }
        finally
        {
            second.Close();
        }
    }

    [FixedAvaloniaFact]
    public void NewWindowSettings_LeaveBothSurfacesOff()
    {
        var settings = new WindowSettings();
        settings.AnnotationToolbarVisible.Should().BeFalse();
        settings.AnnotationPaletteVisible.Should().BeFalse();
        settings.AnnotationPaletteX.Should().BeNull();
        settings.AnnotationPaletteY.Should().BeNull();
    }

    // ── Real-input coverage ──────────────────────────────────────────────────
    //
    // A real click is HIT-TESTED input through the window (window.MouseDown /
    // MouseUp at a window-relative point translated from the target), the same
    // technique RedactionPreferenceControlTests.ClickAsync documents: raising
    // PointerPressed directly on the target leaves IsPointerOver false, and
    // Button/ToggleButton's own release handler then ignores the "click"
    // because its hit test at the release point never finds itself. Driving
    // every plain button on both new surfaces with this REAL synthetic input
    // (not Command.Execute) is what scripts/check-gui-interaction-coverage.sh
    // counts, and it is what the task asked for: at least one command firing
    // correctly from each surface. The five path-mode toggles are used for the
    // precise assertions because they need only IsDocumentLoaded=true — no
    // text selection or pending drag rect to stage.

    public static readonly TheoryData<string, PathAnnotationKind> ToolbarPathModeButtons = new()
    {
        { "AnnotationToolbarInkButton", PathAnnotationKind.Ink },
        { "AnnotationToolbarLineButton", PathAnnotationKind.Line },
        { "AnnotationToolbarArrowButton", PathAnnotationKind.Arrow },
        { "AnnotationToolbarPolygonButton", PathAnnotationKind.Polygon },
        { "AnnotationToolbarPolyLineButton", PathAnnotationKind.PolyLine },
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(ToolbarPathModeButtons))]
    public async Task RealPointerClick_OnToolbarRowButton_TogglesThePathAnnotationMode(
        string buttonName, PathAnnotationKind kind)
    {
        var (vm, window, pdf) = await OpenWithDocumentAsync();
        try
        {
            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            window.UpdateLayout();

            var button = window.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == buttonName);
            button.Should().NotBeNull($"MainWindow.axaml must declare {buttonName} on the annotation toolbar row");

            await ClickAsync(button!, window);

            vm.IsPathAnnotationMode.Should().BeTrue($"a real click on {buttonName} must enter path-annotation mode");
            vm.PathAnnotationKind.Should().Be(kind);
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdf);
        }
    }

    public static readonly TheoryData<string, PathAnnotationKind> PalettePathModeButtons = new()
    {
        { "PaletteInkButton", PathAnnotationKind.Ink },
        { "PaletteLineButton", PathAnnotationKind.Line },
        { "PaletteArrowButton", PathAnnotationKind.Arrow },
        { "PalettePolygonButton", PathAnnotationKind.Polygon },
        { "PalettePolyLineButton", PathAnnotationKind.PolyLine },
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(PalettePathModeButtons))]
    public async Task RealPointerClick_OnPaletteButton_TogglesThePathAnnotationMode(
        string buttonName, PathAnnotationKind kind)
    {
        var (vm, window, pdf) = await OpenWithDocumentAsync();
        try
        {
            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var palette = window.AnnotationPalette;
            palette.Should().NotBeNull();
            palette!.UpdateLayout();

            var button = palette.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == buttonName);
            button.Should().NotBeNull($"AnnotationPaletteWindow.axaml must declare {buttonName}");

            await ClickAsync(button!, palette);

            vm.IsPathAnnotationMode.Should().BeTrue($"a real click on {buttonName} must enter path-annotation mode");
            vm.PathAnnotationKind.Should().Be(kind);
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdf);
        }
    }

    /// <summary>
    /// The remaining plain buttons on each surface (selection- and drag-rect-
    /// gated commands) — a real click must not throw, matching
    /// GuiClickSafetySweepTests' "clicks don't crash" contract for the main
    /// toolbar and Annotate menu. Each of these commands already guards its
    /// own missing precondition (e.g. "Select text before adding a
    /// highlight"), so a click with no selection/drag-rect staged is expected
    /// to no-op safely, not throw.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData("AnnotationToolbarHighlightButton")]
    [InlineData("AnnotationToolbarUnderlineButton")]
    [InlineData("AnnotationToolbarStrikeOutButton")]
    [InlineData("AnnotationToolbarSquigglyButton")]
    [InlineData("AnnotationToolbarSquareButton")]
    [InlineData("AnnotationToolbarCircleButton")]
    [InlineData("AnnotationToolbarFreeTextButton")]
    [InlineData("AnnotationToolbarImageStampButton")]
    [InlineData("AnnotationToolbarStickyNoteButton")]
    public async Task RealPointerClick_OnRemainingToolbarButtons_DoesNotThrow(string buttonName)
    {
        var (vm, window, pdf) = await OpenWithDocumentAsync();
        try
        {
            vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            window.UpdateLayout();

            var button = window.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == buttonName);
            button.Should().NotBeNull();

            var act = async () => await ClickAsync(button!, window);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdf);
        }
    }

    [FixedAvaloniaTheory]
    [InlineData("PaletteHighlightButton")]
    [InlineData("PaletteUnderlineButton")]
    [InlineData("PaletteStrikeOutButton")]
    [InlineData("PaletteSquigglyButton")]
    [InlineData("PaletteSquareButton")]
    [InlineData("PaletteCircleButton")]
    [InlineData("PaletteFreeTextButton")]
    [InlineData("PaletteStampButton")]
    [InlineData("PaletteImageStampButton")]
    [InlineData("PaletteStickyNoteButton")]
    public async Task RealPointerClick_OnRemainingPaletteButtons_DoesNotThrow(string buttonName)
    {
        var (vm, window, pdf) = await OpenWithDocumentAsync();
        try
        {
            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var palette = window.AnnotationPalette;
            palette.Should().NotBeNull();
            palette!.UpdateLayout();

            var button = palette.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == buttonName);
            button.Should().NotBeNull();

            var act = async () => await ClickAsync(button!, palette);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdf);
        }
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenWithDocumentAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-annotation-surfaces-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(path);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await vm.LoadDocumentAsync(path);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        return (vm, window, path);
    }

    /// <summary>
    /// A real, hit-tested click: translate the target's centre into the
    /// window's coordinate space and drive <c>window.MouseDown</c>/<c>MouseUp</c>
    /// there, exactly as <c>RedactionPreferenceControlTests.ClickAsync</c> does
    /// — see the remark on the "Real-input coverage" section above for why a
    /// PointerPressed/Released pair raised directly on the target does not
    /// reliably fire Button.Click.
    /// </summary>
    private static async Task ClickAsync(Control target, Window window)
    {
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();

        var centre = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var inWindow = target.TranslatePoint(centre, window) ?? default;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }
}
