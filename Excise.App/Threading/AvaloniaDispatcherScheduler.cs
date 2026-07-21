using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace Excise.App.Threading;

/// <summary>
/// Rx scheduler that runs work on the Avalonia UI thread. Vendored (adapted
/// from ReactiveUI.Avalonia's MIT-licensed <c>AvaloniaScheduler</c>) so the
/// app can drop the ReactiveUI.Avalonia package — its only load-bearing
/// contribution here was wiring <c>RxApp.MainThreadScheduler</c>, and the
/// package ships without trim/AOT annotations, producing the IL2104 roll-up
/// the Native AOT lane had to suppress (#593). The app uses no other part of
/// the integration (no IViewFor, WhenActivated, or ReactiveWindow).
/// </summary>
public sealed class AvaloniaDispatcherScheduler : LocalScheduler
{
    /// <summary>
    /// Work already on the UI thread is run inline up to this depth, matching
    /// the upstream scheduler: unbounded inlining can starve the dispatcher,
    /// while never inlining reorders same-thread schedules around awaits.
    /// </summary>
    private const int MaxReentrantSchedules = 32;

    [ThreadStatic]
    private static int _reentrancyDepth;

    public static AvaloniaDispatcherScheduler Instance { get; } = new();

    private AvaloniaDispatcherScheduler()
    {
    }

    public override IDisposable Schedule<TState>(
        TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dueTime > TimeSpan.Zero)
        {
            var timerDisposable = new MultipleAssignmentDisposable();
            var timer = DispatcherTimer.RunOnce(
                () =>
                {
                    if (!timerDisposable.IsDisposed)
                        timerDisposable.Disposable = action(this, state);
                },
                dueTime);
            timerDisposable.Disposable = timer;
            return timerDisposable;
        }

        if (Dispatcher.UIThread.CheckAccess() && _reentrancyDepth < MaxReentrantSchedules)
        {
            _reentrancyDepth++;
            try
            {
                return action(this, state);
            }
            finally
            {
                _reentrancyDepth--;
            }
        }

        var posted = new MultipleAssignmentDisposable();
        Dispatcher.UIThread.Post(() =>
        {
            if (!posted.IsDisposed)
                posted.Disposable = action(this, state);
        });
        return posted;
    }
}
