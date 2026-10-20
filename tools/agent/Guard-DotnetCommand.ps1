<#
.SYNOPSIS
    PreToolUse hook for the Bash/PowerShell tools: refuses a raw `dotnet test`
    or `dotnet build` and points the agent at tools/agent/Invoke-Gate.ps1,
    which prints counts instead of the runner's full console output.

.DESCRIPTION
    Claude Code pipes the tool call as JSON on stdin. Exit 2 blocks the call and
    feeds stderr back to the model as the reason.

    The command is split into its shell segments (`;`, `&&`, `||`, `|`, newlines)
    and each segment that starts a `dotnet test` / `dotnet build` invocation is
    judged ON ITS OWN, after its trailing `#` comment is stripped. Only two
    things let such a segment through: `--list-tests` and `--help` as real
    arguments of that invocation. Text elsewhere in the command (another
    segment, a comment, an echo) cannot vouch for it. The wrapper and the
    release/CI scripts run their own `dotnet` from inside PowerShell, which this
    hook never sees, so they need no allowlist entry.

    Runs under Windows PowerShell 5.1 and pwsh 7.
#>
$ErrorActionPreference = 'Stop'
$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }
$command = [string]$payload.tool_input.command
if (-not $command) { exit 0 }

$offending = @()
foreach ($segment in ($command -split '(?:\r?\n|;|&&|\|\||\|)')) {
    $s = $segment.Trim()
    # Bash trailing comment: a `#` that starts the segment or follows whitespace.
    $s = ($s -replace '(^|\s)#.*$', '').Trim()
    if ($s -notmatch '^(?:\S*[\\/])?dotnet(?:\.exe)?\s+(test|build)\b') { continue }
    if ($s -match '(^|\s)--(list-tests|help)(\s|$)') { continue }
    $offending += $s
}
if ($offending.Count -eq 0) { exit 0 }

[Console]::Error.WriteLine(
    "Raw 'dotnet test' / 'dotnet build' output is not allowed in this session (blocked: " + ($offending -join ' | ') + ").`n" +
    "Use the quiet wrapper instead:`n" +
    "  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1                 # build + gate`n" +
    "  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless       # build + headless suite, own process`n" +
    "  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass '*SomeTests,*OtherTests'`n" +
    "It prints pass/fail/skip counts and, on failure, the test name plus the first line of the message. Full logs land in `$env:TEMP\dotcharter-gate\.")
exit 2
