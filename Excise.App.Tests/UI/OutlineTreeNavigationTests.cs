using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Headless GUI tests for the outline (table-of-contents) panel.
/// User reported "nothing happens when I click on the title of chapters
/// in the toc" — these tests drive the same code path the GUI uses
/// (MainWindow + OutlineTree) and assert that selecting a node
/// navigates the viewer.
/// </summary>
[Collection("AvaloniaTests")]
public class OutlineTreeNavigationTests
{
    private readonly ITestOutputHelper _out;
    public OutlineTreeNavigationTests(ITestOutputHelper o) { _out = o; }

    private const string PragmaticBook =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    [FixedAvaloniaFact]
    public async Task OutlineTree_PopulatesAfterDocumentLoad()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new Window
        {
            DataContext = vm,
            Width = 1280,
            Height = 900,
            Content = new MainWindow().Content as Control
        };
        // Simpler: instantiate MainWindow with the VM as DataContext.
        // The above just borrows the content tree; we want the real
        // window so its xaml-defined named controls are wired up.
        window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(100);

        await vm.LoadDocumentAsync(PragmaticBook);

        // Outline parses synchronously during LoadDocumentAsync.
        vm.OutlineNodes.Should().NotBeEmpty(
            "the Pragmatic book ships with a /Outlines tree");
        _out.WriteLine($"OutlineNodes top-level count: {vm.OutlineNodes.Count}");
        foreach (var n in vm.OutlineNodes.Take(5))
            _out.WriteLine($"  '{n.Title}' → page {n.PageNumber} ({n.Children.Count} children)");

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull("OutlineTree must exist in MainWindow");
        tree!.ItemsSource.Should().Be(vm.OutlineNodes,
            "the TreeView's ItemsSource binding should resolve to OutlineNodes");
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_SettingSelectedItem_NavigatesToPage()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(100);

        await vm.LoadDocumentAsync(PragmaticBook);
        await Task.Delay(100);

        // Find a top-level outline node with a real destination.
        var nav = vm.OutlineNodes
            .FirstOrDefault(n => n.PageNumber.HasValue && n.PageNumber.Value > 1);
        nav.Should().NotBeNull(
            "we need at least one outline entry that points beyond page 1 to verify navigation");

        var initialPage = vm.CurrentPageIndex;
        _out.WriteLine($"Initial page: {initialPage + 1}, target: {nav!.PageNumber}");

        // Set the SelectedItem the way the TwoWay binding would when the
        // user clicks a row. This is the *exact* path the click should
        // take — if this doesn't navigate, the click code is broken even
        // before pointer hit-test gets involved.
        vm.SelectedOutlineNode = nav;
        await Task.Delay(100);

        vm.CurrentPageIndex.Should().Be(nav.PageNumber!.Value - 1,
            $"selecting outline node '{nav.Title}' must set CurrentPageIndex " +
            $"to its destination ({nav.PageNumber} → index {nav.PageNumber - 1})");
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_TreeViewSelectedItemSetter_NavigatesToPage()
    {
        // Same end goal as the previous test but exercises the binding
        // through the actual TreeView control: assign to TreeView.SelectedItem
        // → the TwoWay binding should propagate to vm.SelectedOutlineNode →
        // its setter calls JumpToOutline. Catches binding-mode regressions.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(100);

        await vm.LoadDocumentAsync(PragmaticBook);
        await Task.Delay(100);

        var nav = vm.OutlineNodes
            .FirstOrDefault(n => n.PageNumber.HasValue && n.PageNumber.Value > 1);
        nav.Should().NotBeNull();

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull();

        _out.WriteLine($"Setting TreeView.SelectedItem = '{nav!.Title}' → expect page {nav.PageNumber}");
        await Dispatcher.UIThread.InvokeAsync(() => { tree!.SelectedItem = nav; });
        await Task.Delay(200);

        vm.SelectedOutlineNode.Should().BeSameAs(nav,
            "TwoWay binding must push the selection back into VM.SelectedOutlineNode");
        vm.CurrentPageIndex.Should().Be(nav.PageNumber!.Value - 1,
            $"after selecting via TreeView, CurrentPageIndex must equal node.PageNumber - 1");
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_PointerClickOnRow_TriggersNavigation()
    {
        // The diagnostic test: simulate the actual pointer click the user
        // makes. If THIS doesn't navigate, the bug is in the click→
        // selection→VM path and the prior tests only proved the binding
        // works post-selection. If THIS does navigate, the issue is
        // somewhere else (real input, hit-testing, layout) and we need
        // to look elsewhere.
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(PragmaticBook);
        // Let layout settle so TreeView realises its containers and we
        // can compute the click point.
        for (int i = 0; i < 10; i++) { await Task.Delay(100); window.UpdateLayout(); }

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull();

        // Diagnostic dump.
        _out.WriteLine($"TreeView Bounds={tree!.Bounds} IsVisible={tree.IsVisible}");
        _out.WriteLine($"  ItemsSource null? {tree.ItemsSource == null}");
        var sourceCount = (tree.ItemsSource as System.Collections.IEnumerable)
            ?.Cast<object>().Count() ?? -1;
        _out.WriteLine($"  ItemsSource count: {sourceCount}");

        // Walk parents to find the first one with non-zero size — narrows
        // down which container is collapsing.
        Control? walk = tree;
        for (int i = 0; i < 12 && walk != null; i++)
        {
            _out.WriteLine($"  ancestor[{i}] {walk.GetType().Name} Bounds={walk.Bounds} " +
                           $"IsVisible={walk.IsVisible} Name='{walk.Name}'");
            walk = walk.Parent as Control;
        }

        // Force container generation if it hasn't happened yet.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tree.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tree.Arrange(new Rect(tree.Bounds.Size));
            window.UpdateLayout();
        });
        await Task.Delay(200);

        // Probe ContainerFromIndex to coax realization.
        var c0 = tree.ContainerFromIndex(0);
        _out.WriteLine($"ContainerFromIndex(0) = {c0?.GetType().Name ?? "null"}");

        var allItems = tree.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        _out.WriteLine($"Realised TreeViewItems: {allItems.Count}");
        var allDescendants = tree.GetVisualDescendants().ToList();
        _out.WriteLine($"Total visual descendants: {allDescendants.Count}");
        foreach (var d in allDescendants.Take(15))
            _out.WriteLine($"  {d.GetType().Name} bounds={d.Bounds}");
        foreach (var item in allItems.Take(8))
        {
            var dc = item.DataContext as OutlineNode;
            _out.WriteLine($"  bounds={item.Bounds} dc='{dc?.Title}' page={dc?.PageNumber}");
        }

        // Pick the TreeViewItem whose DataContext has the most distinctive
        // page (not page 1, far enough from page 1 to detect navigation).
        // Find the OutlineNode → TreeViewItem mapping by DataContext rather
        // than visual position so a row-height drift doesn't fool us.
        var targetNode = vm.OutlineNodes.FirstOrDefault(n =>
            n.PageNumber.HasValue && n.PageNumber.Value > 5);
        targetNode.Should().NotBeNull("Pragmatic book has outline entries past page 5");

        var targetItem = allItems.First(it =>
            ReferenceEquals(it.DataContext, targetNode));
        _out.WriteLine($"Clicking item '{targetNode!.Title}' at bounds {targetItem.Bounds}");

        // Click well inside the item's leftmost portion, in item-local
        // coords (avoids the right-side overflow into clipped space).
        var pointInItem = new Point(40, targetItem.Bounds.Height / 2);
        var pointInWindow = targetItem.TranslatePoint(pointInItem, window) ?? default;
        _out.WriteLine($"Click point in window coords: {pointInWindow}");

        // Diagnose what's hit at that point before clicking.
        var hit = window.InputHitTest(pointInWindow);
        _out.WriteLine($"InputHitTest at click point = {hit?.GetType().Name} " +
                       $"DC={(hit as Control)?.DataContext?.GetType().Name}");

        var initialPage = vm.CurrentPageIndex;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(pointInWindow, MouseButton.Left);
            window.MouseUp(pointInWindow, MouseButton.Left);
        });
        for (int i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        _out.WriteLine($"After click: vm.SelectedOutlineNode='{vm.SelectedOutlineNode?.Title}', " +
                       $"CurrentPageIndex={vm.CurrentPageIndex + 1} (was {initialPage + 1})");

        vm.CurrentPageIndex.Should().Be(targetNode.PageNumber!.Value - 1,
            $"clicking '{targetNode.Title}' must navigate to its destination " +
            $"(page {targetNode.PageNumber}); ended up at page {vm.CurrentPageIndex + 1}");
    }

    private static T? FindNamedDescendant<T>(Control root, string name) where T : Control
    {
        if (root.Name == name && root is T t) return t;
        if (root is Panel p)
        {
            foreach (var child in p.Children)
                if (child is Control c)
                {
                    var hit = FindNamedDescendant<T>(c, name);
                    if (hit != null) return hit;
                }
        }
        if (root is Decorator d && d.Child is Control dc)
        {
            var hit = FindNamedDescendant<T>(dc, name);
            if (hit != null) return hit;
        }
        if (root is ContentControl cc && cc.Content is Control ccc)
        {
            var hit = FindNamedDescendant<T>(ccc, name);
            if (hit != null) return hit;
        }
        return root.FindControl<T>(name);
    }
}
