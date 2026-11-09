# A tiny OpenAI-compatible server for tests (PowerShell only, nothing to install). Streams answers as server-sent events.
# The scenario comes from the last user message: with "DENY" in it the model asks for one action and gives up when declined;
# otherwise it asks for three (create a folder, open cmd, run a script) and then answers.
param([int]$Port = 8791, [string]$Log = 'C:\PerchOut\mock.log', [string]$Desktop = "$env:USERPROFILE\Desktop")

function Note($text) { Add-Content -Path $Log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text) -Encoding UTF8 }

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Note "mock listening on $Port"

function Send($context, $obj) {
    $line = "data: " + ($obj | ConvertTo-Json -Depth 12 -Compress) + "`n`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($line)
    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $context.Response.OutputStream.Flush()
}
function Text($context, $text) { Send $context @{ choices = @(@{ delta = @{ content = $text } }) } }
function Call($context, $name, $arguments) {
    Send $context @{ choices = @(@{ delta = @{ tool_calls = @(@{ index = 0; id = 'call_' + $name; type = 'function'; function = @{ name = $name; arguments = ($arguments | ConvertTo-Json -Compress) } }) } }) }
}

while ($listener.IsListening) {
    $context = $listener.GetContext()
    try {
        $path = $context.Request.Url.AbsolutePath.TrimEnd('/')
        if ($path -eq '/v1/models') {
            $body = [Text.Encoding]::UTF8.GetBytes('{"data":[{"id":"mock-agent"}]}')
            $context.Response.ContentType = 'application/json'; $context.Response.OutputStream.Write($body, 0, $body.Length)
        }
        elseif ($path -eq '/v1/chat/completions') {
            $raw = (New-Object IO.StreamReader($context.Request.InputStream, [Text.Encoding]::UTF8)).ReadToEnd()
            $req = $raw | ConvertFrom-Json
            $messages = @($req.messages)
            $tools = @($messages | ? { $_.role -eq 'tool' })
            $user = ($messages | ? { $_.role -eq 'user' } | Select -Last 1).content
            Note ("chat: tools offered=" + (@($req.tools | % { $_.function.name }) -join ',') + " toolMessages=" + $tools.Count + " user=" + $user)
            foreach ($t in $tools) { Note ("  tool result: " + (($t.content -replace '\s+', ' ')).Substring(0, [Math]::Min(300, ($t.content -replace '\s+', ' ').Length))) }
            $context.Response.ContentType = 'text/event-stream'; $context.Response.SendChunked = $true
            $deny = "$user" -like '*DENY*'
            switch ($tools.Count) {
                0 {
                    Text $context 'OK. '
                    if ($deny) { Call $context 'create_folder' @{ path = (Join-Path $Desktop 'PerchDenied') } }
                    else { Call $context 'create_folder' @{ path = (Join-Path $Desktop 'PerchTestFolder') } }
                }
                1 { if ($deny) { Text $context 'Understood, I did not do it.' } else { Call $context 'open_app' @{ target = 'cmd' } } }
                2 { Call $context 'run_powershell' @{ script = 'Write-Output ("hello from " + $env:COMPUTERNAME); Write-Output $PSVersionTable.PSVersion.Major' } }
                default { Text $context 'All three steps are done.' }
            }
            Send $context @{ choices = @(@{ delta = @{}; finish_reason = 'stop' }) }
            $done = [Text.Encoding]::UTF8.GetBytes("data: [DONE]`n`n"); $context.Response.OutputStream.Write($done, 0, $done.Length)
        }
        else { $context.Response.StatusCode = 404 }
    }
    catch { Note ("mock error: " + $_) }
    finally { try { $context.Response.Close() } catch { } }
}
