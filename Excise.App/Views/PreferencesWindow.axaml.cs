using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Excise.App.ViewModels;
using System;

namespace Excise.App.Views;

public partial class PreferencesWindow : Window
{
    /// <summary>Memory readout refresh period.</summary>
    internal static readonly TimeSpan MemoryReadoutInterval = TimeSpan.FromSeconds(1);

    // Exists only between Opened and Closed. A timer that outlived the dialog
    // would wake an idle app every second, undoing #1462.
    private DispatcherTimer? _memoryReadoutTimer;

    public PreferencesWindow()
    {
        InitializeComponent();

        // Wire up commands to close the window when DataContext is set
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => StartMemoryReadout();
        Closed += (_, _) => StopMemoryReadout();
    }

    /// <summary>True while the memory readout timer exists (tests).</summary>
    internal bool HasMemoryReadoutTimer => _memoryReadoutTimer != null;

    private void StartMemoryReadout()
    {
        if (_memoryReadoutTimer != null)
            return;
        (DataContext as PreferencesViewModel)?.RefreshMemoryReadout();
        _memoryReadoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = MemoryReadoutInterval };
        _memoryReadoutTimer.Tick += OnMemoryReadoutTick;
        _memoryReadoutTimer.Start();
    }

    private void StopMemoryReadout()
    {
        if (_memoryReadoutTimer == null)
            return;
        _memoryReadoutTimer.Stop();
        _memoryReadoutTimer.Tick -= OnMemoryReadoutTick;
        _memoryReadoutTimer = null;
    }

    private void OnMemoryReadoutTick(object? sender, EventArgs e) =>
        (DataContext as PreferencesViewModel)?.RefreshMemoryReadout();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PreferencesViewModel viewModel)
        {
            viewModel.SaveCommand.Subscribe(_ => Close());
            viewModel.CancelCommand.Subscribe(_ => Close());
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
