using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Perch.Services;

public sealed record DetectedTrack(string Process, string App, string Title, string Artist, bool Playing);

// Music players that never tell Windows what they play (NetEase Cloud Music, QQ Music ...) still put "song - artist" in
// their window title. This finds such a player, reads that title, and checks whether it is really making sound.
public static class PlayerWatcher
{
    public static IReadOnlyList<(string Process, string Name)> Players => Known;

    private static readonly (string Process, string Name)[] Known =
    [
        ("cloudmusic", "NetEase Cloud Music"),
        ("QQMusic", "QQ Music"),
        ("KuGou", "KuGou Music"),
        ("kuwo", "Kuwo Music"),
    ];

    // Titles a player shows when nothing is selected, and helper windows that are not the song.
    private static readonly string[] Ignored = ["网易云音乐", "NetEase Cloud Music", "QQ音乐", "QQ Music", "酷狗音乐", "酷我音乐", "桌面歌词", "Desktop Lyrics", "迷你播放器", "Default IME", "MSCTFIME UI"];

    public static DetectedTrack? Find()
    {
        foreach (var (processName, appName) in Known)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length == 0) continue;
                var ids = processes.Select(p => p.Id).ToHashSet();
                foreach (var title in TitlesOf(ids))
                {
                    var trimmed = title.Trim();
                    if (trimmed.Length == 0 || Ignored.Any(ignored => trimmed.Contains(ignored, StringComparison.OrdinalIgnoreCase))) continue;
                    var split = trimmed.Split(" - ", 2, StringSplitOptions.TrimEntries);
                    if (split.Length < 2 || split[0].Length == 0) continue;
                    return new DetectedTrack(processName, appName, split[0], split[1], AudioActivity.IsAudible(ids));
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return null;
    }

    // Titles of every top-level window of these processes, including hidden ones (a player minimised to the tray keeps its title).
    private static List<string> TitlesOf(HashSet<int> ids)
    {
        var titles = new List<string>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!ids.Contains((int)pid)) return true;
            var text = new StringBuilder(512);
            if (GetWindowText(window, text, text.Capacity) > 0) titles.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
