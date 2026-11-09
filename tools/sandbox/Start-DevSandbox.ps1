<#
  Opens a Windows Sandbox that STAYS open for development. Tests are then sent to it with Run-SandboxTest.ps1 (which notices
  the running sandbox and uses it instead of starting a new one), and the window can be used by hand in between.

  Inside:  C:\PerchBuild = the host's build folder, live (read only)     C:\PerchTests = tools\sandbox (read only)
           C:\PerchOut   = <repo>\artifacts\sandbox-out (read/write): requests, results and screenshots
  Close the sandbox window when done; everything inside is thrown away.
#>
param([string]$Build = (Join-Path $PSScriptRoot '..\..\artifacts\x64-latest'))
$ErrorActionPreference = 'Stop'
$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandbox)) { throw 'Windows Sandbox is not installed (Windows features > Windows Sandbox).' }
if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox is already open; only one can run at a time.' }
$build = (Resolve-Path $Build).Path
$tests = (Resolve-Path $PSScriptRoot).Path
$outRoot = Join-Path $PSScriptRoot '..\..\artifacts\sandbox-out'
New-Item -ItemType Directory -Force $outRoot | Out-Null
$outRoot = (Resolve-Path $outRoot).Path
Remove-Item (Join-Path $outRoot 'watching.txt') -Force -ErrorAction SilentlyContinue

$config = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$build</HostFolder><SandboxFolder>C:\PerchBuild</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$tests</HostFolder><SandboxFolder>C:\PerchTests</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outRoot</HostFolder><SandboxFolder>C:\PerchOut</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PerchTests\watch.ps1</Command></LogonCommand>
</Configuration>
"@
$wsb = Join-Path $outRoot 'dev.wsb'
[IO.File]::WriteAllText($wsb, $config, (New-Object Text.UTF8Encoding $false))
Write-Host 'Starting the development sandbox...'
Start-Process $sandbox -ArgumentList "`"$wsb`""
$deadline = (Get-Date).AddMinutes(3)
while (-not (Test-Path (Join-Path $outRoot 'watching.txt')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (Test-Path (Join-Path $outRoot 'watching.txt')) { Write-Host 'Ready. Send tests with Run-SandboxTest.ps1; close the sandbox window when finished.' } else { Write-Host 'The sandbox did not report in within 3 minutes.' }
