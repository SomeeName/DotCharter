#Requires -Version 7.0

<#
.SYNOPSIS
    The PURE half of Compare-Models.ps1 — catalogue parsing, model-id resolution,
    cost estimation, ranking and the Markdown writer.

.DESCRIPTION
    Everything here is a function of its arguments: no network, no dotnet, no
    files. That is deliberate — it is what lets the default gate execute these
    functions over canned inputs (DotCharter.Tests/Ai/Eval/ToneEvalCompareScriptTests.cs
    drives this module through pwsh), instead of the comparison harness being a
    script nobody can test until the moment it is spending money.

    The orchestration — running the eval once per model, at low priority, and
    collecting the artifacts — lives in Compare-Models.ps1 beside this file.
#>

Set-StrictMode -Version Latest

# ---------------------------------------------------------------- the shortlist

<#
.SYNOPSIS
    The default model shortlist, verified against https://openrouter.ai/api/v1/models
    on 2026-09-12. Re-verify rather than trusting this list: Compare-Models.ps1
    checks every id against the live catalogue on each run and DROPS what is gone.
#>
function Get-ToneEvalDefaultModel {
    [CmdletBinding()]
    param()

    # Chosen from DotCharter-local/scratch/ai-provider-pricing-quality-2026-09-12.md:
    # the three frontier families, the two cheap-and-strict ones, and the wildcard
    # value picks. Spread over price, not over vendor.
    @(
        'anthropic/claude-sonnet-5'
        'anthropic/claude-opus-5'
        'anthropic/claude-haiku-4.5'
        'openai/gpt-5.6-terra'
        'openai/gpt-5.6-luna'
        'google/gemini-3.8-flash'
        'x-ai/grok-4.6'
        'x-ai/grok-4.3'
        'deepseek/deepseek-v4-flash'
        'moonshotai/kimi-k3'
        'qwen/qwen3.8-flash'
        'z-ai/glm-5.3-flash'
        'mistralai/mistral-large-2512'
    )
}

<#
.SYNOPSIS
    Ids that MOVED, mapped to what they are called today.

.DESCRIPTION
    An explicit table, never a guess. Resolving 'mistralai/mistral-large-3' by
    stripping the version and matching 'mistralai/mistral-large' would have picked
    the 2411 model at $2/$6 per MTok instead of Large 3 at $0.50/$1.50 — a 4x
    price error, silently, in a harness whose whole job is comparing price against
    quality. So a rename is a decision written down here, and anything else is
    DROPPED with its near misses printed.
#>
function Get-ToneEvalModelAlias {
    [CmdletBinding()]
    param()

    @{
        # OpenRouter names Mistral Large 3 by its date stamp.
        'mistralai/mistral-large-3' = 'mistralai/mistral-large-2512'
    }
}

# ------------------------------------------------------------------- catalogue

<#
.SYNOPSIS
    Turns an OpenRouter /api/v1/models payload into id -> price/context rows.

.PARAMETER Payload
    The parsed body (ConvertFrom-Json of the endpoint's response). Prices there
    are USD PER TOKEN as strings; they come back here per MILLION tokens, which is
    the unit every other price in this repo uses (ToneEvalOptions, the pricing
    report, the eval's own report header).
#>
function ConvertFrom-OpenRouterCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Payload)

    $catalog = [ordered]@{}
    if ($null -eq $Payload -or -not $Payload.PSObject.Properties.Name.Contains('data')) { return $catalog }

    foreach ($model in $Payload.data) {
        if ($null -eq $model -or -not $model.PSObject.Properties.Name.Contains('id')) { continue }

        $inPrice = $null
        $outPrice = $null
        if ($model.PSObject.Properties.Name.Contains('pricing') -and $null -ne $model.pricing) {
            $inPrice = ConvertTo-UsdPerMTok $model.pricing.prompt
            $outPrice = ConvertTo-UsdPerMTok $model.pricing.completion
        }

        $catalog[[string]$model.id] = [pscustomobject]@{
            Id             = [string]$model.id
            Name           = if ($model.PSObject.Properties.Name.Contains('name')) { [string]$model.name } else { '' }
            InUsdPerMTok   = $inPrice
            OutUsdPerMTok  = $outPrice
            ContextLength  = if ($model.PSObject.Properties.Name.Contains('context_length')) { $model.context_length } else { $null }
        }
    }

    return $catalog
}

function ConvertTo-UsdPerMTok {
    param($PerToken)

    if ($null -eq $PerToken) { return $null }
    $value = 0.0
    if (-not [double]::TryParse([string]$PerToken, [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref] $value)) {
        return $null
    }
    return $value * 1e6
}

<#
.SYNOPSIS
    Resolves each requested id against the catalogue: ok / renamed / missing.

.DESCRIPTION
    Never guesses a rename (see Get-ToneEvalModelAlias). A missing id comes back
    with Status 'missing' and a Note naming the same-vendor ids that DO exist, so
    the owner can fix the list in one read instead of opening a browser.
#>
function Resolve-ToneEvalModel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Requested,
        [Parameter(Mandatory)] $Catalog,
        $Alias = $null
    )

    if ($null -eq $Alias) { $Alias = Get-ToneEvalModelAlias }
    $ids = @($Catalog.Keys)

    foreach ($request in $Requested) {
        $wanted = ([string]$request).Trim()
        if ([string]::IsNullOrWhiteSpace($wanted)) { continue }

        $resolved = $null
        $status = 'missing'
        $note = ''

        if ($Catalog.Contains($wanted)) {
            $resolved = $wanted
            $status = 'ok'
        }
        elseif ($Alias.ContainsKey($wanted) -and $Catalog.Contains($Alias[$wanted])) {
            $resolved = $Alias[$wanted]
            $status = 'renamed'
            $note = "renamed from '$wanted' (alias table)"
        }
        else {
            # Case is the one difference safe to fix without a decision.
            $insensitive = @($ids | Where-Object { $_ -ieq $wanted })
            if ($insensitive.Count -eq 1) {
                $resolved = $insensitive[0]
                $status = 'renamed'
                $note = "renamed from '$wanted' (case)"
            }
            else {
                $vendor = ($wanted -split '/')[0]
                $near = @($ids | Where-Object { $_ -like "$vendor/*" } | Sort-Object | Select-Object -First 5)
                $note = if ($near.Count -gt 0) {
                    "not in the OpenRouter catalogue; same-vendor ids include: " + ($near -join ', ')
                }
                else {
                    'not in the OpenRouter catalogue, and no id shares its vendor prefix'
                }
            }
        }

        $entry = if ($null -ne $resolved) { $Catalog[$resolved] } else { $null }
        [pscustomobject]@{
            Requested     = $wanted
            Id            = $resolved
            Status        = $status
            Note          = $note
            InUsdPerMTok  = if ($null -ne $entry) { $entry.InUsdPerMTok } else { $null }
            OutUsdPerMTok = if ($null -ne $entry) { $entry.OutUsdPerMTok } else { $null }
        }
    }
}

# ------------------------------------------------------------------- estimating

<#
.SYNOPSIS
    The pre-flight cost estimate, per model and in total.

.DESCRIPTION
    "Print the cost before spending it" is the eval's own rule
    (ToneEvalOptions.EstimateUsd, ~6,000 in / ~900 out per call), mirrored here so
    a thirteen-model sweep is priced before the first request rather than by an
    invoice. The token assumptions default to the SAME two numbers and are
    parameters so a measured run can correct them.
#>
function Get-ToneEvalCostEstimate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Models,
        [Parameter(Mandatory)] [int] $Calls,
        [int] $InputTokensPerCall = 6000,
        [int] $OutputTokensPerCall = 900
    )

    foreach ($model in $Models) {
        $usd = $null
        if ($null -ne $model.InUsdPerMTok -and $null -ne $model.OutUsdPerMTok) {
            $usd = $Calls * ($InputTokensPerCall / 1e6 * $model.InUsdPerMTok +
                             $OutputTokensPerCall / 1e6 * $model.OutUsdPerMTok)
        }

        [pscustomobject]@{
            Id            = $model.Id
            Calls         = $Calls
            InUsdPerMTok  = $model.InUsdPerMTok
            OutUsdPerMTok = $model.OutUsdPerMTok
            EstimateUsd   = $usd
        }
    }
}

# ------------------------------------------------------------------ log hygiene

<#
.SYNOPSIS
    The one line the comparison harness parses out of an eval run's console.

.DESCRIPTION
    ToneDesignLiveEvalTests prints '[ai-eval] artifacts: <dir>' (see
    ToneEvalConsoleLines in the test project — the prefix lives there, once). The
    LAST match wins: a testhost that retried would print two, and the newest run
    is the one whose results.json is being read.
#>
function Get-ToneEvalArtifactDirectory {
    [CmdletBinding()]
    param([string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return $null }

    # The literal here is ToneEvalConsoleLines.ArtifactPrefix, regex-escaped, and
    # ToneEvalCompareScriptTests pins the two against each other.
    $matched = [regex]::Matches($Text, '(?m)^\s*\[ai-eval] artifacts:\s*(?<dir>\S.*?)\s*$')
    if ($matched.Count -eq 0) { return $null }
    return $matched[$matched.Count - 1].Groups['dir'].Value
}

<#
.SYNOPSIS
    Removes every secret from a block of text before it reaches a log or a report.

.DESCRIPTION
    A key reaches the child process through the environment, so it should never
    appear in its output — but "should never" is not a guarantee, and a 400 from a
    gateway that echoes the Authorization header would put it in a file the owner
    then pastes into a bug report. Replacement is unconditional and by value.
#>
function Remove-ToneEvalSecret {
    [CmdletBinding()]
    param(
        [string] $Text,
        [string[]] $Secret
    )

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    foreach ($value in $Secret) {
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        $Text = $Text.Replace($value, '***REDACTED***')
    }
    return $Text
}

<#
.SYNOPSIS
    The one line worth putting in a failure row, dug out of a run's console.

.DESCRIPTION
    A failed run is a ROW, not a crash — so the row has to say WHY in one line a
    human can act on: an auth rejection, a 400 on the JSON schema, a skip because
    the environment was half-configured. The first interesting line wins and it is
    truncated; the whole log is beside compare.md either way.
#>
function Get-ToneEvalFailureReason {
    [CmdletBinding()]
    param([string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return 'no output was captured from dotnet test.' }

    $lines = @($Text -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })

    # Ordered by how much they tell you, not by where they appear: a run that both
    # skipped and printed a stack trace was skipped.
    $patterns = @(
        'opt-in'
        'refused the configuration'
        'every single request failed'
        '(?i)\b(401|403|404|429|5\d\d)\b'
        '(?i)(unauthorized|forbidden|invalid[_ ]api[_ ]key|insufficient|quota|rate.?limit)'
        '(?i)ToneDesignException|ToneDesignFault'
        '(?i)\b(exception|error)\b'
    )

    foreach ($pattern in $patterns) {
        $hit = @($lines | Where-Object { $_ -match $pattern })
        if ($hit.Count -gt 0) { return (Limit-Length $hit[0] 400) }
    }

    return (Limit-Length $lines[$lines.Count - 1] 400)
}

function Limit-Length {
    param([string] $Value, [int] $Max)

    if ($null -eq $Value) { return '' }
    if ($Value.Length -le $Max) { return $Value }
    return $Value.Substring(0, $Max) + '…'
}

# --------------------------------------------------------------------- the table

<#
.SYNOPSIS
    One comparison row from one model's run.

.PARAMETER Run
    The parsed results.json (a ToneEvalRun). Null for a model whose run never
    produced one — that is a row with Status 'failed' and a reason, never a
    crash of the whole comparison.
#>
function ConvertTo-ToneEvalCompareRow {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Model,
        $Run = $null,
        [string] $Status = 'ok',
        [string] $Failure = '',
        [double] $WallSeconds = 0,
        $InUsdPerMTok = $null,
        $OutUsdPerMTok = $null
    )

    if ($null -eq $Run) {
        return [pscustomobject]@{
            Model         = $Model
            Status        = if ($Status -eq 'ok') { 'failed' } else { $Status }
            Failure       = $Failure
            Songs         = 0
            Requests      = 0
            Mean          = $null
            Median        = $null
            P10           = $null
            Ceiling       = $null
            Family        = $null
            Module        = $null
            Knob          = $null
            Order         = $null
            Failures      = 0
            WarningRows   = 0
            Warnings      = 0
            InputTokens   = 0
            OutputTokens  = 0
            CostUsd       = $null
            CostSource    = 'none'
            WallSeconds   = $WallSeconds
            Anchored      = $false
            Provider      = ''
            ArtifactDir   = ''
        }
    }

    $rows = @($Run.Results)
    $warningRows = @($rows | Where-Object { $null -ne $_.Warnings -and @($_.Warnings).Count -gt 0 }).Count
    $warnings = ($rows | ForEach-Object { @($_.Warnings).Count } | Measure-Object -Sum).Sum
    if ($null -eq $warnings) { $warnings = 0 }

    # The run's own CostUsd is billed tokens at the prices the run was TOLD
    # (Compare-Models.ps1 passes the model's real OpenRouter price through
    # DOTCHARTER_AI_EVAL_PRICE_IN/_OUT), so it is the real number whenever those
    # were known. When they were not, it is the report's Opus-list estimate and
    # the column says so.
    # TODO(feat/ai-provider-compat): if AiUsage gains a CostUsd carried back from
    # the provider's own reply, prefer THAT over this arithmetic and label the
    # source 'provider'. Do not add the field here — that branch owns it.
    $costSource = if ($null -ne $InUsdPerMTok -and $null -ne $OutUsdPerMTok) {
        'billed tokens x OpenRouter list'
    }
    else {
        'estimate (Opus list price)'
    }

    [pscustomobject]@{
        Model         = $Model
        Status        = $Status
        Failure       = $Failure
        Songs         = $Run.Songs
        Requests      = $rows.Count
        Mean          = $Run.Mean
        Median        = $Run.Median
        P10           = $Run.P10
        Ceiling       = $Run.MeanCeiling
        Family        = $Run.MeanFamily
        Module        = $Run.MeanModule
        Knob          = $Run.MeanKnob
        Order         = $Run.MeanOrder
        Failures      = $Run.Failures
        WarningRows   = $warningRows
        Warnings      = [int] $warnings
        InputTokens   = $Run.InputTokens
        OutputTokens  = $Run.OutputTokens
        CostUsd       = $Run.CostUsd
        CostSource    = $costSource
        WallSeconds   = $WallSeconds
        Anchored      = [bool] $Run.Anchored
        Provider      = [string] $Run.Provider
        ArtifactDir   = [string] $Run.ArtifactDirectory
    }
}

<#
.SYNOPSIS
    Ranks the rows: p10 first, then mean. Failed runs sink to the bottom.

.DESCRIPTION
    p10 before mean is the eval's own rule — "the p10 column matters more than the
    mean; it is the tail a user remembers" (tools/ai-eval/README.md). A model that
    is excellent four songs in five and unusable in the fifth is worse than a
    model that is merely good every time.
#>
function Sort-ToneEvalCompareRow {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Rows)

    # A run that PRODUCED numbers is rankable even when the test process came back
    # non-zero — a 'partial' (say two songs of thirty-five threw) still measured
    # thirty-three, and throwing that away would be the harness losing the data the
    # owner paid for.
    $all = @($Rows)
    $ok = @($all | Where-Object { $null -ne $_.P10 })
    $bad = @($all | Where-Object { $null -eq $_.P10 })

    @($ok | Sort-Object -Property @{ Expression = 'P10'; Descending = $true },
                                  @{ Expression = 'Mean'; Descending = $true },
                                  @{ Expression = 'Model'; Descending = $false }) +
    @($bad | Sort-Object -Property Model)
}

<#
.SYNOPSIS
    Renders compare.md from ranked rows.
#>
function Write-ToneEvalCompareMarkdown {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Rows,
        [hashtable] $Meta = @{}
    )

    $ci = [System.Globalization.CultureInfo]::InvariantCulture
    $ranked = @(Sort-ToneEvalCompareRow $Rows)
    $text = [System.Text.StringBuilder]::new()

    $stamp = if ($Meta.ContainsKey('TimestampUtc')) { $Meta['TimestampUtc'] } else { '' }
    [void]$text.AppendLine("# AI tone design — model comparison $stamp")
    [void]$text.AppendLine()

    foreach ($key in @('BaseUrl', 'Songs', 'Short', 'Mode', 'Corpus')) {
        if ($Meta.ContainsKey($key)) { [void]$text.AppendLine("- **$key**: $($Meta[$key])") }
    }
    $anchored = @($ranked | Where-Object { $_.Anchored }).Count -gt 0
    if ($anchored) {
        [void]$text.AppendLine('- **ANCHORED run** — every model was handed the song''s own converted ' +
            'Rocksmith tone, which is the chain the scorer grades against. These are UPPER BOUNDS, ' +
            'not generalisation scores, and may not be compared against an unanchored table.')
    }
    [void]$text.AppendLine('- **Ranked by p10, then mean** — the tail is what a user remembers.')
    [void]$text.AppendLine('- **~70 is the floor**, not a pass: a fixed `overdrive -> amp -> cab` at ' +
        'catalogue defaults scores 67.3 while knowing nothing about the song. The interesting range is 80+.')
    [void]$text.AppendLine()

    [void]$text.AppendLine('| # | Model | p10 | mean | p50 | ceil | fam | mod | knob | ord | fail | warn | in | out | $ | wall |')
    [void]$text.AppendLine('|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')

    $rank = 0
    foreach ($row in $ranked) {
        if ($null -ne $row.P10) {
            $rank++
            $label = if ($row.Status -eq 'ok') { '`' + $row.Model + '`' }
            else { '`' + $row.Model + '` (' + $row.Status + ')' }
            $cells = @(
                $rank
                $label
                (Format-Number $row.P10 1)
                (Format-Number $row.Mean 1)
                (Format-Number $row.Median 1)
                (Format-Number $row.Ceiling 1)
                (Format-Number $row.Family 2)
                (Format-Number $row.Module 2)
                (Format-Number $row.Knob 2)
                (Format-Number $row.Order 2)
                $row.Failures
                $row.Warnings
                $row.InputTokens.ToString('N0', $ci)
                $row.OutputTokens.ToString('N0', $ci)
                (Format-Usd $row.CostUsd)
                (Format-Number $row.WallSeconds 0) + 's'
            )
            [void]$text.AppendLine('| ' + ($cells -join ' | ') + ' |')
        }
        else {
            $why = if ([string]::IsNullOrWhiteSpace($row.Failure)) { 'run failed' } else { $row.Failure }
            [void]$text.AppendLine("| — | ``$($row.Model)`` | **$($row.Status.ToUpperInvariant())** — " +
                (Format-Cell $why) + ' | | | | | | | | | | | | | ' +
                (Format-Number $row.WallSeconds 0) + 's |')
        }
    }

    [void]$text.AppendLine()
    [void]$text.AppendLine('`fail` counts requests that threw; `warn` counts validator warnings ' +
        'across all requests (a model whose chains are constantly clamped is guessing).')
    $sources = @($ranked | Where-Object { $null -ne $_.P10 } | Select-Object -ExpandProperty CostSource -Unique)
    if ($sources.Count -gt 0) {
        [void]$text.AppendLine("`$` is " + ($sources -join ' / ') + '.')
    }
    [void]$text.AppendLine()

    [void]$text.AppendLine('## Runs')
    [void]$text.AppendLine()
    foreach ($row in $ranked) {
        [void]$text.AppendLine("### ``$($row.Model)`` — $($row.Status)")
        [void]$text.AppendLine()
        if (-not [string]::IsNullOrWhiteSpace($row.Failure)) {
            [void]$text.AppendLine("**$($row.Status.ToUpperInvariant())** — $($row.Failure)")
            [void]$text.AppendLine()
        }
        if ($null -eq $row.P10) { continue }

        [void]$text.AppendLine("- songs $($row.Songs), requests $($row.Requests), " +
            "$($row.Failures) failed, $($row.WarningRows) request(s) carried a validator warning")
        [void]$text.AppendLine("- mean $(Format-Number $row.Mean 1) of a reachable " +
            "$(Format-Number $row.Ceiling 1) · p50 $(Format-Number $row.Median 1) · " +
            "p10 $(Format-Number $row.P10 1)")
        [void]$text.AppendLine("- report: ``$($row.ArtifactDir)``")
        [void]$text.AppendLine()
    }

    return $text.ToString()
}

function Format-Number {
    param($Value, [int] $Digits)

    if ($null -eq $Value) { return '—' }
    return ([double]$Value).ToString("F$Digits", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Usd {
    param($Value)

    if ($null -eq $Value) { return '—' }
    return '$' + ([double]$Value).ToString('F4', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Format-Cell {
    param([string] $Value)

    if ($null -eq $Value) { return '' }
    return ($Value -replace '\r?\n', ' ').Replace('|', '\|')
}

Export-ModuleMember -Function @(
    'Get-ToneEvalDefaultModel'
    'Get-ToneEvalModelAlias'
    'ConvertFrom-OpenRouterCatalog'
    'Resolve-ToneEvalModel'
    'Get-ToneEvalCostEstimate'
    'Get-ToneEvalArtifactDirectory'
    'Remove-ToneEvalSecret'
    'Get-ToneEvalFailureReason'
    'ConvertTo-ToneEvalCompareRow'
    'Sort-ToneEvalCompareRow'
    'Write-ToneEvalCompareMarkdown'
)
