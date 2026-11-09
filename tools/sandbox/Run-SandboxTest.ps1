<#
  Runs a test script inside Windows Sandbox, so tests that click, type, start programs and create files never touch the
  developer's own desktop or data.

  - If a development sandbox is open (Start-DevSandbox.ps1), the test is sent to it and the sandbox stays open afterwards.
  - Otherwise a clean sandbox is started for this one test and closed when it has finished.

  Usage:   .\Run-SandboxTest.ps1 [-Test agent-tools.ps1] [-TestArgs '-Approval none'] [-Build ..\..\artifacts\x64-latest] [-TimeoutMinutes 6]

  The test script writes summary.txt (and screenshots) to its output folder and finally done.txt; this script waits for done.txt.
  Results: <repo>\artifacts\sandbox-out\<time>
#>
param(
    [string]$Test = 'agent-tools.ps1',
    [string]$TestArgs = '',
    [string]$Build = (Join-Path $PSScriptRoot '..\..\artifacts\x64-latest'),
    [int]$TimeoutMinutes = 6
)
$ErrorActionPreference = 'Stop'
$outRoot = Join-Path $PSScriptRoot '..\..\artifacts\sandbox-out'
New-Item -ItemType Directory -Force $outRoot | Out-Null
$outRoot = (Resolve-Path $outRoot).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $outRoot $stamp
New-Item -ItemType Directory -Force $out | Out-Null

$running = Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue
$heartbeat = Join-Path $outRoot 'watching.txt'
$dev = $running -and (Test-Path $heartbeat) -and ((Get-Date) - (Get-Item $heartbeat).LastWriteTime).TotalSeconds -lt 15

if ($dev) {
    Write-Host "Sending $Test to the open development sandbox; results in $out"
    $request = @{ test = $Test; args = $TestArgs; out = "C:\PerchOut\$stamp" } | ConvertTo-Json
    New-Item -ItemType Directory -Force (Join-Path $outRoot 'requests') | Out-Null
    [IO.File]::WriteAllText((Join-Path $outRoot "requests\$stamp.json"), $request, (New-Object Text.UTF8Encoding $false))
}
else {
    if ($running) { throw 'A Windows Sandbox is open but it is not a development sandbox (start one with Start-DevSandbox.ps1, or close this one).' }
    $sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
    if (-not (Test-Path $sandbox)) { throw 'Windows Sandbox is not installed (Windows features > Windows Sandbox).' }
    $build = (Resolve-Path $Build).Path
    if (-not (Test-Path (Join-Path $build 'Perch.exe'))) { throw "No Perch.exe in $build" }
    $tests = (Resolve-Path $PSScriptRoot).Path
    $config = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$build</HostFolder><SandboxFolder>C:\PerchBuild</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$tests</HostFolder><SandboxFolder>C:\PerchTests</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\PerchOut</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PerchTests\$Test $TestArgs</Command></LogonCommand>
</Configuration>
"@
    $wsb = Join-Path $out 'run.wsb'
    [IO.File]::WriteAllText($wsb, $config, (New-Object Text.UTF8Encoding $false))
    Write-Host "Starting a one-off Windows Sandbox ($Test); results in $out"
    Start-Process $sandbox -ArgumentList "`"$wsb`""
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path (Join-Path $out 'done.txt')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (Test-Path (Join-Path $out 'done.txt')) { Write-Host 'Finished.' } else { Write-Host "Timed out after $TimeoutMinutes minutes." }
if (-not $dev) { Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
if (Test-Path (Join-Path $out 'summary.txt')) { Get-Content (Join-Path $out 'summary.txt') } else { Write-Host 'No summary.txt was written.' }
Write-Host "Output folder: $out"
