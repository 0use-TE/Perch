using Perch.Services;

namespace Perch.ViewModels;

// The dinosaur game's page: remembers the best score and tells the game when the page is on screen.
public sealed class DinoViewModel(Store store) : Observable
{
    private bool active;

    public int Best
    {
        get => store.State.Settings.DinoBest;
        set { if (value == store.State.Settings.DinoBest) return; store.State.Settings.DinoBest = value; store.Touch(); Changed(); }
    }

    // True while the island is open on this page; the game runs only then (it pauses itself otherwise).
    public bool Active { get => active; set => Set(ref active, value); }

    // The player clicked or pressed a key in the game: the island needs to keep open and accept the keyboard.
    public event Action? Engaged;
    public void Engage() => Engaged?.Invoke();
}
