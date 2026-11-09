namespace Perch.Services.Sounds;

// One choice in a sound list: an id stored in the settings and the key of its display name.
public sealed record SoundOption(string Id, string NameKey)
{
    public string Name => Loc.T(NameKey);
}

// The built-in sounds. They are made of simple tones (sine waves with a few overtones and a decay) when first needed,
// so Perch ships no audio files; every sound is a short clip of 16-bit mono PCM at 44.1 kHz.
public static class SoundBank
{
    public const int Rate = 44100;
    public const string System = "system", Off = "off", Default = "";

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

    public static bool Exists(string id) => id == Snap || Options.Any(o => o.Id == id);

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
            case "chime": // two bright bell notes
                return Mix(1.3, (0.00, Note(1318.5, 1.1, 0.30, 0.004, 0.55, 0.30, 0.12)), (0.17, Note(1760.0, 1.1, 0.30, 0.004, 0.55, 0.30, 0.12)));
            case "bell": // a struck bell: inharmonic partials that die away slowly
                return Mix(2.6, (0.0, Bell(523.25, 2.5)));
            case "marimba": // four quick wooden notes
                return Mix(1.1, (0.00, Note(523.25, 0.7, 0.13, 0.002, 0.0, 0.0, 0.35)), (0.13, Note(659.25, 0.7, 0.13, 0.002, 0.0, 0.0, 0.35)),
                    (0.26, Note(783.99, 0.7, 0.13, 0.002, 0.0, 0.0, 0.35)), (0.39, Note(1046.5, 0.8, 0.16, 0.002, 0.0, 0.0, 0.35)));
            case "drop": // a water drop: a short falling tone, then a softer one
                return Mix(0.9, (0.0, Drop(1300, 520, 0.22, 0.07, 0.9)), (0.34, Drop(1000, 430, 0.22, 0.08, 0.45)));
            case "digital": // three short electronic beeps
                return Mix(0.75, (0.00, Beep(1000, 0.09)), (0.16, Beep(1000, 0.09)), (0.32, Beep(1000, 0.09)));
            case "pulse": // three soft low pulses
                return Mix(1.0, (0.00, Note(330, 0.3, 0.14, 0.015, 0.0, 0.25, 0.0)), (0.30, Note(330, 0.3, 0.14, 0.015, 0.0, 0.25, 0.0)), (0.60, Note(392, 0.3, 0.14, 0.015, 0.0, 0.25, 0.0)));
            case Snap: // a small soft "tick": a very short falling tone over a faint high click, kept quiet
            {
                var tick = Mix(0.08, (0.0, Drop(1500, 700, 0.06, 0.012, 1.0)), (0.0, Note(3200, 0.02, 0.004, 0.0005, 0.0, 0.0, 0.0)));
                for (var i = 0; i < tick.Length; i++) tick[i] *= 0.55f;
                return tick;
            }
            case "alarm": // the classic alarm: two alternating urgent tones, repeated
                return Alarm();
            default: // "gentle": a soft rising arpeggio
                return Mix(1.8, (0.00, Note(783.99, 0.6, 0.45, 0.012, 0.0, 0.2, 0.0)), (0.13, Note(987.77, 0.6, 0.45, 0.012, 0.0, 0.2, 0.0)),
                    (0.26, Note(1174.7, 0.6, 0.45, 0.012, 0.0, 0.2, 0.0)), (0.39, Note(1568.0, 0.7, 0.55, 0.012, 0.0, 0.2, 0.0)));
        }
    }

    // A note: a sine wave with up to three overtones, a fast attack and an exponential decay (time constant `decay`).
    private static float[] Note(double frequency, double length, double decay, double attack, double second, double third, double fourth)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var envelope = Math.Min(1, t / Math.Max(attack, 1e-4)) * Math.Exp(-t / decay);
            var phase = 2 * Math.PI * frequency * t;
            var value = Math.Sin(phase) + second * Math.Sin(2 * phase) + third * Math.Sin(3 * phase) + fourth * Math.Sin(4 * phase);
            wave[i] = (float)(value * envelope);
        }
        return wave;
    }

    private static float[] Bell(double frequency, double length)
    {
        double[] ratio = [1.0, 2.0, 2.76, 5.4, 8.93], level = [1.0, 0.55, 0.45, 0.22, 0.12], decay = [1.1, 0.8, 0.6, 0.35, 0.2];
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            double value = 0;
            for (var p = 0; p < ratio.Length; p++) value += level[p] * Math.Sin(2 * Math.PI * frequency * ratio[p] * t) * Math.Exp(-t / decay[p]);
            wave[i] = (float)(value * Math.Min(1, t / 0.003));
        }
        return wave;
    }

    private static float[] Drop(double from, double to, double length, double decay, double level)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        double phase = 0;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var frequency = to + (from - to) * Math.Exp(-t / 0.05); // glides down quickly
            phase += 2 * Math.PI * frequency / Rate;
            wave[i] = (float)(Math.Sin(phase) * Math.Exp(-t / decay) * level * Math.Min(1, t / 0.002));
        }
        return wave;
    }

    // A flat electronic beep with a trace of the third overtone.
    private static float[] Beep(double frequency, double length)
    {
        var count = (int)(length * Rate);
        var wave = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)Rate;
            var edge = Math.Min(Math.Min(1, t / 0.004), Math.Min(1, (length - t) / 0.006));
            var phase = 2 * Math.PI * frequency * t;
            wave[i] = (float)((Math.Sin(phase) + 0.33 * Math.Sin(3 * phase)) * 0.75 * edge);
        }
        return wave;
    }

    private static float[] Alarm()
    {
        var parts = new List<(double At, float[] Wave)>();
        for (var round = 0; round < 4; round++)
        {
            parts.Add((round * 0.52, Beep(880, 0.13)));
            parts.Add((round * 0.52 + 0.19, Beep(1175, 0.13)));
        }
        return Mix(2.3, parts.ToArray());
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
