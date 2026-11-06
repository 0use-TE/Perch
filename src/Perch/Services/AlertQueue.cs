using Avalonia.Threading;

namespace Perch.Services;

public sealed record IslandAlert(string Glyph, string Text);

// Short messages the island shows one at a time: widened compact island when closed, a banner when open.
public sealed class AlertQueue
{
    public const string Bell = "", Warning = "", Moon = "", Bolt = "", Timer = "", Done = "";
    private readonly Queue<IslandAlert> pending = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public IslandAlert? Current { get; private set; }
    public bool Sound { get; set; } = true;
    public event Action? Changed;

    public AlertQueue() => timer.Tick += (_, _) => Next();

    public void Show(string glyph, string text)
    {
        pending.Enqueue(new(glyph, text));
        if (Current == null) Next();
    }

    public void Dismiss() => Next();

    private void Next()
    {
        timer.Stop();
        Current = pending.Count > 0 ? pending.Dequeue() : null;
        if (Current != null)
        {
            if (Sound) Platform.Windows.Beep.Play();
            timer.Start();
        }
        Changed?.Invoke();
    }
}
