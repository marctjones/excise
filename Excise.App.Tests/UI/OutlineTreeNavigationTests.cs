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
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Headless GUI tests for the outline (table-of-contents) panel.
/// User reported "nothing happens when I click on the title of chapters
/// in the toc" — these tests drive the same code path the GUI uses
/// (MainWindow + OutlineTree) and assert that selecting a node
/// navigates the viewer.
///
/// #1768: every test here used to return at the top on a book path that was
/// the empty string, so outline click → navigate had NO live test while the
/// class reported four passes. They now run on a synthetic 8-page document
/// with three bookmarks (pages 1, 3, 6), built in the test and owned by it.
/// </summary>
[Collection("AvaloniaTests")]
public class OutlineTreeNavigationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "excise-outline-" + Guid.NewGuid().ToString("N"));

    public OutlineTreeNavigationTests(ITestOutputHelper o) { _out = o; }

    public void Dispose()
    {
        _windows.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An 8-page PDF whose outline points at pages 1, 3 and 6.</summary>
    private string CreateOutlinedPdf()
    {
        Directory.CreateDirectory(_tempDir);
        var plain = Path.Combine(_tempDir, "plain.pdf");
        var outlined = Path.Combine(_tempDir, "outlined.pdf");
        TestPdfGenerator.CreateMultiPagePdf(plain, pageCount: 8);
        using (var document = PdfDocument.Open(plain))
        {
            document.AddOutlineItem("Chapter One", 1);
            document.AddOutlineItem("Chapter Two", 3);
            document.AddOutlineItem("Chapter Three", 6);
            document.Save(outlined);
        }
        return outlined;
    }

    private async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = _windows.Show(new MainWindow { DataContext = vm, Width = 1280, Height = 900 });
        await Task.Delay(100);
        await vm.LoadDocumentAsync(CreateOutlinedPdf());
        await Task.Delay(100);
        return (vm, window);
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_PopulatesAfterDocumentLoad()
    {
        var (vm, window) = await OpenAsync();

        vm.OutlineNodes.Select(n => (n.Title, n.PageNumber)).Should().Equal(
            new (string, int?)[] { ("Chapter One", 1), ("Chapter Two", 3), ("Chapter Three", 6) },
            "the outline must load with each bookmark's /Dest resolved to its 1-based page");

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull("OutlineTree must exist in MainWindow");
        tree!.ItemsSource.Should().Be(vm.OutlineNodes,
            "the TreeView's ItemsSource binding should resolve to OutlineNodes");
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_SettingSelectedItem_NavigatesToPage()
    {
        var (vm, _) = await OpenAsync();

        // A bookmark that points beyond page 1, so navigation is observable.
        var nav = vm.OutlineNodes.First(n => n.PageNumber > 1);
        vm.CurrentPageIndex.Should().Be(0, "the document opens on page 1");

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
        var (vm, window) = await OpenAsync();
        var nav = vm.OutlineNodes.Last();

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull();

        _out.WriteLine($"Setting TreeView.SelectedItem = '{nav.Title}' → expect page {nav.PageNumber}");
        await Dispatcher.UIThread.InvokeAsync(() => { tree!.SelectedItem = nav; });
        await Task.Delay(200);

        vm.SelectedOutlineNode.Should().BeSameAs(nav,
            "TwoWay binding must push the selection back into VM.SelectedOutlineNode");
        vm.CurrentPageIndex.Should().Be(nav.PageNumber!.Value - 1,
            "after selecting via TreeView, CurrentPageIndex must equal node.PageNumber - 1");
    }

    [FixedAvaloniaFact]
    public async Task OutlineTree_PointerClickOnRow_TriggersNavigation()
    {
        // The diagnostic test: simulate the actual pointer click the user
        // makes. The two tests above prove the binding works once a row is
        // SELECTED; this one proves a real click on a realised row selects it.
        var (vm, window) = await OpenAsync();
        for (int i = 0; i < 10; i++) { await Task.Delay(50); window.UpdateLayout(); }

        var tree = FindNamedDescendant<TreeView>(window, "OutlineTree");
        tree.Should().NotBeNull();

        // Force container generation if layout has not realised the rows yet.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tree!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tree.Arrange(new Rect(tree.Bounds.Size));
            window.UpdateLayout();
        });
        await Task.Delay(200);

        var items = tree!.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        _out.WriteLine($"TreeView Bounds={tree.Bounds}; realised TreeViewItems: {items.Count}");

        // Click the row for a bookmark well away from page 1, found by
        // DataContext rather than visual position so row-height drift cannot
        // fool the test.
        var targetNode = vm.OutlineNodes.First(n => n.PageNumber == 6);
        var targetItem = items.First(it => ReferenceEquals(it.DataContext, targetNode));
        _out.WriteLine($"Clicking item '{targetNode.Title}' at bounds {targetItem.Bounds}");

        // Click well inside the item's leftmost portion, in item-local coords.
        var pointInWindow = targetItem.TranslatePoint(new Point(40, targetItem.Bounds.Height / 2), window) ?? default;
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
