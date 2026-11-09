using Avalonia.Threading;

namespace Perch.Interaction;

public enum InteractionState { Compact, Expanded, Editing, Pinned }
public enum ContentState { Idle, Media, TemporaryAlert }

public sealed class IslandController : IDisposable
{
    private readonly DispatcherTimer delay = new();
    private bool hovered, editing, pinned;
    public bool HoverEnabled { get; set; } = true;
    public bool CollapseOnLeave { get; set; } = true;
    public int ExpandDelay { get; set; } = 100;
    public int CollapseDelay { get; set; } = 400;
    public InteractionState State { get; private set; }
    public ContentState Content { get; private set; } = ContentState.Idle;
    public event Action<bool>? ExpansionChanged;
    public IslandController() => delay.Tick += OnDelay;
    public void Pointer(bool inside)
    {
        hovered = inside;
        Dbg.Log($"Pointer({inside}) editing={editing} pinned={pinned} state={State}");
        delay.Stop();
        if (editing || pinned) return;
        if (inside && !HoverEnabled) return;
        if (!inside && !CollapseOnLeave) return;
        delay.Interval = TimeSpan.FromMilliseconds(inside ? ExpandDelay : CollapseDelay);
        delay.Start();
    }
    private void OnDelay(object? sender, EventArgs e) { delay.Stop(); SetState(hovered ? InteractionState.Expanded : InteractionState.Compact); }
    public void TogglePin() { pinned = !pinned; Reconcile(); }
    public void Edit(bool value) { Dbg.Log($"Edit({value}) hovered={hovered}"); editing = value; Reconcile(); }
    // Explicit close (click outside the island): also ends editing, but never overrides the pin.
    public void Collapse()
    {
        Dbg.Log("Collapse()");
        delay.Stop();
        editing = false;
        if (!pinned) SetState(InteractionState.Compact);
    }
    public void Open() { Dbg.Log("Open()"); SetState(pinned ? InteractionState.Pinned : InteractionState.Expanded); }
    private void Reconcile()
    {
        delay.Stop();
        if (pinned) SetState(InteractionState.Pinned);
        else if (editing) SetState(InteractionState.Editing);
        else if (hovered || (!CollapseOnLeave && State != InteractionState.Compact)) SetState(InteractionState.Expanded);
        else Pointer(false);
    }
    private void SetState(InteractionState value) { Dbg.Log($"SetState({value}) hovered={hovered}"); State = value; ExpansionChanged?.Invoke(value != InteractionState.Compact); }
    public void Dispose() { delay.Stop(); delay.Tick -= OnDelay; }
}
