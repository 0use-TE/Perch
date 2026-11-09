# Runs INSIDE the development sandbox: opens the dinosaur game, starts it with a click, jumps with the keyboard, and saves
# screenshots of the island at several moments (the host looks at them). Checks that the picture is animating and that
# a run that never jumps ends. English UI, plain ASCII.
$ErrorActionPreference = 'Continue'
$out = if ($env:PERCH_OUT) { $env:PERCH_OUT } else { 'C:\PerchOut' }
New-Item -ItemType Directory $out -Force | Out-Null
$summary = New-Object System.Collections.Generic.List[string]
function Say($text) { $summary.Add($text); Add-Content -Path "$out\progress.txt" -Value $text }
function Check($name, $ok, $detail = '') { Say ("{0}  {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)) }
function Finish { $summary | Set-Content "$out\summary.txt" -Encoding UTF8; 'done' | Set-Content "$out\done.txt"; if (-not $env:PERCH_KEEP) { Start-Sleep 2; shutdown.exe /s /t 3 } }
try {
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing, System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);' -Name W -Namespace SB2
[void][SB2.W]::SetProcessDPIAware()
Get-Process Perch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
Remove-Item C:\PerchRun, C:\PerchData -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item C:\PerchBuild C:\PerchRun -Recurse -Force
New-Item -ItemType Directory C:\PerchData -Force | Out-Null
[IO.File]::WriteAllText('C:\PerchData\state.json', '{"Settings":{"HotkeysEnabled":false,"Language":"en","NavPinned":["dino","music"]}}', (New-Object Text.UTF8Encoding $false))
$env:PERCH_DATA = 'C:\PerchData'
$perch = Start-Process C:\PerchRun\Perch.exe -PassThru
$root = [Windows.Automation.AutomationElement]::RootElement
$cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $perch.Id)
$w = $null; for ($i = 0; $i -lt 100 -and -not $w; $i++) { Start-Sleep -Milliseconds 300; $w = $root.FindFirst('Children', $cond) }
Check 'Perch started' ($null -ne $w); if (-not $w) { Finish; return }
Start-Sleep 3
$r = $w.Current.BoundingRectangle; $cx = [int]($r.X + $r.Width / 2)
function Retry([scriptblock]$what) { for ($i = 0; $i -lt 8; $i++) { try { return (& $what) } catch { Start-Sleep -Milliseconds 400 } } & $what }
function N($n) { Retry { $w.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $n))) } }
function Click($x, $y) { [void][SB2.W]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 250; [SB2.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [SB2.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300 }
function Shot($name) { $bm = New-Object Drawing.Bitmap 872, 300; $g = [Drawing.Graphics]::FromImage($bm); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bm.Size); $g.Dispose(); $bm.Save("$out\$name.png"); $bm }
function Changed($a, $b) { $n = 0; for ($x = 0; $x -lt $a.Width; $x += 2) { for ($y = 60; $y -lt $a.Height; $y += 2) { $c = $a.GetPixel($x, $y); $d = $b.GetPixel($x, $y); if ([Math]::Abs($c.R - $d.R) + [Math]::Abs($c.G - $d.G) + [Math]::Abs($c.B - $d.B) -gt 80) { $n++ } } }; $n }
for ($k = 0; $k -lt 6 -and -not (N 'Dino'); $k++) { foreach ($y in 2, 6, 10, 14) { [void][SB2.W]::SetCursorPos($cx, $y); Start-Sleep -Milliseconds 300 }; [SB2.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [SB2.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 1.5 }
$tab = N 'Dino'; Check 'Dino tab found' ($null -ne $tab)
$tab.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1.5
$ready = Shot 'dino-1-ready'
# start with a click in the middle of the game area
$gx = [int]($r.X + 440); $gy = [int]($r.Y + 190)
Click $gx $gy
Start-Sleep -Milliseconds 400
$a = Shot 'dino-2-running-a'; Start-Sleep -Milliseconds 250; $b = Shot 'dino-3-running-b'
$moved = Changed $a $b
Check 'the game is animating after the click' ($moved -gt 40) "(changed samples: $moved)"
# jump a few times with the keyboard
for ($i = 0; $i -lt 3; $i++) { [Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep -Milliseconds 900 }
Shot 'dino-4-after-jumps' | Out-Null
# stop jumping: a cactus ends the run sooner or later
Start-Sleep 12
$dead = Shot 'dino-5-later'
Start-Sleep -Milliseconds 300
$still = Shot 'dino-6-later-b'
Check 'a run without jumping ends (the picture stops moving)' ((Changed $dead $still) -lt 30) "(changed samples: $(Changed $dead $still))"
Click $gx $gy
Start-Sleep -Milliseconds 500
$again = Shot 'dino-7-restarted'; Start-Sleep -Milliseconds 250; $again2 = Shot 'dino-8-restarted-b'
Check 'a click starts a new run' ((Changed $again $again2) -gt 40)
$logs = Get-ChildItem C:\PerchData\logs -Filter *.log -ErrorAction SilentlyContinue
if ($logs) { Get-Content $logs.FullName | ? { $_ -match 'WARN|ERROR' } | % { Say $_ } }
}
catch { Say ("SCRIPT ERROR: " + $_ + " at " + $_.InvocationInfo.ScriptLineNumber) }
finally { try { Stop-Process -Id $perch.Id -Force -ErrorAction SilentlyContinue } catch { }; Finish }
