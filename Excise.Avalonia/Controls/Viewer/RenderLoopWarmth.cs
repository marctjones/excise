using System;
using System.Diagnostics;
using global::Avalonia;
using global::Avalonia.Media;
using global::Avalonia.Rendering.Composition;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Keeps Avalonia's render loop ticking for a short hold after the reader
/// acts, so the next page turn is not delayed by a display-link restart (#2069).
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 stops its render timer ten ticks after the last composition
/// commit (<c>ServerCompositor.RenderCore</c> returns false, and
/// <c>DefaultRenderLoop</c> sets the timer's <c>Tick</c> to null). On macOS
/// that stops the CVDisplayLink, and the next commit restarts it. Measured
/// on a 60 Hz panel (irs and scan, 20 turns 1.5 s apart, 3-4 runs each):
/// commit to "batch processed" took ~19 ms median from a stopped loop and
/// ~3 ms from a running one; screen first change p50 went from 54-64 ms to
/// 44-51 ms.
/// </para>
/// <para>
/// A zero-size custom visual asks for the next animation frame until the hold
/// expires. That keeps the loop running without committing batches (a
/// keep-alive commit could hold back the page turn's own commit behind
/// <c>MediaContext</c>'s one-batch-in-flight throttle) and without dirtying
/// anything, so the idle ticks render nothing. The hold is armed by input and
/// page changes and lapses on its own: an idle viewer does no work. The first
/// action after a pause longer than <see cref="Hold"/> still pays the restart.
/// </para>
/// </remarks>
internal sealed class RenderLoopWarmth
{
    /// <summary>How long the loop keeps ticking after the last action.</summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    // A message per keystroke or wheel event would be a commit per event; once
    // a hold is running, extending it this often is enough.
    private static readonly long ResendTicks = Stopwatch.Frequency / 2;

    private readonly Visual _host;
    private CompositionCustomVisual? _visual;
    private long _lastSent;

    internal RenderLoopWarmth(Visual host) => _host = host;

    /// <summary>Messages sent to the render thread (tests).</summary>
    internal int MessagesSent { get; private set; }

    /// <summary>Start or extend the hold. UI thread; cheap when called often.</summary>
    internal void Touch()
    {
        long now = Stopwatch.GetTimestamp();
        if (_visual != null && now - _lastSent < ResendTicks)
            return;

        if (_visual == null)
        {
            var compositor = ElementComposition.GetElementVisual(_host)?.Compositor;
            if (compositor == null)
                return; // not attached to a compositor yet
            _visual = compositor.CreateCustomVisual(new Handler());
            ElementComposition.SetElementChildVisual(_host, _visual);
        }

        _lastSent = now;
        MessagesSent++;
        _visual.SendHandlerMessage(Hold);
    }

    /// <summary>Drop the visual; the next <see cref="Touch"/> recreates it on the new compositor.</summary>
    internal void Detach()
    {
        if (_visual == null)
            return;
        ElementComposition.SetElementChildVisual(_host, null);
        _visual = null;
    }

    private sealed class Handler : CompositionCustomVisualHandler
    {
        private TimeSpan _until;

        public override void OnMessage(object message)
        {
            if (message is TimeSpan hold)
            {
                _until = CompositionNow + hold;
                RegisterForNextAnimationFrameUpdate();
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (CompositionNow < _until)
                RegisterForNextAnimationFrameUpdate();
        }

        public override Rect GetRenderBounds() => default;

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
        }
    }
}
