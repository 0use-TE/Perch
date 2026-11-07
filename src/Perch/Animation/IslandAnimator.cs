using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using System.Runtime.InteropServices;

namespace Perch.Animation;

// Width and height each have a spring. Growing: width leads with a slight overshoot, height follows.
// Shrinking: height leads without bounce, width follows. Retargeting preserves value and velocity.
public sealed class IslandAnimator : IDisposable
{
    private readonly Stopwatch clock = new();
    private Spring width = new() { Response = 0.38, Damping = 0.78 };
    private Spring height = new() { Response = 0.46, Damping = 0.86 };
    private double last;
    private bool running;
    // Frames are driven by the render loop of this window, so it must be set before the first MoveTo.
    public TopLevel? Host { get; set; }
    public bool ReducedMotion { get; set; }
    public bool Expanding { get; private set; }
    public bool IsRunning => running;
    // Seconds covered by the latest frame; lets the view rate-limit content fades.
    public double StepSeconds { get; private set; }
    // width progress, height progress, settled
    public event Action<double, double, bool>? Frame;

    // A short fade and slide when a page opens.
    public void Reveal(Control page, TranslateTransform offset)
    {
        if (ReducedMotion) { page.Opacity = 1; offset.Y = 0; return; }
        Tween(160, t => { page.Opacity = t; offset.Y = 5 * (1 - (1 - (1 - t) * (1 - t))); });
    }

    // Runs step(0..1) over the given milliseconds, one call per rendered frame.
    public void Tween(double milliseconds, Action<double> step)
    {
        if (Host == null || milliseconds <= 0) { step(1); return; }
        var timer = Stopwatch.StartNew();
        step(0);
        void Next(TimeSpan _)
        {
            var t = Math.Clamp(timer.Elapsed.TotalMilliseconds / milliseconds, 0, 1);
            step(t);
            if (t < 1) Host!.RequestAnimationFrame(Next);
        }
        Host.RequestAnimationFrame(Next);
    }

    // Targets are progress between the compact (0) and expanded (1) sizes; the wide compact island uses a width between them.
    public void MoveTo(double widthTarget, double heightTarget)
    {
        Expanding = heightTarget >= 0.5;
        (width.Response, width.Damping) = widthTarget > width.Value ? (0.38, 0.78) : (0.34, 1.0);
        (height.Response, height.Damping) = heightTarget > height.Value ? (0.46, 0.86) : (0.28, 1.0);
        width.Target = widthTarget;
        height.Target = heightTarget;
        if (ReducedMotion || Host == null)
        {
            Stop(); width.Snap(); height.Snap(); StepSeconds = 0;
            Frame?.Invoke(width.Value, height.Value, true);
            return;
        }
        if (running) return;
        running = true; last = 0; clock.Restart();
        // Windows ticks every ~15.6 ms by default, which halves the frame rate to ~32 fps; ask for 1 ms while animating.
        timeBeginPeriod(1);
        Host.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        if (!running) return;
        Render();
        if (running) Host?.RequestAnimationFrame(OnFrame);
    }

    private void Render()
    {
        var now = clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - last, 0.032); last = now;
        StepSeconds = dt;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
        for (var i = 0; i < steps; i++) { var h = dt / steps; width.Step(h); height.Step(h); }
        var settled = width.Settled && height.Settled;
        if (settled) { width.Snap(); height.Snap(); Stop(); }
        Frame?.Invoke(width.Value, height.Value, settled);
    }

    private void Stop()
    {
        if (running) timeEndPeriod(1);
        clock.Stop(); running = false;
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    public void Dispose() => Stop();
}
