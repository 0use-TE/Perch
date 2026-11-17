using System.IO;

namespace Perch.Services.Sounds;

// One choice in a sound list: an id stored in the settings and the key of its display name (or a custom file label).
public sealed record SoundOption(string Id, string NameKey, string? CustomLabel = null)
{
    public string Name => CustomLabel ?? Loc.T(NameKey);
}

// The built-in sounds. They are synthesized cleanly with soft cosine attacks, warm detuned harmonics, and subtle
// room tails, so Perch ships no audio files; every sound is a 16-bit mono PCM clip at 44.1 kHz.
public static class SoundBank
{
    public const int Rate = 44100;
    public const string System = "system", Off = "off", Default = "", FilePrefix = "file:";

    // The ids, in the order the lists show them.
    public static readonly SoundOption[] Options =
    [
        new("gentle", "Sound.gentle"), new("chime", "Sound.chime"), new("bell", "Sound.bell"), new("marimba", "Sound.marimba"),
        new("drop", "Sound.drop"), new("digital", "Sound.digital"), new("pulse", "Sound.pulse"), new("alarm", "Sound.alarm"),
        new(System, "Sound.system"),
    ];

    private static readonly Dictionary<string, float[]> Cache = [];

    // The tick when the island clings to the centre; not one of the choices in the lists.
    public const string Snap = "snap";

    public static bool IsCustom(string? id) => !string.IsNullOrEmpty(id) && (id.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(id));
    public static string? CustomFile(string? id) => string.IsNullOrEmpty(id) ? null : id.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ? id[FilePrefix.Length..] : Path.IsPathRooted(id) ? id : null;
    public static bool Exists(string id) => id == Snap || Options.Any(o => o.Id == id) || (CustomFile(id) is { } p && File.Exists(p));

    // The clip as samples in -1..1, followed by `tail` seconds of silence (so a looping clip breathes between repeats).
    public static float[] Samples(string id, double tail = 0)
    {
        if (!Cache.TryGetValue(id, out var clip)) Cache[id] = clip = Make(id);
        if (tail <= 0) return clip;
        var padded = new float[clip.Length + (int)(tail * Rate)];
        clip.CopyTo(padded, 0);
        return padded;
    }

    // A WAV file in memory, at the given volume (0..1).
    public static byte[] Wav(string id, double volume, double tail = 0)
    {
        var samples = Samples(id, tail);
        var gain = Math.Clamp(volume, 0, 1);
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * gain * 32767, short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(value & 0xFF); data[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        var wav = new byte[44 + data.Length];
        void Text(int at, string s) { for (var i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i]; }
        void Int(int at, int v) { wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8); wav[at + 2] = (byte)(v >> 16); wav[at + 3] = (byte)(v >> 24); }
        void Short(int at, int v) { wav[at] = (byte)v; wav[at + 1] = (byte)(v >> 8); }
        Text(0, "RIFF"); Int(4, 36 + data.Length); Text(8, "WAVE"); Text(12, "fmt "); Int(16, 16);
        Short(20, 1); Short(22, 1); Int(24, Rate); Int(28, Rate * 2); Short(32, 2); Short(34, 16);
        Text(36, "data"); Int(40, data.Length);
        data.CopyTo(wav, 44);
        return wav;
    }

    private static float[] Make(string id)
    {
        switch (id)
        {
            case "chime": // shimmering crystal wind chime (fifth + airy sparkle)
                return Tail(Mix(1.9,
                    (0.00, WarmNote(1046.50, 1.4, 0.42, 0.014, 0.18, 0.06, 0.8)),
                    (0.09, WarmNote(1567.98, 1.4, 0.44, 0.014, 0.16, 0.05, 0.8)),
                    (0.22, WarmNote(1318.51, 1.5, 0.48, 0.014, 0.18, 0.06, 0.8)),
                    (0.32, WarmNote(2093.00, 1.5, 0.50, 0.016, 0.10, 0.03, 0.6))));
            case "bell": // warm singing bowl / soft temple bell with gentle beating
                return Tail(Mix(2.8,
                    (0.00, Bowl(440.0, 2.7)),
                    (0.03, WarmNote(880.0, 1.8, 0.55, 0.025, 0.10, 0.02, 0.5))));
            case "marimba": // soft wooden mallet triad (Fmaj7)
                return Tail(Mix(1.3,
                    (0.00, Mallet(349.23, 0.55, 0.14)),
                    (0.11, Mallet(440.00, 0.55, 0.14)),
                    (0.22, Mallet(523.25, 0.60, 0.15)),
                    (0.33, Mallet(659.25, 0.85, 0.22))));
            case "drop": // round, crisp water droplet (upward bubble resonance)
                return Tail(Mix(0.85,
                    (0.00, Bubble(620, 1180, 0.16, 0.045, 0.9)),
                    (0.22, Bubble(780, 1420, 0.14, 0.040, 0.42))));
            case "digital": // clean modern UI two-note chime (softened, non-harsh)
                return Tail(Mix(0.95,
                    (0.00, WarmNote(659.25, 0.24, 0.09, 0.008, 0.14, 0.04, 0.5)),
                    (0.14, WarmNote(880.00, 0.45, 0.16, 0.008, 0.14, 0.04, 0.5))));
            case "pulse": // deep warm ambient fifth swell
                return Tail(Mix(1.35,
                    (0.00, Swell(220.00, 0.42, 0.12)),
                    (0.00, Swell(329.63, 0.42, 0.12)),
                    (0.45, Swell(246.94, 0.60, 0.16)),
                    (0.45, Swell(369.99, 0.60, 0.16))));
            case Snap: // subtle tactile wooden tick
            {
                var tick = Mix(0.06, (0.0, Mallet(980, 0.045, 0.010)));
                for (var i = 0; i < tick.Length; i++) tick[i] *= 0.45f;
                return tick;
            }
            case "alarm": // pleasant, clear rising marimba-bell alarm motif
                return Tail(Mix(2.2,
                    (0.00, Mallet(523.25, 0.40, 0.14)),
                    (0.18, Mallet(659.25, 0.40, 0.14)),
                    (0.36, Mallet(783.99, 0.40, 0.15)),
                    (0.54, WarmNote(1046.50, 0.65, 0.25, 0.006, 0.22, 0.08, 0.7)),
                    (1.05, Mallet(523.25, 0.40, 0.14)),
                    (1.23, Mallet(659.25, 0.40, 0.14)),
                    (1.41, Mallet(783.99, 0.40, 0.15)),
                    (1.59, WarmNote(1046.50, 0.65, 0.25, 0.006, 0.22, 0.08, 0.7))));
            default: // "gentle": warm Rhodes-like Cmaj9 rising arpeggio
                return Tail(Mix(1.9,
                    (0.00, WarmNote(523.25, 0.90, 0.34, 0.016, 0.18, 0.04, 0.9)),
                    (0.11, WarmNote(659.25, 0.90, 0.34, 0.016, 0.16, 0.04, 0.9)),
                    (0.22, WarmNote(783.99, 0.95, 0.36, 0.016, 0.16, 0.04, 0.9)),
                    (0.33, WarmNote(987.77, 1.05, 0.40, 0.016, 0.14, 0.03, 0.9)),
                    (0.45, WarmNote(1174.66, 1.25, 0.48, 0.018, 0.12, 0.02, 0.9))));
        }
    }

    // Soft cosine attack + warm detuned unison + faster-decaying upper harmonics for an acoustic feel.
    private static float[] WarmNote(double freq, double length, double decay, double attack, double h2, double h3, double detune)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var att = t < attack ? 0.5 * (1 - Math.Cos(Math.PI * t / attack)) : 1.0;
            var release = i > count - 400 ? (count - i) / 400.0 : 1.0;
            var p1 = 2 * Math.PI * (freq - detune) * t;
            var p2 = 2 * Math.PI * (freq + detune) * t;
            var fund = 0.5 * (Math.Sin(p1) + Math.Sin(p2)) * Math.Exp(-t / decay);
            var over = h2 * Math.Sin(2 * 2 * Math.PI * freq * t) * Math.Exp(-t / (decay * 0.55))
                     + h3 * Math.Sin(3 * 2 * Math.PI * freq * t) * Math.Exp(-t / (decay * 0.32));
            wave[i] = (float)((fund + over) * att * release);
        }
        return wave;
    }

    // Singing bowl: warm fundamental with gentle beating partials.
    private static float[] Bowl(double freq, double length)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var att = t < 0.035 ? 0.5 * (1 - Math.Cos(Math.PI * t / 0.035)) : 1.0;
            var release = i > count - 800 ? (count - i) / 800.0 : 1.0;
            var v = Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t / 1.25)
                  + 0.35 * Math.Sin(2 * Math.PI * (freq * 1.0035) * t) * Math.Exp(-t / 1.15)
                  + 0.22 * Math.Sin(2 * Math.PI * (freq * 2.756) * t) * Math.Exp(-t / 0.55)
                  + 0.08 * Math.Sin(2 * Math.PI * (freq * 5.404) * t) * Math.Exp(-t / 0.25);
            wave[i] = (float)(v * att * release);
        }
        return wave;
    }

    // Woody marimba mallet: clean fundamental + short soft woody transient.
    private static float[] Mallet(double freq, double length, double decay)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var att = t < 0.003 ? 0.5 * (1 - Math.Cos(Math.PI * t / 0.003)) : 1.0;
            var v = Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t / decay)
                  + 0.28 * Math.Sin(2 * Math.PI * (freq * 4.0) * t) * Math.Exp(-t / 0.018);
            wave[i] = (float)(v * att);
        }
        return wave;
    }

    // Natural water droplet: rising pitch sweep as the air bubble resonates.
    private static float[] Bubble(double from, double to, double length, double decay, double level)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        double phase = 0;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var freq = to - (to - from) * Math.Exp(-t / 0.028);
            phase += 2 * Math.PI * freq / Rate;
            var env = Math.Min(1, t / 0.003) * Math.Exp(-t / decay);
            wave[i] = (float)(Math.Sin(phase) * env * level);
        }
        return wave;
    }

    // Soft ambient swell.
    private static float[] Swell(double freq, double length, double attack)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Sin(Math.PI * Math.Min(1, t / length));
            var att = t < attack ? 0.5 * (1 - Math.Cos(Math.PI * t / attack)) : 1.0;
            var v = Math.Sin(2 * Math.PI * freq * t) + 0.15 * Math.Sin(4 * Math.PI * freq * t);
            wave[i] = (float)(v * env * att);
        }
        return wave;
    }

    // Subtle multi-tap room reflection so notes sit in space instead of sounding dry.
    private static float[] Tail(float[] dry)
    {
        var wet = new float[dry.Length];
        dry.CopyTo(wet, 0);
        ReadOnlySpan<(int Delay, float Gain)> taps = [(1500, 0.18f), (3200, 0.11f), (5400, 0.06f)];
        foreach (var (delay, gain) in taps)
            for (var i = delay; i < wet.Length; i++)
                wet[i] += dry[i - delay] * gain;
        var peak = wet.Length == 0 ? 0 : wet.Max(Math.Abs);
        if (peak > 0.85f) { var s = 0.85f / peak; for (var i = 0; i < wet.Length; i++) wet[i] *= s; }
        return wet;
    }

    // Lays the parts on a timeline (start time in seconds) and keeps the peak below clipping.
    private static float[] Mix(double length, params (double At, float[] Wave)[] parts)
    {
        var mix = new float[(int)(length * Rate)];
        foreach (var (at, wave) in parts)
        {
            var start = (int)(at * Rate);
            for (var i = 0; i < wave.Length && start + i < mix.Length; i++) mix[start + i] += wave[i];
        }
        var peak = mix.Length == 0 ? 0 : mix.Max(Math.Abs);
        if (peak > 0) { var scale = 0.85f / peak; for (var i = 0; i < mix.Length; i++) mix[i] *= scale; }
        return mix;
    }
}
