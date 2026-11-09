# Runs INSIDE Windows Sandbox (started by Run-SandboxTest.ps1). Drives Perch's AI chat against a mock model that asks for the
# computer tools, answers the confirmation cards, and checks that the folder was made, cmd opened and the script ran;
# then checks that declining really does nothing. English UI throughout, so this file stays plain ASCII.
param([string]$Approval = 'ask')   # ask | script | none : the "ask before acting" setting under test
$ErrorActionPreference = 'Continue'
# in the development sandbox the host picks the output folder and keeps the sandbox open (PERCH_KEEP)
$out = if ($env:PERCH_OUT) { $env:PERCH_OUT } else { 'C:\PerchOut' }
New-Item -ItemType Directory $out -Force | Out-Null
$summary = New-Object System.Collections.Generic.List[string]
function Say($text) { $summary.Add($text); Add-Content -Path "$out\progress.txt" -Value $text }
function Check($name, $ok, $detail = '') { Say ("{0}  {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)) }
function Finish { $summary | Set-Content "$out\summary.txt" -Encoding UTF8; 'done' | Set-Content "$out\done.txt"; if (-not $env:PERCH_KEEP) { Start-Sleep 2; shutdown.exe /s /t 3 } }

try {
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing, System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);' -Name W -Namespace SB
[void][SB.W]::SetProcessDPIAware()
function Shot($name) { $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; $bm = New-Object Drawing.Bitmap $b.Width, $b.Height; $g = [Drawing.Graphics]::FromImage($bm); $g.CopyFromScreen(0, 0, 0, 0, $bm.Size); $g.Dispose(); $bm.Save("$out\$name.png"); $bm.Dispose() }
Say ("sandbox user: " + $env:USERNAME + ", screen: " + [Windows.Forms.Screen]::PrimaryScreen.Bounds.Size)

# ---- set up: the build, a data folder, the mock model (a clean start, also in a sandbox that has run tests before)
Get-Process Perch -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ? { $_.CommandLine -like '*mock-openai.ps1*' } | % { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 1
Remove-Item C:\PerchRun, C:\PerchData -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'PerchTestFolder'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'PerchDenied') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item C:\PerchBuild C:\PerchRun -Recurse -Force
New-Item -ItemType Directory C:\PerchData -Force | Out-Null
$port = 8791
$state = '{"Settings":{"HotkeysEnabled":false,"Language":"en","NavPinned":["ai","music"],"WebSearch":false,"AiLocalTools":true,"AiApproval":"' + $Approval + '","AiProviderId":"m","AiModel":"mock-agent","AiProviders":[{"Id":"m","Name":"Mock","BaseUrl":"http://127.0.0.1:' + $port + '/v1","Models":["mock-agent"]}]}}'
[IO.File]::WriteAllText('C:\PerchData\state.json', $state, (New-Object Text.UTF8Encoding $false))
$desktop = [Environment]::GetFolderPath('Desktop')
$mock = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\PerchTests\mock-openai.ps1', '-Port', $port, '-Log', "$out\mock.log", '-Desktop', $desktop -PassThru -WindowStyle Minimized
Start-Sleep 3
$env:PERCH_DATA = 'C:\PerchData'
$perch = Start-Process C:\PerchRun\Perch.exe -PassThru
$root = [Windows.Automation.AutomationElement]::RootElement
$cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $perch.Id)
$w = $null; for ($i = 0; $i -lt 100 -and -not $w; $i++) { Start-Sleep -Milliseconds 300; $w = $root.FindFirst('Children', $cond) }
Check 'Perch started' ($null -ne $w)
if (-not $w) { Finish; return }
Start-Sleep 3
$r = $w.Current.BoundingRectangle; $cx = [int]($r.X + $r.Width / 2)
# UI Automation sometimes answers E_FAIL while the window is redrawing; a short retry makes the test steady
function Retry([scriptblock]$what) { for ($i = 0; $i -lt 8; $i++) { try { return (& $what) } catch { Start-Sleep -Milliseconds 400 } } & $what }
function N($n) { Retry { $w.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $n))) } }
function Click($x, $y) { [void][SB.W]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 250; [SB.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [SB.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 600 }
function Invoke($e) { $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Texts { Retry { $w.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) | % { $_.Current.Name } | ? { $_ } } }

# open the island and the AI page
for ($k = 0; $k -lt 6 -and -not (N 'AI'); $k++) { foreach ($y in 2, 6, 10, 14) { [void][SB.W]::SetCursorPos($cx, $y); Start-Sleep -Milliseconds 300 }; [SB.W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [SB.W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 1.5 }
$tab = N 'AI'; Check 'AI page tab found' ($null -ne $tab)
Invoke $tab; Start-Sleep 1

function Ask($message) {
    $box = Retry { $w.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit))) | ? { $_.Current.Name -like 'Message*' } | Select -First 1 }
    $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($message); Start-Sleep -Milliseconds 400
    Invoke (N 'Send')
}
# answers confirmation cards (Allow or Decline) until the answer is complete; returns how many were answered
function Answer($button, $finishedText, $seconds = 60) {
    $answered = 0; $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $b = N $button
        if ($b -and -not $b.Current.IsOffscreen) { Shot ("confirm-$button-" + ($answered + 1)); Invoke $b; $answered++; Start-Sleep -Milliseconds 800; continue }
        if ((Texts) -contains $finishedText -or ((Texts) | ? { $_ -like "*$finishedText*" })) { break }
        Start-Sleep -Milliseconds 500
    }
    $answered
}

# ---- scenario 1: allow everything
$cmdBefore = @(Get-Process cmd -ErrorAction SilentlyContinue).Count
Say '--- scenario 1: allow all'
Ask 'please set things up'
$allowed = Answer 'Allow' 'All three steps are done.'
if ($Approval -eq 'none') { Check 'no confirmation card was shown (approval = never)' ($allowed -eq 0) "(answered $allowed)" }
elseif ($Approval -eq 'script') { Check 'only the script asked (approval = scripts only)' ($allowed -eq 1) "(answered $allowed)" }
else { Check 'three confirmation cards were shown and allowed' ($allowed -eq 3) "(answered $allowed)" }
Start-Sleep 2
Shot 'after-allow'
Check 'folder was created' (Test-Path (Join-Path $desktop 'PerchTestFolder'))
Check 'cmd was opened by open_app' (@(Get-Process cmd -ErrorAction SilentlyContinue).Count -gt $cmdBefore)
$t = Texts
Check 'final answer is shown' ([bool]($t | ? { $_ -like '*All three steps are done*' }))
Say ("steps on screen: " + (($t | ? { $_ -like 'Create folder*' -or $_ -like 'Open:*' -or $_ -like 'Run a PowerShell*' -or $_ -like 'exit code*' -or $_ -like 'created' }) -join ' | '))

# ---- scenario 2: decline (only when something asks about the first action)
if ($Approval -eq 'ask') {
Say '--- scenario 2: decline'
Get-Process cmd -ErrorAction SilentlyContinue | Stop-Process -Force
Ask 'DENY this one'
$declined = Answer 'Decline' 'Understood, I did not do it.'
Check 'one confirmation card was shown and declined' ($declined -eq 1) "(answered $declined)"
Start-Sleep 2
Shot 'after-decline'
Check 'declined folder was NOT created' (-not (Test-Path (Join-Path $desktop 'PerchDenied')))
Check 'model was told it was declined' ([bool](Select-String -Path "$out\mock.log" -Pattern 'declined this action' -Quiet))
}

# ---- what the mock saw (the tool results Perch handed back)
Say '--- tool results as the model received them'
Get-Content "$out\mock.log" | ? { $_ -like '*tool result*' } | % { Say $_ }
$logs = Get-ChildItem C:\PerchData\logs -Filter *.log -ErrorAction SilentlyContinue
if ($logs) { Say '--- Perch log (warnings and errors)'; Get-Content $logs.FullName | ? { $_ -match 'WARN|ERROR' } | % { Say $_ } }
}
catch { Say ("SCRIPT ERROR: " + $_ + " at " + $_.InvocationInfo.ScriptLineNumber) }
finally {
    try { Stop-Process -Id $perch.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue } catch { }
    Finish
}
