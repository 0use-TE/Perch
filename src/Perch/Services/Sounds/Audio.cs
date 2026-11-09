namespace Perch.Services.Sounds;

// What a sound is for. Each kind has its own sound in the settings.
public enum SoundKind { None, Reminder, Pomodoro, Alarm }

// Plays a clip of WAV data (and can repeat it until stopped). Windows has one; another platform adds its own.
public interface ISoundPlayer
{
    void Play(byte[] wav, bool loop);
    void Stop();
    // The system's own alert sound.
    void SystemBeep();
}

// The one place that decides whether and what to play, from the user's settings.
public static class Audio
{
    private const double LoopPause = 0.7; // seconds of silence between repeats of a looping sound

    public static ISoundPlayer? Player { get; set; }
    public static Settings? Settings { get; set; }

    public static string IdFor(SoundKind kind, string? own = null)
    {
        if (!string.IsNullOrEmpty(own)) return own;
        var s = Settings;
        return kind switch { SoundKind.Pomodoro => s?.SoundPomodoro ?? "bell", SoundKind.Alarm => s?.SoundAlarm ?? "alarm", _ => s?.SoundReminder ?? "gentle" };
    }

    // A reminder's sound, once. Honours the master switch ("play a sound with alerts") for everything but the alarm.
    public static void Play(SoundKind kind, string? own = null)
    {
        if (kind == SoundKind.None || Settings is not { } s) return;
        if (kind != SoundKind.Alarm && !s.AlertSound) return;
        Start(IdFor(kind, own), loop: false);
    }

    // An alarm that keeps ringing until it is stopped.
    public static void Ring(string? own = null) => Start(IdFor(SoundKind.Alarm, own), loop: true);

    // Plays a sound from a list so the user can hear it, whatever the master switch says.
    public static void Preview(string id) => Start(id, loop: false);

    public static void Stop() => Player?.Stop();

    private static void Start(string id, bool loop)
    {
        if (Player is not { } player || id == SoundBank.Off) return;
        var volume = (Settings?.SoundVolume ?? 70) / 100.0;
        if (volume <= 0) return;
        if (id == SoundBank.System) { player.SystemBeep(); return; }
        if (!SoundBank.Exists(id)) id = "gentle";
        player.Play(SoundBank.Wav(id, volume, loop ? LoopPause : 0), loop);
    }
}
