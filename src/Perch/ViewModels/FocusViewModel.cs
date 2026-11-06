using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Services;

namespace Perch.ViewModels;

public enum FocusPhase { Focus, ShortBreak, LongBreak }

// Pomodoro: focus, short break, and a long break after every fourth focus round.
public sealed class FocusViewModel : Observable
{
    // Ring circumference in stroke-thickness units for a 132 px ellipse with an 8 px stroke: π × 124 / 8.
    private const double RingLength = 48.69;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Store store;
    private readonly AlertQueue alerts;
    private DateTime? endsAt;
    private TimeSpan left;
    private int rounds;

    public FocusPhase Phase { get; private set; } = FocusPhase.Focus;
    public bool Running => endsAt != null;
    public bool IsFocus => Phase == FocusPhase.Focus;
    public bool IsShortBreak => Phase == FocusPhase.ShortBreak;
    public bool IsLongBreak => Phase == FocusPhase.LongBreak;
    public string PhaseLabel => Loc.T(Phase switch { FocusPhase.Focus => "Nav.Focus", FocusPhase.ShortBreak => "Focus.Short", _ => "Focus.Long" });
    public string Remaining
    {
        get
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }
    public double Progress => Math.Clamp(1 - left.TotalSeconds / Duration(Phase).TotalSeconds, 0, 1);
    public DoubleCollection Ring => new() { Math.Max(0.001, Progress * RingLength), 100 };
    public string PlayGlyph => Running ? "" : "";
    public string RoundText => Loc.F("Focus.Round", rounds % 4 + 1);
    public int TodaySessions => Today()?.Sessions ?? 0;
    public string TodayMinutes => Loc.F("Focus.Minutes", Today()?.Minutes ?? 0);

    public ICommand Toggle { get; }
    public ICommand Reset { get; }
    public ICommand Skip { get; }
    public ICommand Select { get; }

    public FocusViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        left = Duration(Phase);
        tick.Tick += (_, _) => Update();
        Toggle = new RelayCommand(_ => { if (Running) Pause(); else Start(); });
        Reset = new RelayCommand(_ => { Pause(); left = Duration(Phase); Changed(string.Empty); });
        Skip = new RelayCommand(_ => Complete(false));
        Select = new RelayCommand(p =>
        {
            Pause();
            Phase = p switch { "short" => FocusPhase.ShortBreak, "long" => FocusPhase.LongBreak, _ => FocusPhase.Focus };
            left = Duration(Phase);
            Changed(string.Empty);
        });
    }

    private Settings S => store.State.Settings;

    private TimeSpan Duration(FocusPhase phase) => TimeSpan.FromMinutes(Math.Clamp(phase switch
    {
        FocusPhase.Focus => S.FocusMinutes,
        FocusPhase.ShortBreak => S.ShortBreakMinutes,
        _ => S.LongBreakMinutes,
    }, 1, 180));

    private FocusDay? Today() => store.State.Focus.FirstOrDefault(d => d.Date == DateTime.Today.ToString("yyyy-MM-dd"));

    public void Relocalize() => Changed(string.Empty);

    // Durations changed in settings take effect when the timer isn't running.
    public void SettingsChanged()
    {
        if (Running) return;
        left = Duration(Phase);
        Changed(string.Empty);
    }

    private void Start()
    {
        if (left <= TimeSpan.Zero) left = Duration(Phase);
        endsAt = DateTime.Now + left;
        tick.Start();
        Changed(string.Empty);
    }

    private void Pause()
    {
        if (endsAt is { } end) left = end - DateTime.Now;
        endsAt = null;
        tick.Stop();
        Changed(string.Empty);
    }

    private void Update()
    {
        if (endsAt is not { } end) return;
        left = end - DateTime.Now;
        if (left <= TimeSpan.Zero) { Complete(true); return; }
        Changed(nameof(Remaining)); Changed(nameof(Progress)); Changed(nameof(Ring));
    }

    private void Complete(bool finished)
    {
        tick.Stop();
        endsAt = null;
        if (Phase == FocusPhase.Focus)
        {
            if (finished)
            {
                rounds++;
                Record(Duration(Phase));
                alerts.Show(AlertQueue.Timer, Loc.T(rounds % 4 == 0 ? "Focus.DoneFour" : "Focus.DoneOne"));
            }
            Phase = rounds > 0 && rounds % 4 == 0 && finished ? FocusPhase.LongBreak : FocusPhase.ShortBreak;
        }
        else
        {
            if (finished) alerts.Show(AlertQueue.Timer, Loc.T("Focus.BreakOver"));
            Phase = FocusPhase.Focus;
        }
        left = Duration(Phase);
        if (finished && S.FocusAutoContinue) Start();
        else Changed(string.Empty);
    }

    private void Record(TimeSpan duration)
    {
        var day = Today();
        if (day == null)
        {
            day = new FocusDay { Date = DateTime.Today.ToString("yyyy-MM-dd") };
            store.State.Focus.Add(day);
            if (store.State.Focus.Count > 90) store.State.Focus.RemoveAt(0);
        }
        day.Sessions++;
        day.Minutes += (int)Math.Round(duration.TotalMinutes);
        store.Touch();
    }
}
