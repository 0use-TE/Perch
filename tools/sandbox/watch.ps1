# Runs INSIDE a long-lived Windows Sandbox (started by Start-DevSandbox.ps1). It waits for test requests that the host
# drops into C:\PerchOut\requests and runs each one, so the sandbox can stay open for development while tests come and go.
# C:\PerchBuild is the host's build folder, mapped live: a new build on the host is what the next test runs.
$root = 'C:\PerchOut'
New-Item -ItemType Directory "$root\requests" -Force | Out-Null
'Windows Sandbox for Perch development. Tests run here when the host asks; this window can stay open.' | Write-Host
while ($true) {
    (Get-Date).ToString('o') | Set-Content "$root\watching.txt"
    foreach ($request in Get-ChildItem "$root\requests" -Filter *.json -ErrorAction SilentlyContinue | Sort Name) {
        $job = Get-Content $request.FullName -Raw | ConvertFrom-Json
        Remove-Item $request.FullName -Force
        Write-Host ("{0:HH:mm:ss}  running {1} {2}" -f (Get-Date), $job.test, $job.args)
        $env:PERCH_OUT = $job.out
        $env:PERCH_KEEP = '1'
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "C:\PerchTests\$($job.test)") + @("$($job.args)".Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
        $test = Start-Process powershell -ArgumentList $arguments -PassThru -WindowStyle Minimized
        while (-not $test.HasExited) { (Get-Date).ToString('o') | Set-Content "$root\watching.txt"; Start-Sleep -Seconds 2 }   # still alive while a test runs
    }
    Start-Sleep -Seconds 2
}
