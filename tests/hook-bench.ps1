# Test bench for the DeepSeek Harness PreToolUse hook.
#
# The hook decides what the reviewer sees in the diff. When it rebuilds the wrong bytes the
# diff comes out empty and the reviewer approves a change they never saw, so the
# reconstruction is asserted here rather than trusted.
#
# Two things this bench learned the hard way:
#   - PowerShell will not let you assign to $input (it is an automatic variable), and a
#     hashtable is indexed rather than dotted; both silently produced an empty payload and
#     made every case look like a hook failure.
#   - A PowerShell HttpListener as the stand-in bridge cannot be driven reliably from here
#     (the client sees a closed connection). The bench uses tests\fake-bridge.mjs instead.
#
# Each case asserts the hook's decision and, when the hook reaches the bridge, the exact
# newContents the bridge was asked to approve. Cases expecting no decision assert that too:
# "I could not tell what this writes" must never be answered with a silent allow.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$hook = Join-Path $repoRoot 'scripts\vs-permission-hook.ps1'
$fakeBridge = Join-Path $PSScriptRoot 'fake-bridge.mjs'
foreach ($required in @($hook, $fakeBridge)) {
    if (-not (Test-Path $required)) { throw "missing: $required" }
}

# Sentinel for "the hook must emit no decision at all", and for "this case must not reach
# the bridge". $null cannot express either: PowerShell's -ne on two $nulls is false, so the
# cases compared equal and then fell through to the wrong assertion.
$Defer = '<no decision>'
$NoBridge = '<must not reach the bridge>'

$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw 'node is required for the bench (the stand-in bridge runs on it)' }

# The hook reaches the bridge only when a lock file names a live port, so the bench stands up
# a throwaway bridge and advertises it the same way the extension does.
$port = Get-Random -Minimum 53600 -Maximum 53900
$token = [guid]::NewGuid().ToString()
$bridgeDir = Join-Path $env:LOCALAPPDATA 'DeepSeekHarness\vs-bridge'
New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null
$lockPath = Join-Path $bridgeDir "$port.lock"
$bridgeLog = Join-Path $env:TEMP "hook-bench-bridge-$port.jsonl"
$bridgeOut = Join-Path $env:TEMP "hook-bench-node-$port.txt"

@{ port = $port; authToken = $token; pid = $PID; workspaceFolders = @($PSScriptRoot) } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath $lockPath -Encoding UTF8

$bridge = Start-Process -FilePath $node `
    -ArgumentList @($fakeBridge, $port, $token, $PSScriptRoot, $bridgeLog) `
    -NoNewWindow -PassThru -RedirectStandardOutput $bridgeOut

$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $bridgeOut) -and (Get-Content -Raw $bridgeOut) -match 'READY') { break }
    Start-Sleep -Milliseconds 150
}
if (-not ((Get-Content -Raw $bridgeOut -ErrorAction SilentlyContinue) -match 'READY')) {
    throw 'the stand-in bridge did not start'
}

function Invoke-Hook($payload) {
    # Serialised explicitly: a hashtable piped into ConvertTo-Json enumerates into an array.
    $fields = [ordered]@{}
    $source = $payload['tool_input']
    foreach ($key in @($source.Keys)) { $fields[$key] = $source[$key] }

    $envelope = [ordered]@{ tool_name = $payload['tool_name']
                            tool_input = $fields
                            cwd = $payload['cwd']
                            transcript_path = $payload['transcript_path'] }
    $json = ConvertTo-Json -InputObject $envelope -Compress -Depth 12

    # The hook reads this in preference to stdin, so the bench does not depend on how a
    # parent PowerShell hands stdin to a child.
    $env:DSH_VS_HOOK_STDIN = $json
    try {
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = 'powershell.exe'
        $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $hook + '"'
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $info.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)

        $process = [System.Diagnostics.Process]::Start($info)
        $text = $process.StandardOutput.ReadToEnd()
        $errors = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        $process.Dispose()
    }
    finally {
        $env:DSH_VS_HOOK_STDIN = $null
    }

    $decision = $null
    $reason = $null
    if ($text -and $text.Trim()) {
        try {
            $parsed = $text.Trim() | ConvertFrom-Json
            $decision = $parsed.hookSpecificOutput.permissionDecision
            $reason = $parsed.hookSpecificOutput.permissionDecisionReason
        } catch { }
    }
    return [pscustomobject]@{ Decision = $decision; Reason = $reason; Raw = $text; Stderr = $errors; ExitCode = $exitCode }
}

function New-Proposal([string]$name, [string]$tool, $toolInput, [string]$fileBody, [string]$expectNew, [string]$expectDecision) {
    $file = Join-Path $PSScriptRoot "bench-$name.txt"
    if ($null -ne $fileBody) { [IO.File]::WriteAllText($file, $fileBody, (New-Object Text.UTF8Encoding($false))) }
    elseif (Test-Path $file) { Remove-Item $file -Force }

    # str_replace_editor names its target `path`; everything else uses `file_path`.
    $fields = @{}
    foreach ($key in $toolInput.Keys) { $fields[$key] = $toolInput[$key] }
    if ($fields.ContainsKey('path')) { $fields['path'] = $file } else { $fields['file_path'] = $file }

    # Truncate the bridge log so the assertions read only this case's request.
    Set-Content -LiteralPath $bridgeLog -Value '' -Encoding UTF8

    $result = Invoke-Hook @{ tool_name = $tool; tool_input = $fields; cwd = $PSScriptRoot; transcript_path = '' }

    $status = 'PASS'
    $detail = ''
    # Compared with an explicit null test: PowerShell's -ne on two $nulls is false, and its
    # string comparison rules make "" and $null awkward to distinguish.
    $decisionMatches = if ($expectDecision -ceq $Defer) { $null -eq $result.Decision }
                       else { $result.Decision -ceq $expectDecision }
    if (-not $decisionMatches) {
        $status = 'FAIL'
        $detail = "decision=[$($result.Decision)] expected=[$expectDecision]"
        if ($result.Reason) { $detail += " reason=[$($result.Reason)]" }
        if ($result.Stderr) { $detail += " stderr=[$($result.Stderr.Trim())]" }
    }
    elseif ($expectNew -ceq $NoBridge) {
        $lines = @(Get-Content -LiteralPath $bridgeLog -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
        if ($lines.Count -ne 0) { $status = 'FAIL'; $detail = 'the bridge should not have been called' }
    }
    elseif ($expectDecision -eq 'allow' -and $null -ne $expectNew) {
        $lines = @(Get-Content -LiteralPath $bridgeLog -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
        if ($lines.Count -eq 0) {
            $status = 'FAIL'; $detail = 'the bridge was never called'
        }
        else {
            $actual = ($lines[-1] | ConvertFrom-Json).body | ConvertFrom-Json
            if ($actual.newContents -cne $expectNew) {
                $status = 'FAIL'
                $expectedShown = $expectNew -replace "`r", '\r' -replace "`n", '\n'
                $actualShown = $actual.newContents -replace "`r", '\r' -replace "`n", '\n'
                $detail = "newContents mismatch`n          expected: $expectedShown`n          actual  : $actualShown"
            }
        }
    }
    if (Test-Path $file) { Remove-Item $file -Force }

    $color = if ($status -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $status, $name) -ForegroundColor $color
    if ($detail) { Write-Host "        $detail" -ForegroundColor $color }
    return $status -eq 'PASS'
}

Write-Host 'DeepSeek Harness hook bench' -ForegroundColor Cyan
$ok = $true

try {
    # --- write ---------------------------------------------------------------------------
    $ok = (New-Proposal 'write-new' 'write' @{ content = "hello`nworld`n" } $null "hello`nworld`n" 'allow') -and $ok

    # --- edit ----------------------------------------------------------------------------
    $ok = (New-Proposal 'edit-simple' 'edit' @{ old_string = 'aaa'; new_string = 'bbb' } "aaa`nccc`n" "bbb`nccc`n" 'allow') -and $ok

    # The defect that started this: old_string arriving with CRLF must still match a CRLF
    # file. The old code turned it into \r\r\n and then handed back the unchanged file.
    $ok = (New-Proposal 'edit-crlf' 'edit' @{ old_string = "aaa`r`nccc"; new_string = "bbb`r`nccc" } "aaa`r`nccc`r`n" "bbb`r`nccc`r`n" 'allow') -and $ok

    # A search text that cannot match must not come back as "unchanged"; it defers.
    $ok = (New-Proposal 'edit-nomatch' 'edit' @{ old_string = 'zzz'; new_string = 'yyy' } "aaa`n" $null $Defer) -and $ok

    # --- str_replace_editor: create -------------------------------------------------------
    # The tool names its target `path`. The old hook only looked at file_path, so this
    # resolved to an empty name and the reviewer was shown an empty diff.
    $ok = (New-Proposal 'sre-create' 'str_replace_editor' @{ command = 'create'; file_text = "brand`nnew`n" } $null "brand`nnew`n" 'allow') -and $ok

    # --- str_replace_editor: str_replace --------------------------------------------------
    $ok = (New-Proposal 'sre-replace' 'str_replace_editor' @{ command = 'str_replace'; old_str = 'one'; new_str = 'two' } "one`nthree`n" "two`nthree`n" 'allow') -and $ok

    # --- str_replace_editor: insert -------------------------------------------------------
    # insert_line is 1-based and the text goes after that line, matching DSH.
    $ok = (New-Proposal 'sre-insert-mid' 'str_replace_editor' @{ command = 'insert'; insert_line = 1; new_str = 'MID' } "L1`nL2`n" "L1`nMID`nL2`n" 'allow') -and $ok
    $ok = (New-Proposal 'sre-insert-first' 'str_replace_editor' @{ command = 'insert'; insert_line = 0; new_str = 'FIRST' } "L1`nL2`n" "FIRST`nL1`nL2`n" 'allow') -and $ok

    # --- str_replace_editor: view ---------------------------------------------------------
    # A read is not a change, and the hook says so without asking the bridge to gate it.
    $ok = (New-Proposal 'sre-view' 'str_replace_editor' @{ command = 'view' } "L1`n" $NoBridge 'allow') -and $ok

    # --- a replacement that is really a deletion -------------------------------------------
        # Deleting a whole line leaves its newline behind, so the faithful result is a blank
    # line - matching what DSH's split/join produces.
    $ok = (New-Proposal 'sre-delete' 'str_replace_editor' @{ command = 'str_replace'; old_str = 'gone'; new_str = '' } "keep`ngone`n" "keep`n`n" 'allow') -and $ok
    $ok = (New-Proposal 'sre-delete-line' 'str_replace_editor' @{ command = 'str_replace'; old_str = "gone`n"; new_str = '' } "keep`ngone`n" "keep`n" 'allow') -and $ok

    # --- a brand new file via str_replace_editor -------------------------------------------
    # No current file: the rebuild must come from file_text alone.
    $ok = (New-Proposal 'sre-create-absent' 'str_replace_editor' @{ command = 'create'; file_text = "fresh`n" } $null "fresh`n" 'allow') -and $ok

    # --- unhandled tool ---------------------------------------------------------------------
    # Not a file edit this hook gates, so it allows without waking the bridge.
    $ok = (New-Proposal 'other-tool' 'shell' @{ command = 'dir' } $null $NoBridge 'allow') -and $ok
}
finally {
    if ($bridge -and -not $bridge.HasExited) { Stop-Process -Id $bridge.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
    Remove-Item $bridgeLog, $bridgeOut -Force -ErrorAction SilentlyContinue
    Get-ChildItem $PSScriptRoot -Filter 'bench-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Host ''
if ($ok) { Write-Host 'All hook cases passed.' -ForegroundColor Green; exit 0 }
Write-Host 'Some hook cases FAILED.' -ForegroundColor Red
exit 1
