using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Excise.App.Tests.UI;

/// <summary>
/// Closes every window a test opened, even when the test fails (#706).
///
/// THE BUG THIS FIXES
/// ------------------
/// `MouseInputTests` called `window.Show()` thirteen times and `Close()` zero
/// times; `PointerInteractionTests`, eight and zero. xUnit builds a fresh
/// instance of a test class per test, but the Avalonia application is
/// process-wide, so every window those tests showed stayed open for the rest of
/// the run and accumulated.
///
/// That is the order-dependent state #706 described but did not identify:
/// pointer and hover routing, focus, and topmost-window resolution all depend
/// on which windows are open. A test that passes alone can fail after its
/// siblings have left a dozen windows behind — which is exactly the reported
/// symptom, and why `ClickOnLinkAnnotation` and `HoverOverLinkAnnotation`
/// failed in class context and passed in isolation.
///
/// WHY A TRACKER AND NOT JUST window.Close() AT THE END OF EACH TEST
/// -----------------------------------------------------------------
/// Some classes already close inline, and it does not hold: a `Close()` on the
/// last line is skipped whenever the test fails, so the very runs that leak
/// windows are the ones that most need cleaning up. Cleanup has to be in a
/// `finally`, which for xUnit means `IDisposable` on the class.
///
/// Usage:
///     public sealed class MyTests : IDisposable
///     {
///         private readonly ShownWindowTracker _windows = new();
///         public void Dispose() => _windows.Dispose();
///         ...
///         _windows.Show(window);   // instead of window.Show()
///     }
/// </summary>
public sealed class ShownWindowTracker : IDisposable
{
    private readonly List<Window> _shown = new();

    /// <summary>Shows the window and records it for cleanup.</summary>
    public T Show<T>(T window) where T : Window
    {
        window.Show();
        _shown.Add(window);
        return window;
    }

    public void Dispose()
    {
        // Closing must happen on the UI thread. Tests may dispose from either,
        // so route explicitly rather than assuming.
        void CloseAll()
        {
            for (int i = _shown.Count - 1; i >= 0; i--)
            {
                try { _shown[i].Close(); }
                catch { /* a window the test already closed, or a torn-down app */ }
            }
            _shown.Clear();
        }

        if (Dispatcher.UIThread.CheckAccess())
            CloseAll();
        else
            Dispatcher.UIThread.Invoke(CloseAll);
    }
}
