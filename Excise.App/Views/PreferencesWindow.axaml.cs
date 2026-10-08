using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Excise.App.ViewModels;
using System;

namespace Excise.App.Views;

internal partial class PreferencesWindow : Window
{
    /// <summary>Memory readout refresh period.</summary>
    internal static readonly TimeSpan MemoryReadoutInterval = TimeSpan.FromSeconds(1);

    // Exists only between Opened and Closed. A timer that outlived the dialog
    // would wake an idle app every second, undoing #1462.
    private DispatcherTimer? _memoryReadoutTimer;

    /// <summary>How long a change settles before it applies (#2000).</summary>
    internal static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(300);

    // #2000: changes apply as they are made. A burst (typing a number, stepping a
    // spinner) applies once, after it settles; closing applies whatever is pending
    // so the last change is never lost. Stopped on close like the readout timer.
    private DispatcherTimer? _applyTimer;
    private PreferencesViewModel? _subscribed;

    public PreferencesWindow()
    {
        InitializeComponent();

        // Wire up commands to close the window when DataContext is set
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => StartMemoryReadout();
        Closing += (_, _) => FlushAndStopApplying();
        Closed += (_, _) => StopMemoryReadout();
    }

    /// <summary>True while a change waits to apply (tests).</summary>
    internal bool HasPendingApply => _applyTimer?.IsEnabled == true;

    private void OnPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (PreferencesViewModel.IsReadout(e.PropertyName))
            return;
        _applyTimer ??= CreateApplyTimer();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private DispatcherTimer CreateApplyTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ApplyDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _subscribed?.ApplyNow();
        };
        return timer;
    }

    /// <summary>
    /// The guarantee (#2000): whatever the window shows when it closes is what
    /// applies and persists, whether or not a control raised a change.
    /// </summary>
    private void FlushAndStopApplying()
    {
        _applyTimer?.Stop();
        _applyTimer = null;
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged -= OnPreferenceChanged;
            _subscribed.ApplyNow();
            _subscribed = null;
        }
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
        if (_subscribed != null)
            _subscribed.PropertyChanged -= OnPreferenceChanged;
        _subscribed = DataContext as PreferencesViewModel;
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged += OnPreferenceChanged;
            _subscribed.CloseRequested += (_, _) => Close();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
