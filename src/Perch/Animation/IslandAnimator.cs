using System.Diagnostics;
using System.Windows.Media;
using System.Windows;
using System.Windows.Media.Animation;

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
    public bool ReducedMotion { get; set; }
    public bool Expanding { get; private set; }
    public bool IsRunning => running;
    // Seconds covered by the latest frame; lets the view rate-limit content fades.
    public double StepSeconds { get; private set; }
    // width progress, height progress, settled
    public event Action<double, double, bool>? Frame;
    public void Reveal(UIElement page, TranslateTransform offset)
    {
        var duration = TimeSpan.FromMilliseconds(ReducedMotion ? 0 : 160);
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    // Targets are progress between the compact (0) and expanded (1) sizes; the wide compact island uses a width between them.
    public void MoveTo(double widthTarget, double heightTarget)
    {
        Expanding = heightTarget >= 0.5;
        (width.Response, width.Damping) = widthTarget > width.Value ? (0.38, 0.78) : (0.34, 1.0);
        (height.Response, height.Damping) = heightTarget > height.Value ? (0.46, 0.86) : (0.28, 1.0);
        width.Target = widthTarget;
        height.Target = heightTarget;
        if (ReducedMotion)
        {
            Stop(); width.Snap(); height.Snap(); StepSeconds = 0;
            Frame?.Invoke(width.Value, height.Value, true);
            return;
        }
        if (running) return;
        running = true; last = 0; clock.Restart(); CompositionTarget.Rendering += Render;
    }
    private void Render(object? sender, EventArgs e)
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
    private void Stop() { CompositionTarget.Rendering -= Render; clock.Stop(); running = false; }
    public void Dispose() => Stop();
}
