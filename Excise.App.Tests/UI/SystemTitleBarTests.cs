using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using ReactiveUI;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The window uses the operating system's title bar (#1664) instead of drawing
/// its own. What can be asserted headlessly is the absence of the old
/// machinery and the binding that feeds the system bar; how each OS draws it
/// is a live check on the real window.
/// </summary>
[Collection("AvaloniaTests")]
public class SystemTitleBarTests
{
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task TheWindow_DoesNotExtendItsClientAreaIntoTheDecorations()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await Settle(window);
            window.ExtendClientAreaToDecorationsHint.Should().BeFalse();
            window.ExtendClientAreaTitleBarHeightHint.Should().BeLessThanOrEqualTo(0,
                "no height is reserved for a title bar the app draws");
            window.FindControl<Control>("TitleBarArea").Should().BeNull("the custom title row is gone");
            window.FindControl<Control>("TitleBarAppLabel").Should().BeNull();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task TheWindowTitle_IsBoundToTheViewModel()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await Settle(window);
            window.Title.Should().Be(vm.WindowTitle);
            vm.WindowTitle.Should().Be("Excise", "no document is open");

            var second = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
            window.DataContext = second;
            await Settle(window);
            window.Title.Should().Be(second.WindowTitle);
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(nameof(MainWindowViewModel.DocumentName))]
    [InlineData(nameof(MainWindowViewModel.IsDocumentLoaded))]
    [InlineData(nameof(MainWindowViewModel.SaveButtonText))]
    public void WindowTitle_IsRaisedWhenItsInputsChange(string input)
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RaisePropertyChanged(input);

        raised.Should().Contain(nameof(MainWindowViewModel.WindowTitle));
    }

    [Fact]
    public void TheCustomTitleBarWorkarounds_AreGone()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        var names = typeof(MainWindow).GetMembers(all).Select(m => m.Name).ToList();
        names.Should().NotContain("IsWindowDragPoint");
        names.Should().NotContain("MacWindowButtonInset");
        names.Should().NotContain("OnTitleBarPointerPressed");
        names.Should().NotContain("UpdateTitle", "the title is a binding, not code-behind");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task TheContent_StartsAtTheTopOfTheClientArea_AndNoticeBarsSitBelowIt()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            await Settle(window);
            var toolbar = window.FindControl<Border>("ToolbarBorder")!;
            var menu = window.FindControl<Menu>("MainMenuBar")!;
            var menuBottom = menu.IsVisible ? menu.Bounds.Height : 0;
            toolbar.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().Be(menuBottom,
                "nothing sits between the system title bar and the toolbar");

            var toast = window.FindControl<FluentAvalonia.UI.Controls.FAInfoBar>("ToastInfoBar")!;
            toast.Title = "notice";
            toast.IsOpen = true;
            await Settle(window);
            toast.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().BeGreaterThanOrEqualTo(
                toolbar.Bounds.Height + menuBottom);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(10);
        }
    }
}
