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

THE RULE THIS SCRIPT EXISTS TO KEEP: never send the bridge a proposal that is not the
bytes DeepSeek Harness is about to write. A reconstruction that fails silently returns
the file unchanged, the diff shows nothing, the reviewer accepts, and the real edit
lands unreviewed - the gate still looks like it is working. So every path that cannot
faithfully rebuild the result emits NO decision instead of guessing, and DSH's own
permission flow answers. Failing open is only ever correct for "the bridge is absent";
it is never correct for "I could not tell what this change does".

Fail-open by design: if the bridge is missing or unreachable we emit "allow" so the
user is never blocked by a broken install. Note that DSH also treats a TIMED-OUT hook
as allow, which is why settings.json sets a 24h timeout for this hook.
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

# Emit no decision at all: DSH runs its own permission flow for this call.
function Defer() { exit 0 }

# --- line handling --------------------------------------------------------------------
# Mirrors DSH. Its normalizeLineEndings is exactly `.replaceAll("\r\n", "\n")`, and
# applyLiteralEdit matches on content normalised that way, so the hook must match on the
# same text or the two disagree about whether an edit applies.
function Normalize-Eol([string]$text) {
    if ($null -eq $text) { return '' }
    return $text.Replace("`r`n", "`n")
}
function Detect-Crlf([string]$raw) {
    if ($null -eq $raw) { return $false }
    $sample = if ($raw.Length -gt 4096) { $raw.Substring(0, 4096) } else { $raw }

    # Counted with single-character splits on purpose. PowerShell splits a string on *any*
    # character in the separator set, so [string].Split("`r`n") also splits on the bare LF -
    # which made an LF-only file look like CRLF and rewrote every edit with Windows endings.
    # This mirrors DSH's own detectLineEndings.
    $lfCount = ([regex]::Matches($sample, "`n")).Count
    $crlfCount = ([regex]::Matches($sample, "`r`n")).Count
    return $crlfCount -gt ($lfCount - $crlfCount)
}
function Restore-Eol([string]$text, [bool]$crlf) {
    if (-not $crlf) { return $text }
    return $text.Replace("`n", "`r`n")
}

# --- reconstruction -------------------------------------------------------------------
# A literal replacement, matching DSH's applyLiteralEdit.
#
# Returns $null when the call cannot be reproduced, so the caller defers instead of handing
# back an unchanged file. DSH throws in those cases (nothing is written), so deferring is
# the faithful outcome - the danger was answering with the current content, which made the
# diff empty and the reviewer approve something they never saw.
function ApplyLiteral([string]$content, [string]$old, [string]$new, [bool]$all) {
    $oldNorm = Normalize-Eol $old
    if ([string]::IsNullOrEmpty($oldNorm)) { return $null }

    $crlf = Detect-Crlf $content
    $body = Normalize-Eol $content
    $newNorm = Normalize-Eol $new

    $count = $body.Split([string[]]@($oldNorm), [StringSplitOptions]::None).Count - 1
    if ($count -eq 0) { return $null }
    # DSH refuses an ambiguous edit unless replace_all is set, so DSH would write nothing.
    if (-not $all -and $count -gt 1) { return $null }

    # DSH replaces every occurrence; replace_all only governs whether it is allowed to.
    $replaced = $body.Replace($oldNorm, $newNorm)
    return Restore-Eol $replaced $crlf
}

# Insert text after a line, matching how DSH applies insert_line.
# insert_line is 1-based: the text lands after that line, and 0 means before the first.
function ApplyInsert([string]$content, [int]$line, [string]$text) {
    $crlf = Detect-Crlf $content
    $body = Normalize-Eol $content
    $owner = Normalize-Eol $text

    $hasFinalEol = $body.EndsWith("`n")
    $core = if ($hasFinalEol) { $body.Substring(0, $body.Length - 1) } else { $body }

    # The trailing newline is not an extra line. Splitting "L1\nL2\n" leaves an empty final
    # element, which pushed the inserted text past the end of the file.
    $lines = if ($core.Length -eq 0) { @() } else { @($core.Split("`n")) }
    if ($line -lt 0 -or $line -gt $lines.Count) { return $null }

    $at = [Math]::Min($line, $lines.Count)
    $rebuilt = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $at; $i++) { [void]$rebuilt.Add($lines[$i]) }
    [void]$rebuilt.Add($owner)
    for ($i = $at; $i -lt $lines.Count; $i++) { [void]$rebuilt.Add($lines[$i]) }

    $joined = [string]::Join("`n", [string[]]$rebuilt.ToArray())
    if ($hasFinalEol) { $joined += "`n" }
    return Restore-Eol $joined $crlf
}

# The proposed content for a call, or $null when it cannot be rebuilt faithfully.
# $isRead is set for calls that only read; those must not be gated at all.
function Rebuild([string]$tool, $ti, [string]$current, [ref]$isRead) {
    $isRead.Value = $false

    if ($tool -eq 'write') {
        if ($null -eq $ti.content) { return $null }
        return [string]$ti.content
    }

    if ($tool -eq 'edit') {
        return ApplyLiteral $current ([string]$ti.old_string) ([string]$ti.new_string) ([bool]$ti.replace_all)
    }

    # str_replace_editor is four tools in one: view, create, str_replace, insert. Only
    # str_replace carries old_str, so the other commands each need their own reconstruction.
    # Treating them all as a literal replacement is what made the diff come out empty.
    if ($tool -eq 'str_replace_editor') {
        $command = [string]$ti.command

        if ($command -eq 'view') { $isRead.Value = $true; return $null }

        # A null placeholder means "omitted" to DSH, so an explicitly null command falls
        # back to str_replace.
        if ([string]::IsNullOrEmpty($command) -or $command -eq 'str_replace') {
            # DSH defaults a null new_str to the empty string, i.e. a deletion.
            $new = if ($null -eq $ti.new_str) { '' } else { [string]$ti.new_str }
            return ApplyLiteral $current ([string]$ti.old_str) $new ([bool]$ti.replace_all)
        }

        if ($command -eq 'create') {
            # create refuses to overwrite, so DSH's result is exactly file_text.
            if ($null -eq $ti.file_text) { return $null }
            return [string]$ti.file_text
        }

        if ($command -eq 'insert') {
            if ($null -eq $ti.insert_line) { return $null }
            $text = if ($null -eq $ti.new_str) { '' } else { [string]$ti.new_str }
            return ApplyInsert $current ([int]$ti.insert_line) $text
        }

        return $null   # unknown command: let DSH decide
    }

    return $null
}

try {
    # DSH always writes the payload on stdin. The environment variable exists so the test
    # bench can drive this script without depending on how a parent PowerShell hands stdin
    # to a child; unset (the real case) means read stdin.
    $raw = $env:DSH_VS_HOOK_STDIN
    if ([string]::IsNullOrEmpty($raw)) {
        # Read stdin as UTF-8: the default console input encoding garbles non-ASCII.
        $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
        $raw = $stdin.ReadToEnd()
    }
    $p = $raw | ConvertFrom-Json

    # DSH tool names are LOWERCASE (write, edit, str_replace_editor), unlike
    # Claude Code's Write/Edit/MultiEdit. PowerShell's switch is case-insensitive,
    # so these labels still match, but keep them lowercase for clarity.
    $tool = [string]$p.tool_name

    if ($tool -ne 'write' -and $tool -ne 'edit' -and $tool -ne 'str_replace_editor') {
        Emit 'allow' "unhandled tool $tool"
    }

    $ti = $p.tool_input
    # str_replace_editor names its target `path`, not `file_path`.
    $file = [string]$ti.file_path
    if (-not $file) { $file = [string]$ti.path }
    if (-not $file) { Defer }

    $cur = if (Test-Path -LiteralPath $file) { Get-Content -Raw -LiteralPath $file -Encoding UTF8 } else { '' }
    if ($null -eq $cur) { $cur = '' }

    $isRead = $false
    $new = Rebuild $tool $ti $cur ([ref]$isRead)

    # A read is not a change, and DSH's own read path already covers permissions.
    if ($isRead) { Emit 'allow' 'read-only command' }

    # No faithful reconstruction means we do not know what would be written. Saying
    # nothing lets DSH ask; showing the reviewer an unchanged file would not.
    if ($null -eq $new) {
        Defer
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
        Defer
    }
    else {
        $why = if ($resp.reason) { [string]$resp.reason } else { 'Rejected in the Visual Studio diff' }
        Emit 'deny' $why
    }
}
catch {
    Emit 'allow' ("DeepSeek Harness VS hook error (allowing): " + $_.Exception.Message)
}
