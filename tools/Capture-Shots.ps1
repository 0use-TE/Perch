# Takes the screenshots used by the website (website/assets/shots/*.png).
#
# It covers the screen with a gradient "wallpaper" (tools/shots/backdrop.ps1), starts Perch with a throw-away state folder
# filled with sample data (tools/shots/seed.ps1), then drives the real UI through UI Automation and saves PNGs.
# Run it from an interactive, unlocked desktop and leave the mouse alone for about a minute.
#
#   pwsh tools\Capture-Shots.ps1                 # all shots
#   pwsh tools\Capture-Shots.ps1 -Only island    # only the island pages (also: settings)
#
# Not captured automatically (open it yourself and take the shot):
#   music.png   - needs a player that is playing; crop the island while a song shows.
#   glass.png   - the frosted island hides itself from screen capture on purpose.
# Check apps.png, photos.png and shelf.png before publishing: they show your Start-menu apps / Pictures folder.
param(
    [string]$Exe = "$PSScriptRoot\..\src\Perch\bin\Release\net10.0-windows10.0.19041.0\Perch.exe",
    [string]$Out = "$PSScriptRoot\..\website\assets\shots",
    [ValidateSet('all', 'island', 'settings')][string]$Only = 'all'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int w,int hh,uint f);' -Name Shots -Namespace Perch
[void][Perch.Shots]::SetProcessDPIAware()
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$Exe = (Resolve-Path $Exe).Path
$flag = Join-Path $env:TEMP 'perch-shots-stop.flag'
$screen = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$parkX = 10; $parkY = $screen.Height - 20

function Find($scope, [string]$name) {
    $scope.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
}
function Invoke($el) { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot($name, $x, $y, $w, $h) {
    $bm = New-Object Drawing.Bitmap ([int]$w), ([int]$h)
    $g = [Drawing.Graphics]::FromImage($bm); $g.CopyFromScreen([int]$x, [int]$y, 0, 0, $bm.Size)
    $bm.Save((Join-Path $Out "$name.png")); Write-Host "saved $name.png"
}

# Starts the backdrop and Perch (sample data), runs $Body with the Perch process and its island window, then cleans up.
function Session([string]$Pinned, [string]$Theme, [scriptblock]$Body) {
    & "$PSScriptRoot\shots\seed.ps1" -Pinned $Pinned -Theme $Theme | Out-Null
    Remove-Item $flag, "$flag.ready" -ErrorAction SilentlyContinue
    $backdrop = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-File', "$PSScriptRoot\shots\backdrop.ps1", '-Flag', $flag, '-MaxSeconds', '240'
    # Perch must start after the backdrop is on screen, or the (topmost) backdrop ends up above the island.
    for ($wait = 0; $wait -lt 100 -and -not (Test-Path "$flag.ready"); $wait++) { Start-Sleep -Milliseconds 200 }
    Start-Sleep 1
    $env:PERCH_DATA = Join-Path $env:TEMP 'perch-shots'
    [void][Perch.Shots]::SetCursorPos($parkX, $parkY)
    $p = Start-Process $Exe -PassThru
    try {
        $root = [Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
        $win = $null
        for ($try = 0; $try -lt 30 -and -not $win; $try++) {
            Start-Sleep -Seconds 1
            $win = $root.FindAll([Windows.Automation.TreeScope]::Children, $cond) | Select-Object -First 1
        }
        if (-not $win) { throw 'Perch window did not appear' }
        # Both the backdrop and the island are topmost; make sure the island is the upper one.
        [void][Perch.Shots]::SetWindowPos([IntPtr]$win.Current.NativeWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x53)
        Start-Sleep -Seconds 3
        & $Body $p $root $cond $win
    } finally {
        [void][Perch.Shots]::SetCursorPos($parkX, $parkY)
        Stop-Process $p.Id -ErrorAction SilentlyContinue
        New-Item $flag -ItemType File -Force | Out-Null
        [void]$backdrop.WaitForExit(8000) # the next session must not start while this backdrop is still on screen
        Start-Sleep 1
    }
}

# Island: hover to open, then switch pages through the tab buttons.
function IslandPages([string]$Pinned, [string[]]$Tabs, [string[]]$Names, [string]$Clip, [switch]$Extras) {
    Session $Pinned 'dark' {
        param($p, $root, $cond, $win)
        $r = $win.Current.BoundingRectangle; $cx = [int]($r.X + $r.Width / 2)
        if ($Extras) { Shot 'compact' $r.X $r.Y $r.Width 120 }
        [void][Perch.Shots]::SetCursorPos($cx, [int]$r.Y + 8); Start-Sleep -Milliseconds 2200
        if ($Clip) { foreach ($text in ($Clip -split '\|')) { Set-Clipboard -Value $text; Start-Sleep -Milliseconds 800 } }
        for ($i = 0; $i -lt $Tabs.Count; $i++) {
            $el = Find $win $Tabs[$i]
            if (-not $el) { Write-Warning "tab '$($Tabs[$i])' not found"; continue }
            Invoke $el; Start-Sleep -Milliseconds 1000
            Shot $Names[$i] $r.X $r.Y $r.Width 560
        }
        if ($Extras) {
            Invoke (Find $win '更多'); Start-Sleep -Milliseconds 900
            Shot 'more' $r.X $r.Y $r.Width 560
        }
    }
}

# Settings window on the sample data; $Pages are sidebar names.
function SettingsPages([string]$Theme, [string[]]$Pages, [string[]]$Names) {
    Session 'music,focus,todo,notes,info,calendar' $Theme {
        param($p, $root, $cond, $win)
        Invoke (Find $win '设置'); Start-Sleep 2
        $sw = $root.FindAll([Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.BoundingRectangle.Width -gt 600 } | Select-Object -First 1
        # Put the settings window on top of the backdrop (it is only moved, never clicked).
        [void][Perch.Shots]::SetWindowPos([IntPtr]$sw.Current.NativeWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x53); Start-Sleep -Milliseconds 800
        for ($i = 0; $i -lt $Pages.Count; $i++) {
            $item = Find $sw $Pages[$i]
            if ($item) { try { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { Invoke $item }; Start-Sleep -Milliseconds 900 }
            $b = $sw.Current.BoundingRectangle
            Shot $Names[$i] $b.X $b.Y $b.Width ([Math]::Min($b.Height, $screen.Height - $b.Y))
        }
    }
}

if ($Only -in 'all', 'island') {
    IslandPages 'music,focus,todo,notes,info,calendar' @('概览', '专注', '待办', '笔记', '天气与状态', '日历') @('home', 'focus', 'todo', 'notes', 'info', 'calendar') -Extras
    IslandPages 'clipboard,system,apps,tray,photos' @('剪贴板', '系统', '应用与指令', '文件中转', '照片') @('clipboard', 'system', 'apps', 'shelf', 'photos') -Clip '会议链接 https://example.com/room/42|git commit -m "fix: login redirect"|明天 10:00 和设计组对齐配色'
}
if ($Only -in 'all', 'settings') {
    SettingsPages 'dark' @('灵动岛', '功能', '提醒', '通用') @('settings-island', 'settings-features', 'settings-reminders', 'settings-general')
    SettingsPages 'light' @('灵动岛', '通用') @('settings-island-light', 'settings-general-light')
}
Write-Host "Done. Review the PNGs in $Out before publishing."
