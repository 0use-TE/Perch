using System.Runtime.InteropServices;

namespace Perch.Platform.Windows;

// Presses a keyboard shortcut ("Ctrl+Alt+Right") as if typed, so a player's own global shortcut does the work
// (play/pause, next, previous) when the player gives Windows no other way to control it.
internal static class KeySender
{
    public static bool CanSend(string combo) => HotkeyParser.TryParse(combo, out _, out _);

    public static async Task<bool> SendAsync(string combo)
    {
        if (!HotkeyParser.TryParse(combo, out var modifiers, out var key)) return false;
        var held = new List<ushort>();
        if ((modifiers & HotkeyParser.Control) != 0) held.Add(0x11);
        if ((modifiers & HotkeyParser.Alt) != 0) held.Add(0x12);
        if ((modifiers & HotkeyParser.Shift) != 0) held.Add(0x10);
        if ((modifiers & HotkeyParser.Win) != 0) held.Add(0x5B);
        // Arrow, paging, insert and delete keys are "extended" keys.
        var extended = key is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
        var down = held.Select(vk => Key(vk, false, false)).Append(Key((ushort)key, false, extended)).ToArray();
        var up = new[] { Key((ushort)key, true, extended) }.Concat(Enumerable.Reverse(held).Select(vk => Key(vk, true, false))).ToArray();
        var size = Marshal.SizeOf<Input>();
        var sent = SendInput((uint)down.Length, down, size);
        await Task.Delay(40); // long enough for programs that poll the key state
        sent += SendInput((uint)up.Length, up, size);
        return sent == down.Length + up.Length;
    }

    private static Input Key(ushort virtualKey, bool release, bool extended) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyInput { VirtualKey = virtualKey, Flags = (release ? 2u : 0u) | (extended ? 1u : 0u) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nint Extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
