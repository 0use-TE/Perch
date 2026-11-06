param([string]$Exe = "$PSScriptRoot\..\src\Perch\bin\Release\net10.0-windows\Perch.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class IslandProbe {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr h,IntPtr r);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetStyle(IntPtr h,int i);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
 [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr r,int x,int y);
 [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr r);
 public static IntPtr Find(uint process) { IntPtr result=IntPtr.Zero; EnumWindows((h,p)=>{uint id;GetWindowThreadProcessId(h,out id);if(id==process&&IsWindowVisible(h)){result=h;return false;}return true;},IntPtr.Zero);return result; }
}
'@
function Assert($condition, $description) { if (!$condition) { throw "$description (foreground=$([IslandProbe]::GetForegroundWindow()), initial=$foreground, island=$handle)" }; Write-Output "PASS: $description" }
function Bounds($handle) { $r = New-Object IslandProbe+RECT; [IslandProbe]::GetWindowRect($handle,[ref]$r) | Out-Null; return $r }
function Click($x,$y) {
    [IslandProbe]::SetCursorPos([int]$x,[int]$y) | Out-Null
    Start-Sleep -Milliseconds 100
    [IslandProbe]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [IslandProbe]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
function ClickNamed($name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
    if (!$element -or $element.Current.IsOffscreen) { throw "Visible control not found: $name" }
    $rect = $element.Current.BoundingRectangle
    Click ($rect.X+$rect.Width/2) ($rect.Y+$rect.Height/2)
}
$cursor = New-Object IslandProbe+POINT
[IslandProbe]::GetCursorPos([ref]$cursor) | Out-Null
$process = $null
$handle = [IntPtr]::Zero
try {
    [IslandProbe]::SetCursorPos(30,500) | Out-Null
    $foreground = [IslandProbe]::GetForegroundWindow()
    $env:PERCH_LANG = 'zh-CN' # the checks below find controls by their Chinese names
    $process = Start-Process -FilePath (Resolve-Path $Exe) -PassThru -WindowStyle Hidden
    for ($i=0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 100
        $handle = [IslandProbe]::Find($process.Id)
        if ($handle -ne [IntPtr]::Zero) { break }
    }
    Assert ($handle -ne [IntPtr]::Zero) 'Visible island started'
    Start-Sleep -Milliseconds 600
    Assert ($foreground -eq [IslandProbe]::GetForegroundWindow()) 'Startup preserves foreground window'
    $r = Bounds $handle
    $scale = [IslandProbe]::GetDpiForWindow($handle) / 96.0
    Assert ([Math]::Abs(($r.Bottom-$r.Top)/$scale - 36) -lt 2) 'Compact host is 36 DIP tall'
    $style = [IslandProbe]::GetStyle($handle,-20).ToInt64()
    Assert (($style -band 0x80) -ne 0 -and ($style -band 0x40000) -eq 0) 'Tool window excludes taskbar and Alt+Tab'
    Assert (($style -band 0x8000000) -ne 0) 'Passive mode prevents the window from taking native keyboard focus'
    $region = [IslandProbe]::CreateRectRgn(0,0,0,0)
    try {
        [IslandProbe]::GetWindowRgn($handle,$region) | Out-Null
        Assert (![IslandProbe]::PtInRegion($region,[int](249*$scale),[int](20*$scale))) 'No side components exist in native HWND region'
        Assert ([IslandProbe]::PtInRegion($region,[int](100*$scale),[int](22*$scale))) 'Main island is included in native HWND region'
        Assert (![IslandProbe]::PtInRegion($region,0,[int](20*$scale))) 'Transparent corner is excluded from native HWND region'
    } finally { [IslandProbe]::DeleteObject($region) | Out-Null }
    $center = [int](($r.Left+$r.Right)/2)
    $foreground = [IslandProbe]::GetForegroundWindow()
    [IslandProbe]::SetCursorPos($center,($r.Top+[int](20*$scale))) | Out-Null
    Start-Sleep -Milliseconds 1400
    $expanded = Bounds $handle
    Assert ([Math]::Abs(($expanded.Bottom-$expanded.Top)/$scale - 294) -lt 2) 'Hover expands host to 294 DIP (276 island + overshoot pad + capsule gap)'
    Assert ($foreground -eq [IslandProbe]::GetForegroundWindow()) 'Hover and animation preserve foreground window'
    for ($i=0; $i -lt 10; $i++) {
        [IslandProbe]::SetCursorPos(30,500) | Out-Null
        Start-Sleep -Milliseconds 490
        [IslandProbe]::SetCursorPos($center,($r.Top+[int](20*$scale))) | Out-Null
        Start-Sleep -Milliseconds 220
    }
    Start-Sleep -Milliseconds 1000
    Assert ([IslandProbe]::IsWindowVisible($handle)) 'Island survives ten animation reversals'
    [IslandProbe]::SetCursorPos(30,500) | Out-Null
    Start-Sleep -Milliseconds 1500
    $collapsed = Bounds $handle
    Assert ([Math]::Abs(($collapsed.Bottom-$collapsed.Top)/$scale - 36) -lt 2) 'Collapse shrinks native host after animation'
    Assert ([Math]::Abs(($collapsed.Right-$collapsed.Left)/$scale - 248) -lt 2) 'Compact native host width is restored'
    [IslandProbe]::SetCursorPos($center,($r.Top+[int](20*$scale))) | Out-Null
    Start-Sleep -Milliseconds 1400
    $foreground = [IslandProbe]::GetForegroundWindow()
    ClickNamed '固定'
    Assert ($foreground -eq [IslandProbe]::GetForegroundWindow()) 'Pin button preserves foreground window'
    [IslandProbe]::SetCursorPos(30,500) | Out-Null
    Start-Sleep -Milliseconds 1400
    $pinned = Bounds $handle
    Assert ([Math]::Abs(($pinned.Bottom-$pinned.Top)/$scale - 294) -lt 2) 'Pinned panel stays open after pointer leaves'
    $foreground = [IslandProbe]::GetForegroundWindow()
    ClickNamed '待办'
    Assert ($foreground -eq [IslandProbe]::GetForegroundWindow()) 'Page switch preserves foreground window'
    ClickNamed '新待办'
    Assert ([IslandProbe]::GetForegroundWindow() -eq $handle) 'Clicking text input activates the island'
    Assert (([IslandProbe]::GetStyle($handle,-20).ToInt64() -band 0x8000000) -eq 0) 'Text input removes WS_EX_NOACTIVATE for editing'
    Write-Output "Verified display scale: $([Math]::Round($scale*100))%"
} finally {
    if ($handle -ne [IntPtr]::Zero) { [IslandProbe]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
    if ($process) {
        if (!$process.WaitForExit(5000)) { $process.Kill(); throw 'Normal shutdown did not finish in five seconds' }
        Write-Output 'PASS: Normal close exits the process'
    }
    [IslandProbe]::SetCursorPos($cursor.X,$cursor.Y) | Out-Null
}



