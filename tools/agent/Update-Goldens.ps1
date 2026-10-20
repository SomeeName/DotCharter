<#
.SYNOPSIS
    Stages new golden screenshots for review, or - for the OWNER only - accepts
    the staged ones into DotCharter.Tests/Goldens/ui/.

.DESCRIPTION
    docs/agents/ui/target-architecture.md section 5.

    -Stage   runs the golden harness (Invoke-Gate.ps1 -Goldens) with
             DOTCHARTER_GOLDEN_STAGE_DIR set, so every screen x variant is
             rendered into the staging folder beside a status file, and writes
             index.html there: one row per NEW or CHANGED screen with the
             approved image (before), the render (after) and the diff. The
             harness run itself fails while any screen has no approved golden;
             that is expected here and does not stop the contact sheet.

    -Accept  copies the staged NEW and CHANGED PNGs into
             DotCharter.Tests/Goldens/ui/ and writes each one's sidecar
             (<name>.json: commit, acceptedOn, sha256). It ASKS FIRST with
             Read-Host and refuses outright when stdin is not an interactive
             console - agent tool shells run with stdin on the null device, so
             an agent cannot accept a baseline, even by mistake. Only the owner
             accepts; a PR touching Goldens/** is reviewed by opening its
             contact sheet.

    Runs under Windows PowerShell 5.1 and pwsh 7.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Update-Goldens.ps1 -Stage
    powershell -NoProfile -File tools/agent/Update-Goldens.ps1 -Accept     # owner, interactive console only
#>
[CmdletBinding()]
param(
    [switch]$Stage,
    [switch]$Accept,
    # Skip the build before -Stage (only when you have JUST built).
    [switch]$NoBuild,
    [string]$StageDir,
    [string]$GoldensDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $StageDir) { $StageDir = Join-Path $repoRoot 'artifacts\ui-goldens\staged' }
if (-not $GoldensDir) { $GoldensDir = Join-Path $repoRoot 'DotCharter.Tests\Goldens\ui' }
if ($Stage -eq $Accept) {
    Write-Host 'Pass exactly one of -Stage or -Accept.'
    exit 2
}

$utf8 = New-Object System.Text.UTF8Encoding $false

function Get-StagedCases {
    if (-not (Test-Path -LiteralPath $StageDir)) { return @() }
    Get-ChildItem -LiteralPath $StageDir -Recurse -Filter '*.stage.json' | ForEach-Object {
        $s = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{
            Relative = [string]$s.relative
            Status   = [string]$s.status
            Share    = [double]$s.differingShare
            Png      = Join-Path $StageDir $s.relative
        }
    } | Sort-Object Relative
}

function ConvertTo-FileUri([string]$Path) { ([System.Uri](Resolve-Path -LiteralPath $Path).Path).AbsoluteUri }

if ($Stage) {
    if (Test-Path -LiteralPath $StageDir) { Remove-Item -LiteralPath $StageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

    $gateArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Invoke-Gate.ps1'), '-Goldens')
    if ($NoBuild) { $gateArgs += '-NoBuild' }
    $env:DOTCHARTER_GOLDEN_STAGE_DIR = $StageDir
    try { & powershell @gateArgs | Out-Host }
    finally { Remove-Item Env:\DOTCHARTER_GOLDEN_STAGE_DIR -ErrorAction SilentlyContinue }

    $cases = @(Get-StagedCases)
    if ($cases.Count -eq 0) {
        Write-Host "nothing was staged under $StageDir - the harness did not run; read the goldens log above."
        exit 1
    }

    $review = @($cases | Where-Object { $_.Status -ne 'same' })
    $rows = New-Object System.Text.StringBuilder
    foreach ($c in $review) {
        $stem = $c.Relative.Substring(0, $c.Relative.Length - 4)
        $golden = Join-Path $GoldensDir $c.Relative
        $before = if (Test-Path -LiteralPath $golden) { "<img src=`"$(ConvertTo-FileUri $golden)`">" } else { '<div class="none">no approved golden</div>' }
        $diff = if ($c.Status -eq 'changed') { "<img src=`"$stem.diff.png`">" } else { '<div class="none">-</div>' }
        $share = '{0:P3}' -f $c.Share
        [void]$rows.Append("<h2>$($c.Relative) <span>$($c.Status)$(if ($c.Status -eq 'changed') { " - $share of pixels differ" })</span></h2>")
        [void]$rows.Append("<div class=`"row`"><figure>$before<figcaption>before</figcaption></figure>")
        [void]$rows.Append("<figure><img src=`"$($c.Relative)`"><figcaption>after</figcaption></figure>")
        [void]$rows.Append("<figure>$diff<figcaption>diff</figcaption></figure></div>`n")
    }

    $new = @($cases | Where-Object { $_.Status -eq 'new' }).Count
    $changed = @($cases | Where-Object { $_.Status -eq 'changed' }).Count
    $same = @($cases | Where-Object { $_.Status -eq 'same' }).Count
    $html = "<!doctype html><meta charset=`"utf-8`"><title>UI goldens - staged</title>" +
        '<style>body{background:#111;color:#eee;font:13px system-ui;margin:24px}h2{font:600 14px monospace;margin:28px 0 8px}' +
        'h2 span{color:#e8a;font-weight:400}.row{display:flex;gap:14px}figure{margin:0}img{width:520px;border:1px solid #444;display:block}' +
        '.none{width:520px;height:292px;border:1px dashed #444;display:flex;align-items:center;justify-content:center;color:#888}</style>' +
        "<h1>UI goldens - staged for review</h1><p>$new new, $changed changed, $same unchanged. Accept with " +
        '<code>tools/agent/Update-Goldens.ps1 -Accept</code> from an interactive console.</p>' + "`n" + $rows.ToString()
    $sheet = Join-Path $StageDir 'index.html'
    [System.IO.File]::WriteAllText($sheet, $html, $utf8)

    Write-Host "staged $($cases.Count) screens: $new new, $changed changed, $same unchanged - contact sheet: $sheet"
    exit 0
}

# -Accept
$cases = @(Get-StagedCases | Where-Object { $_.Status -ne 'same' })
if ($cases.Count -eq 0) {
    Write-Host "nothing to accept - stage first (Update-Goldens.ps1 -Stage); staging folder: $StageDir"
    exit 0
}

Write-Host "$($cases.Count) staged screen(s) would be copied into $GoldensDir :"
$cases | ForEach-Object { Write-Host "  $($_.Status.PadRight(8)) $($_.Relative)" }

if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive) {
    Write-Host 'REFUSED: -Accept asks for confirmation and needs an interactive console. Nothing was copied.'
    exit 3
}
$answer = Read-Host "Type ACCEPT to approve these $($cases.Count) baseline(s)"
if ($answer -cne 'ACCEPT') {
    Write-Host 'Not accepted. Nothing was copied.'
    exit 3
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
$acceptedOn = (Get-Date).ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
foreach ($c in $cases) {
    $target = Join-Path $GoldensDir $c.Relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $c.Png -Destination $target -Force
    $sha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $json = "{`n  `"commit`": `"$commit`",`n  `"acceptedOn`": `"$acceptedOn`",`n  `"sha256`": `"$sha`"`n}`n"
    [System.IO.File]::WriteAllText([System.IO.Path]::ChangeExtension($target, '.json'), $json, $utf8)
}
Write-Host "accepted $($cases.Count) golden(s) into $GoldensDir - commit them with their .json sidecars."
exit 0
