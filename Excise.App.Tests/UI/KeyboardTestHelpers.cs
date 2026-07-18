using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Excise.App.Tests.UI;

/// <summary>
/// Test helpers for simulating keyboard input on Avalonia windows.
/// Extensions for Window to send key presses with modifiers and dispatch flushing.
/// </summary>
public static class KeyboardTestHelpers
{
    /// <summary>
    /// Simulate a key press on the window with optional modifiers.
    /// Flushes the dispatcher to allow ReactiveUI commands to process.
    /// </summary>
    public static async Task PressKeyAsync(
        this Window window,
        Key key,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        // Use global::Avalonia.Headless' raw-input path instead of manually raising
        // routed events. The raw path exercises focus, key routing, and
        // TopLevel input processing in the same way as the rest of the
        // headless pointer helpers.
        var physicalKey = ToPhysicalKey(key);
        var keySymbol = ToKeySymbol(key);
        window.KeyPress(key, modifiers, physicalKey, keySymbol);
        window.KeyRelease(key, modifiers, physicalKey, keySymbol);

        // Flush the dispatcher to allow commands to execute
        await FlushDispatcherAsync();
    }

    /// <summary>
    /// Type a text string by sending individual character key presses.
    /// Automatically determines if Shift is needed for special characters.
    /// </summary>
    public static async Task TypeTextAsync(this Window window, string text)
    {
        // global::Avalonia.Headless explicitly documents KeyTextInput as the supported
        // path for TextBox text entry. KeyPress alone routes shortcuts but does
        // not synthesize text composition.
        window.KeyTextInput(text);
        await FlushDispatcherAsync();
    }

    /// <summary>
    /// Send Enter key press and flush dispatcher.
    /// </summary>
    public static async Task PressEnterAsync(this Window window)
    {
        await window.PressKeyAsync(Key.Return);
    }

    /// <summary>
    /// Send Escape key press and flush dispatcher.
    /// </summary>
    public static async Task PressEscapeAsync(this Window window)
    {
        await window.PressKeyAsync(Key.Escape);
    }

    /// <summary>
    /// Flush the Avalonia dispatcher to allow pending operations to complete.
    /// </summary>
    public static async Task FlushDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() => tcs.SetResult(true), DispatcherPriority.Background);
        // Best-effort flush. If the dispatcher is saturated by higher-priority work
        // — e.g. a success-toast auto-dismiss timer scheduled by a save — this
        // Background-priority completion can be starved indefinitely under CI load,
        // which previously hung the whole test host until the 120s blame-hang fired
        // (KeyboardShortcutTests.CtrlS_SavesFile). Bound the wait so a busy dispatcher
        // can't hang the run; Task.Delay fires off the thread-pool timer, not the
        // dispatcher, so it can't itself be starved. (#363)
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    private static PhysicalKey ToPhysicalKey(Key key) => key switch
    {
        Key.A => PhysicalKey.A,
        Key.B => PhysicalKey.B,
        Key.C => PhysicalKey.C,
        Key.D => PhysicalKey.D,
        Key.E => PhysicalKey.E,
        Key.F => PhysicalKey.F,
        Key.G => PhysicalKey.G,
        Key.H => PhysicalKey.H,
        Key.I => PhysicalKey.I,
        Key.J => PhysicalKey.J,
        Key.K => PhysicalKey.K,
        Key.L => PhysicalKey.L,
        Key.M => PhysicalKey.M,
        Key.N => PhysicalKey.N,
        Key.O => PhysicalKey.O,
        Key.P => PhysicalKey.P,
        Key.Q => PhysicalKey.Q,
        Key.R => PhysicalKey.R,
        Key.S => PhysicalKey.S,
        Key.T => PhysicalKey.T,
        Key.U => PhysicalKey.U,
        Key.V => PhysicalKey.V,
        Key.W => PhysicalKey.W,
        Key.X => PhysicalKey.X,
        Key.Y => PhysicalKey.Y,
        Key.Z => PhysicalKey.Z,
        Key.D0 => PhysicalKey.Digit0,
        Key.D1 => PhysicalKey.Digit1,
        Key.D2 => PhysicalKey.Digit2,
        Key.D3 => PhysicalKey.Digit3,
        Key.D4 => PhysicalKey.Digit4,
        Key.D5 => PhysicalKey.Digit5,
        Key.D6 => PhysicalKey.Digit6,
        Key.D7 => PhysicalKey.Digit7,
        Key.D8 => PhysicalKey.Digit8,
        Key.D9 => PhysicalKey.Digit9,
        Key.OemPlus => PhysicalKey.Equal,
        Key.OemMinus => PhysicalKey.Minus,
        Key.OemComma => PhysicalKey.Comma,
        Key.OemPeriod => PhysicalKey.Period,
        Key.OemQuestion => PhysicalKey.Slash,
        Key.OemOpenBrackets => PhysicalKey.BracketLeft,
        Key.OemCloseBrackets => PhysicalKey.BracketRight,
        Key.OemBackslash => PhysicalKey.Backslash,
        Key.OemTilde => PhysicalKey.Backquote,
        Key.OemSemicolon => PhysicalKey.Semicolon,
        Key.OemQuotes => PhysicalKey.Quote,
        Key.PageDown => PhysicalKey.PageDown,
        Key.PageUp => PhysicalKey.PageUp,
        Key.Home => PhysicalKey.Home,
        Key.End => PhysicalKey.End,
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Return => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        Key.F1 => PhysicalKey.F1,
        Key.F3 => PhysicalKey.F3,
        _ => PhysicalKey.None,
    };

    private static string ToKeySymbol(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return key.ToString();
        if (key >= Key.D0 && key <= Key.D9)
            return key.ToString()[1..];

        return key switch
        {
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemBackslash => "\\",
            Key.OemTilde => "`",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            _ => "",
        };
    }
}
