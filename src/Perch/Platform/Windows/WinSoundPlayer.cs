using System.Runtime.InteropServices;
using System.Text;
using Perch.Services.Sounds;

namespace Perch.Platform.Windows;

// Plays WAV data straight from memory with PlaySound, or custom local audio files (.wav/.mp3/.m4a/.wma) via MCI.
internal sealed class WinSoundPlayer : ISoundPlayer
{
    private const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004, Loop = 0x0008, Purge = 0x0040;
    private const string Alias = "perch_custom";
    private GCHandle pinned;
    private bool mciOpen;

    public void Play(byte[] wav, bool loop)
    {
        Stop();
        pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
        PlaySound(pinned.AddrOfPinnedObject(), 0, Memory | Async | NoDefault | (loop ? Loop : 0));
    }

    public void PlayFile(string path, double volume, bool loop)
    {
        Stop();
        var safe = path.Replace("\"", "");
        if (Mci($"open \"{safe}\" alias {Alias}") != 0) return;
        mciOpen = true;
        var vol = (int)Math.Clamp(Math.Round(volume * 1000), 0, 1000);
        Mci($"setaudio {Alias} volume to {vol}");
        Mci($"play {Alias}" + (loop ? " repeat" : ""));
    }

    public void Stop()
    {
        PlaySound(0, 0, Purge);
        if (pinned.IsAllocated) pinned.Free();
        if (mciOpen)
        {
            Mci($"stop {Alias}");
            Mci($"close {Alias}");
            mciOpen = false;
        }
    }

    public void SystemBeep() => MessageBeep(0x40);

    private static int Mci(string command) => mciSendString(command, null, 0, 0);

    [DllImport("winmm.dll")] private static extern bool PlaySound(nint sound, nint module, uint flags);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, nint hwndCallback);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}
