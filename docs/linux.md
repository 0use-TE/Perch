# Arch Linux x86_64 / X11

This is an initial native X11 port using the existing Avalonia 12.0 UI. Use an X11 session with an EWMH window manager (for example Openbox, Xfce or Plasma X11). A compositor is needed for transparent rounded edges. A system tray that supports StatusNotifierItem is needed for the tray menu. Native Wayland, layer-shell and desktop portals for global shortcuts are outside this port's scope; running under XWayland does not establish native Wayland support.

## Build and run

Install .NET **10** SDK and the runtime libraries on Arch:

```sh
sudo pacman -S --needed dotnet-sdk libx11 libxext libxfixes libxss libxi libxcursor libxrandr libice libsm fontconfig freetype2 mesa icu openssl zlib glib2 xdg-utils ttf-dejavu
# Optional: MPRIS music control, audio playback, API-key storage and Chinese fonts
sudo pacman -S --needed playerctl alsa-utils libsecret noto-fonts-cjk
```

Check `dotnet --list-sdks` includes a 10.x SDK. If Arch has moved to a newer SDK, install .NET 10 through Microsoft's [user-local installer](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script). Saving API keys also requires an unlocked Secret Service provider, such as GNOME Keyring or KeePassXC with Secret Service integration enabled.

```sh
dotnet build Perch.slnx -c Release
dotnet run --project src/Perch/Perch.csproj -c Release
```

Publish a directory containing the application and .NET runtime:

```sh
dotnet publish src/Perch/Perch.csproj -c Release -r linux-x64 \
  --self-contained true -o artifacts/Perch-linux-x64
./artifacts/Perch-linux-x64/Perch
```

Distribute the **whole directory**, including its `.so` files and licenses. This is not a single-file/AOT release. System X11, font and graphics libraries are still required. For a smaller build requiring an installed .NET 10 runtime, use `--self-contained false`.

`packaging/linux/net.0use.Perch.desktop` is a desktop-entry template for packagers; its `Exec=Perch` expects the published executable on `PATH`. Install the application icon from `src/Perch/Assets/perch.png` as `net.0use.Perch.png`. No system installation or AUR publication is performed by building the project.

The build selects `net10.0` for Linux and the original Windows target framework for Windows. A runtime such as `-r win-x64` selects Windows when cross-building; use `-p:EnableWindowsTargeting=true` on Linux. `-p:PerchPlatform=Windows` can also select that target explicitly. Restore/build one target at a time because they share the project's intermediate directory.

## Desktop integration

| Feature | X11 implementation |
| --- | --- |
| Island, hover, editing, rounded click-through shape | Avalonia window + XShape input/bounding regions; primary-screen work area |
| Global shortcuts | XGrabKey; default Ctrl+Alt+I and Ctrl+Alt+N; Caps Lock/Num Lock are ignored |
| Clipboard history | XFixes selection-owner notifications; password-manager exclusion formats are skipped |
| Installed applications | XDG `.desktop` files; user entries override system entries; `gio launch` handles Exec field codes |
| Open files/URLs, reveal parent directory | `xdg-open` |
| User-defined commands | `/bin/sh -c`, with the command passed as one argument |
| Login startup | `$XDG_CONFIG_HOME/autostart/net.0use.Perch.desktop` (defaults to `~/.config`) |
| CPU, memory, disk, uptime, network | `/proc`, .NET filesystem/network APIs |
| Battery / CPU temperature | `/sys/class/power_supply`; coretemp/k10temp/zenpower hwmon sensors, when readable |
| Listening TCP ports | `/proc/net/tcp{,6}` and `/proc/<pid>/fd`; inaccessible process owners display `?`/PID 0 |
| Music | MPRIS via optional `playerctl`; title, artist, progress and transport buttons; no album art yet |
| Sounds | Generated WAV piped to optional `aplay` from `alsa-utils`; ALSA needs a working default device |
| Calendar / weather | `.ics` feeds and files; manually chosen weather city |
| AI API keys | `secret-tool`/Secret Service; JSON contains an opaque reference, not a plaintext/base64 copy |
| Fullscreen hiding / idle reminders | EWMH fullscreen state / XScreenSaver idle time |

To-dos, notes, focus timers, alarms, file staging, image export, photos, AI chat and the dinosaur game reuse the shared code. AI chat without a key works with suitable local servers. If the keyring is locked or absent, saving a key reports a validation error rather than writing it unprotected. Settings and chats normally live in `~/.config/Perch` (`$XDG_CONFIG_HOME/Perch` when configured); `PERCH_DATA` overrides the directory for testing.

Windows calendar accounts, automatic geolocation, screenshot-based glass, GPU counters and the Windows/PowerShell AI computer tools are unavailable in this initial port. Their relevant settings are disabled. Windows-player shortcut emulation is hidden; use an MPRIS player. No Linux shell-execution tool is automatically exposed to AI chat.

The bundled MIT-licensed FluentAvalonia font supplies icons without installing Windows fonts. The few glyphs absent from that font use equivalent shared symbols. `Tmds.DBus.Protocol` is pinned to **0.92.0**: it includes the [D-Bus security fixes](https://github.com/tmds/Tmds.DBus/security/advisories/GHSA-xrw6-gwf8-vvr9) while retaining the `Connection` API used by Avalonia 12.0. Version 0.93 and later remove that API and cause an X11 startup `TypeLoadException` with this Avalonia version.

## Verification

Run the non-graphical Linux regression checks (proc parsing, a real local listener, XDG overrides and single-instance IPC):

```sh
dotnet run --project tests/Perch.Linux.Tests -c Release
```

For isolated desktop checks, install the test tools and build first:

```sh
sudo pacman -S --needed python xorg-server-xvfb openbox xcompmgr xdotool xorg-xmessage xorg-fonts-misc xclip imagemagick dbus
dotnet build src/Perch/Perch.csproj -c Release
python tools/Verify-X11.py
```

The script starts its own Xvfb server, Openbox, compositor and private D-Bus, and uses temporary settings. It checks native startup, input regions, focus preservation, hover, shortcut repeat handling, fullscreen hiding/restoration, second-instance activation, text entry/persistence, clipboard history and Settings. Screenshots and results go to `artifacts/x11-smoke`. `--dotnet /path/to/dotnet` supports a user-local SDK; `--app /path/to/Perch` (or `Perch.dll`) can test a published directory.

This automated session cannot establish hardware/desktop parity. Real multi-monitor changes, fractional DPI, Chinese IME composition, tray hosts, audio devices, Secret Service providers and media players still require manual verification. Windows cross-compilation verifies source compatibility, not Windows desktop behavior.
