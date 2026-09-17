using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Excise.App.ViewModels;

namespace Excise.App.Views;

/// <summary>
/// The in-app document tab strip (#1554). Everything a tab does is a command
/// on <see cref="DocumentTabViewModel"/>; this code-behind holds only the two
/// pointer gestures a command cannot express: middle-click to close, and drag
/// to reorder.
/// </summary>
public partial class DocumentTabStrip : UserControl
{
    // A press moves the tab only after the pointer has travelled this far, so
    // an ordinary click never reorders.
    private const double DragThreshold = 6;

    private DocumentTabViewModel? _pressedTab;
    private Point _pressPosition;
    private bool _dragging;

    public DocumentTabStrip()
    {
        InitializeComponent();

        // Tunnel + handledEventsToo: the tab is a Button, which marks pointer
        // presses handled (the same reason MainWindow's thumbnail strip
        // registers this way, #827).
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private DocumentTabsViewModel? Tabs => DataContext as DocumentTabsViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tab = TabUnder(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (tab == null)
            return;

        if (properties.IsLeftButtonPressed)
        {
            _pressedTab = tab;
            _pressPosition = e.GetPosition(this);
            _dragging = false;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab == null || Tabs is not { } tabs)
            return;

        var position = e.GetPosition(this);
        if (!_dragging)
        {
            var delta = position - _pressPosition;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                return;
            _dragging = true;
        }

        var over = TabUnder(e);
        if (over == null || ReferenceEquals(over, _pressedTab))
            return;

        tabs.Move(tabs.Tabs.IndexOf(_pressedTab), tabs.Tabs.IndexOf(over));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _dragging;
        _pressedTab = null;
        _dragging = false;

        if (wasDragging)
        {
            // The drag was the gesture; the release must not also select.
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Middle && TabUnder(e) is { } tab)
        {
            e.Handled = true;
            tab.CloseCommand.Execute().Subscribe();
        }
    }

    private DocumentTabViewModel? TabUnder(PointerEventArgs e)
    {
        var hit = this.InputHitTest(e.GetPosition(this)) as Visual;
        while (hit != null)
        {
            if (hit is Control { DataContext: DocumentTabViewModel tab })
                return tab;
            hit = hit.GetVisualParent();
        }
        return null;
    }
}
