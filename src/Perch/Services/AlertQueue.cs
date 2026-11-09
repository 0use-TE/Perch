using Avalonia.Threading;

namespace Perch.Services;

public sealed record IslandAlert(string Glyph, string Text, Sounds.SoundKind Sound);

// Short messages the island shows one at a time: widened compact island when closed, a banner when open.
public sealed class AlertQueue
{
    public const string Bell = "", Warning = "", Moon = "", Bolt = "", Timer = "", Done = "";
    private readonly Queue<IslandAlert> pending = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public IslandAlert? Current { get; private set; }
    public event Action? Changed;

    public AlertQueue() => timer.Tick += (_, _) => Next();

    // `sound` says which of the user's sounds goes with the message; None is for quiet feedback (a copy, a finished zip).
    public void Show(string glyph, string text, Sounds.SoundKind sound = Sounds.SoundKind.Reminder)
    {
        pending.Enqueue(new(glyph, text, sound));
        if (Current == null) Next();
    }


    private void Next()
    {
        timer.Stop();
        Current = pending.Count > 0 ? pending.Dequeue() : null;
        if (Current != null)
        {
            Sounds.Audio.Play(Current.Sound);
            timer.Start();
        }
        Changed?.Invoke();
    }
}
