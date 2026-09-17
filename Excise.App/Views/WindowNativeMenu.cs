using Avalonia.Controls;
using Excise.App.ViewModels;
using System.Runtime.CompilerServices;

namespace Excise.App.Views;

/// <summary>
/// #1584: a window's one native menu. A window that shows several sessions
/// (#1554 tabs) keeps this single <see cref="NativeMenu"/> for its whole life
/// and swaps the top-level items to the session it shows.
/// </summary>
/// <remarks>
/// <para>Why not a menu per session: Avalonia.Native 12.1.2's
/// <c>AvaloniaNativeMenuExporter</c> creates the window's native menu proxy
/// once, bound to the first managed menu it receives, and every later
/// <c>NativeMenu.SetMenu</c> reaches <c>__MicroComIAvnMenuProxy.Update</c>,
/// which throws "The menu being updated does not match" unless the menu is
/// that same instance. Setting <c>null</c> does not help: the exporter
/// substitutes a fresh empty menu, which is another instance. The same check
/// applies to every submenu, so an item's <see cref="NativeMenuItem.Menu"/> is
/// never reassigned either.</para>
/// <para>Swapping items is supported: the exporter disposes the native proxies
/// of items that left the menu and creates proxies for items that joined.</para>
/// <para>#1551: each session's items are cached under a weak key, so a closed
/// session is not kept alive by this window unless it is the one shown.</para>
/// </remarks>
internal sealed class WindowNativeMenu
{
    private readonly ConditionalWeakTable<MainWindowViewModel, MacNativeMenuBuilder.SessionMenu> _sessions = new();
    private MacNativeMenuBuilder.SessionMenu? _shown;

    public WindowNativeMenu()
    {
        Menu.NeedsUpdate += (_, _) => _shown?.Refresh();
    }

    /// <summary>The one menu attached to the window.</summary>
    public NativeMenu Menu { get; } = new();

    /// <summary>Show <paramref name="viewModel"/>'s items in <see cref="Menu"/>.</summary>
    public void Show(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var session = _sessions.GetValue(viewModel, MacNativeMenuBuilder.CreateSession);
        if (ReferenceEquals(session, _shown))
            return;

        var items = Menu.Items;
        // One at a time, so each leaving item's Parent is cleared and the
        // session can be shown again later.
        while (items.Count > 0)
            items.RemoveAt(items.Count - 1);
        foreach (var item in session.Items)
            items.Add(item);

        _shown = session;
        session.Refresh();
    }
}
