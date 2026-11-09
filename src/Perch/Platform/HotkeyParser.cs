using Avalonia.Input;
using Avalonia.Win32.Input;

namespace Perch.Platform;

// "Ctrl+Alt+P" <-> modifier flags and a virtual-key code for RegisterHotKey.
internal static class HotkeyParser
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8;

    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0; virtualKey = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Control; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                case "win": modifiers |= Win; break;
                default: return false;
            }
        }
        var last = parts[^1];
        if (last.Length == 1 && char.IsDigit(last[0])) last = "D" + last;
        if (!Enum.TryParse<Key>(last, true, out var key) || key == Key.None) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0 && modifiers != 0;
    }

    public static string Format(KeyModifiers modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }
}
