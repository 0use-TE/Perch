using System.Runtime.InteropServices;
using Perch.Services.Sounds;

namespace Perch.Platform.Windows;

// Plays WAV data straight from memory with the Windows multimedia API; no file is written. The clip must stay in
// place while it plays, so it is pinned until the next sound starts or Stop is called.
internal sealed class WinSoundPlayer : ISoundPlayer
{
    private const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004, Loop = 0x0008, Purge = 0x0040;
    private GCHandle pinned;

    public void Play(byte[] wav, bool loop)
    {
        Stop();
        pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
        PlaySound(pinned.AddrOfPinnedObject(), 0, Memory | Async | NoDefault | (loop ? Loop : 0));
    }

    public void Stop()
    {
        PlaySound(0, 0, Purge);
        if (pinned.IsAllocated) pinned.Free();
    }

    public void SystemBeep() => MessageBeep(0x40);

    [DllImport("winmm.dll")] private static extern bool PlaySound(nint sound, nint module, uint flags);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}
