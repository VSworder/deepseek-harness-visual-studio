<#
DeepSeek Harness <-> Visual Studio bridge: PreToolUse permission hook.

This script is shipped INSIDE the DeepSeekHarness for Visual Studio extension and
installed to %LOCALAPPDATA%\DeepSeekHarness\vs-bridge\ by the extension itself.
It is deliberately installed outside the user's repository: DeepSeek Harness runs
command hooks through Git Bash on Windows, and an '&' in a repository path is a
bash command separator (e.g. D:\WorkProject\Unity\Move&Jump), which would make the
hook silently never run.

Contract (verified against DeepSeek Harness 0.2.0-rc.2):
  - DSH writes one JSON object on stdin:
      { tool_name, tool_input, cwd, permission_mode?, transcript_path? }
  - DSH reads ONE JSON line on stdout:
      { "hookSpecificOutput": { "hookEventName": "PreToolUse",
                                "permissionDecision": "allow"|"deny"|"ask",
                                "permissionDecisionReason": "..." } }
  - exit 0 always.

Fail-open by design: if anything goes wrong we emit "allow" so the user is never
blocked by a broken bridge. Note that DSH also treats a TIMED-OUT hook as allow,
which is why settings.json sets a 24h timeout for this hook.
#>
$ErrorActionPreference = 'Stop'

function Emit([string]$decision, [string]$reason) {
    @{ hookSpecificOutput = @{
        hookEventName = 'PreToolUse'
        permissionDecision = $decision
        permissionDecisionReason = $reason
    } } | ConvertTo-Json -Compress -Depth 8
    exit 0
}

function ApplyEdit([string]$content, [string]$old, [string]$new, [bool]$all) {
    if ([string]::IsNullOrEmpty($old)) { return $content }
    # Match the file's newline convention so the diff is not a sea of line-ending changes.
    if ($content -match "`r`n") {
        $old = $old -replace "`n", "`r`n"
        $new = $new -replace "`n", "`r`n"
    }
    if ($all) { return $content.Replace($old, $new) }
    $idx = $content.IndexOf($old)
    if ($idx -lt 0) { return $content }   # not found (e.g. newline mismatch) -> leave unchanged
    return $content.Substring(0, $idx) + $new + $content.Substring($idx + $old.Length)
}

try {
    # Read stdin as UTF-8: the default console input encoding garbles non-ASCII.
    $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $p = $stdin.ReadToEnd() | ConvertFrom-Json

    # DSH tool names are LOWERCASE (write, edit, str_replace_editor), unlike
    # Claude Code's Write/Edit/MultiEdit. PowerShell's switch is case-insensitive,
    # so these labels still match, but keep them lowercase for clarity.
    $tool = [string]$p.tool_name
    $ti = $p.tool_input
    $file = [string]$ti.file_path
    if (-not $file) { $file = [string]$ti.path }

    $cur = if ($file -and (Test-Path -LiteralPath $file)) { Get-Content -Raw -LiteralPath $file -Encoding UTF8 } else { '' }
    switch ($tool) {
        'write'              { $new = [string]$ti.content }
        'edit'               { $new = ApplyEdit $cur $ti.old_string $ti.new_string ([bool]$ti.replace_all) }
        'str_replace_editor' { $new = ApplyEdit $cur $ti.old_str $ti.new_str ([bool]$ti.replace_all) }
        default              { Emit 'allow' "unhandled tool $tool" }
    }

    # Locate the live bridge. The extension writes one lock file per VS instance:
    #   %LOCALAPPDATA%\DeepSeekHarness\vs-bridge\<port>.lock
    #   { "port": <int>, "authToken": "<guid>", "pid": <int>, "workspaceFolders": ["..."] }
    # Selection mirrors the extension: longest workspace match wins, and the port
    # must actually be listening (a stale lock must never win discovery).
    function Test-BridgePort([int]$pt) {
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $live = $c.BeginConnect('127.0.0.1', $pt, $null, $null).AsyncWaitHandle.WaitOne(300) -and $c.Connected
            $c.Close(); return $live
        } catch { return $false }
    }

    $bridgeDir = Join-Path $env:LOCALAPPDATA 'DeepSeekHarness\vs-bridge'
    if (-not (Test-Path $bridgeDir)) { Emit 'allow' 'no DeepSeek Harness VS bridge installed' }

    $cwd = [string]$p.cwd
    $cands = @()
    foreach ($f in Get-ChildItem $bridgeDir -Filter '*.lock' -ErrorAction SilentlyContinue) {
        try {
            $j = Get-Content -Raw $f.FullName | ConvertFrom-Json
            # Skip lock files owned by a dead VS instance.
            if ($j.pid) {
                $alive = $null -ne (Get-Process -Id ([int]$j.pid) -ErrorAction SilentlyContinue)
                if (-not $alive) { continue }
            }
            $ws = if ($j.workspaceFolders) { [string]$j.workspaceFolders[0] } else { '' }
            # Separator-aware, both-way containment, ranked: exact > session inside
            # workspace > workspace inside session. Guards against 'C:\work\app'
            # matching a sibling 'C:\work\app-service'.
            $wsN = ($ws -replace '/', '\').TrimEnd('\')
            $cwdN = ($cwd -replace '/', '\').TrimEnd('\')
            $rank = 0
            if ($wsN -and $cwdN) {
                if     ($cwdN -eq $wsN)            { $rank = 3 }
                elseif ($cwdN -like ($wsN + '\*')) { $rank = 2 }
                elseif ($wsN -like ($cwdN + '\*')) { $rank = 1 }
            }
            $cands += [pscustomobject]@{
                Port  = [int]$f.BaseName
                Token = [string]$j.authToken
                Score = ($rank * 1000000 + $ws.Length)
            }
        } catch { }
    }

    $port = $null; $token = $null
    foreach ($cand in ($cands | Sort-Object Score -Descending)) {
        if (Test-BridgePort $cand.Port) { $port = $cand.Port; $token = $cand.Token; break }
    }
    if (-not $port) { Emit 'allow' 'no live DeepSeek Harness VS bridge found' }

    $body = @{
        filePath       = $file
        newContents    = $new
        cwd            = $cwd
        permissionMode = [string]$p.permission_mode
        transcriptPath = [string]$p.transcript_path
        pid            = $PID
    } | ConvertTo-Json -Compress -Depth 8

    # Explicit UTF-8 bytes: Invoke-RestMethod's default string encoding mangles non-ASCII.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$port/permission" -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'x-dsh-vs-authorization' = $token } `
        -Body $bytes -TimeoutSec 86400

    if ($resp.accept) {
        Emit 'allow' 'Accepted in the Visual Studio diff'
    }
    elseif ($resp.ask) {
        # The bridge declined to gate (session belongs to another workspace/IDE):
        # emit NO decision so DSH runs its own permission flow.
        exit 0
    }
    else {
        $why = if ($resp.reason) { [string]$resp.reason } else { 'Rejected in the Visual Studio diff' }
        Emit 'deny' $why
    }
}
catch {
    Emit 'allow' ("DeepSeek Harness VS hook error (allowing): " + $_.Exception.Message)
}
