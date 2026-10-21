<#
.SYNOPSIS
    Splits the headless-UI suite (`*UiPerfSmokeTests`) across N concurrent
    `dotnet test` processes and merges what they report. Shared by
    tools/agent/Invoke-Gate.ps1 (-Headless), tools/ci/Invoke-HeadlessUiCi.ps1
    and tools/release/New-Release.ps1 (Invoke-HeadlessUiGate), so the three
    cannot drift on how the suite is cut.

.DESCRIPTION
    WHY PROCESSES. Every case runs on Avalonia.Headless's ONE process-global
    dispatcher, in the serialized `HeadlessUI` collection, so a single testhost
    uses one core of sixteen (measured 2026-10-04: 991 cases, 578 s of summed
    case time == the wall span). A second dispatcher needs a second PROCESS.

    THE PARTITION (New-HeadlessShardPlan) is deterministic and whole-class:
      * the unit is a test CLASS - a class's cases share fixtures, cached
        windows and ordering assumptions, so they always run together;
      * each class weighs its measured seconds from headless-shard-weights.json
        (summed TRX case durations of a full run). A class that file does not
        know yet - a NEW class - weighs its discovered case count times the
        suite's mean seconds per case, so it is assigned on the day it compiles
        without anybody editing a list;
      * longest-processing-time first: heaviest class to the lightest shard,
        ties broken by ordinal class name and lowest shard index. Same inputs,
        same plan, on every machine and culture.

    THE INPUT is the runner's own discovery (`--list-tests` with the suite's
    filter), so the set of classes the shards are given is exactly the set the
    plain filter selects. Each shard then names its classes with one exact
    `--filter-class` per class.

    THE MERGE (Merge-HeadlessTrx) folds the shards' TRX files into one TRX -
    results, definitions, entries, summed counters - that the callers' existing
    TRX readers grade unchanged. A shard that wrote NO TRX contributes nothing,
    so its classes are absent from the merged file and every caller's
    discovered-vs-reported check names them. That is the safety net for a
    crashed shard: absence, never silence.

    Runs under Windows PowerShell 5.1 and pwsh 7.
#>

Set-StrictMode -Off

# "Namespace.Class.Method(args)" -> "Namespace.Class". Theory arguments can
# contain dots, so the parameter list is cut off first.
function Get-HeadlessClassName([string]$TestName) {
    $bare = ($TestName -split '\(', 2)[0]
    if ($bare -match '^(.*)\.[^.]+$') { return $Matches[1] }
    return $bare
}

# One argument for a Windows command line (CommandLineToArgvW rules).
function ConvertTo-CommandLineArgument([string]$Value) {
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

<#
    Parses a `dotnet test --list-tests` log into class -> discovered case count.
    The runner prints a (localized!) banner and then one indented fully
    qualified name per line; this keys on the SHAPE of the line, not the banner,
    so a German shell lists the same set.
#>
function Read-HeadlessTestListing([string]$LogPath) {
    $counts = @{}
    foreach ($line in (Get-Content -LiteralPath $LogPath)) {
        $name = $line.Trim()
        if ($name -notmatch '^[A-Za-z_][\w.]*\.[A-Za-z_]\w*(\(.*\))?$' -or $name -notlike 'DotCharter.Tests.*') { continue }
        $class = Get-HeadlessClassName $name
        if ($counts.ContainsKey($class)) { $counts[$class]++ } else { $counts[$class] = 1 }
    }
    return $counts
}

<#
    Runs discovery for $Filter and returns class -> case count. $DotnetTestArgs is
    the caller's `test --project ... --no-build [-c ...] [-r ...]` prefix.
#>
function Get-HeadlessDiscovery([string[]]$DotnetTestArgs, [string[]]$Filter, [string]$LogPath) {
    & dotnet @($DotnetTestArgs + @('--list-tests') + $Filter) *> $LogPath
    return Read-HeadlessTestListing $LogPath
}

# class -> seconds, from the committed weights file. Missing file = no weights.
function Read-HeadlessShardWeights([string]$Path) {
    $weights = @{}
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $weights }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($p in $json.classes.PSObject.Properties) { $weights[$p.Name] = [double]$p.Value }
    return $weights
}

<#
    The deterministic whole-class partition. $CaseCounts: class -> discovered
    cases (the set to run). $Weights: class -> measured seconds (may be empty or
    stale). Returns one object per NON-EMPTY shard:
    Index (1-based), Classes (ordinal order), Weight (estimated seconds),
    Estimated (classes whose weight was estimated from their case count).
#>
function New-HeadlessShardPlan([System.Collections.IDictionary]$CaseCounts, [System.Collections.IDictionary]$Weights, [int]$ShardCount) {
    if ($ShardCount -lt 1) { throw "ShardCount must be at least 1 (got $ShardCount)." }

    # Mean seconds per case over the classes that are BOTH discovered and measured.
    $knownSeconds = 0.0; $knownCases = 0
    foreach ($class in @($CaseCounts.Keys)) {
        if ($Weights.Contains($class)) { $knownSeconds += $Weights[$class]; $knownCases += $CaseCounts[$class] }
    }
    $perCase = if ($knownCases -gt 0) { $knownSeconds / $knownCases } else { 1.0 }

    $items = New-Object 'System.Collections.Generic.List[object]'
    foreach ($class in @($CaseCounts.Keys)) {
        $estimated = -not $Weights.Contains($class)
        $w = if ($estimated) { $perCase * [double]$CaseCounts[$class] } else { [double]$Weights[$class] }
        $items.Add([pscustomobject]@{ Name = [string]$class; Weight = $w; Estimated = $estimated })
    }
    # Heaviest first; ties by ORDINAL name so the plan does not depend on the culture.
    $items.Sort([System.Comparison[object]] {
        param($a, $b)
        $c = $b.Weight.CompareTo($a.Weight)
        if ($c -ne 0) { return $c }
        return [string]::CompareOrdinal($a.Name, $b.Name)
    })

    $shards = @()
    for ($i = 0; $i -lt $ShardCount; $i++) {
        $shards += [pscustomobject]@{
            Index = $i + 1; Weight = 0.0
            Classes = (New-Object 'System.Collections.Generic.List[string]')
            Estimated = (New-Object 'System.Collections.Generic.List[string]')
        }
    }
    foreach ($item in $items) {
        $target = $shards[0]
        foreach ($s in $shards) { if ($s.Weight -lt $target.Weight) { $target = $s } }  # strict: lowest index wins a tie
        $target.Classes.Add($item.Name)
        if ($item.Estimated) { $target.Estimated.Add($item.Name) }
        $target.Weight += $item.Weight
    }

    $plan = @()
    foreach ($s in $shards) {
        if ($s.Classes.Count -eq 0) { continue }
        $sorted = $s.Classes.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
        $plan += [pscustomobject]@{
            Index = $s.Index; Classes = [string[]]$sorted; Weight = $s.Weight; Estimated = [string[]]$s.Estimated.ToArray()
        }
    }
    return $plan
}

# Every process id below (and including) $RootId, from one Win32_Process snapshot.
function Get-ProcessTreeIds([int]$RootId, $Snapshot) {
    $children = @{}
    foreach ($p in $Snapshot) {
        $parent = [int]$p.ParentProcessId
        if (-not $children.ContainsKey($parent)) { $children[$parent] = New-Object 'System.Collections.Generic.List[int]' }
        $children[$parent].Add([int]$p.ProcessId)
    }
    $ids = New-Object 'System.Collections.Generic.List[int]'
    $queue = New-Object 'System.Collections.Generic.Queue[int]'
    $queue.Enqueue($RootId)
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if ($ids.Contains($id)) { continue }
        $ids.Add($id)
        if ($children.ContainsKey($id)) { foreach ($c in $children[$id]) { $queue.Enqueue($c) } }
    }
    return $ids.ToArray()
}

function Stop-ProcessTree([int]$RootId) {
    $snapshot = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction SilentlyContinue)
    $ids = Get-ProcessTreeIds $RootId $snapshot
    [Array]::Reverse($ids)
    foreach ($id in $ids) { try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { } }
}

<#
    Starts one `dotnet test` per shard, all at once, samples each shard's process
    tree (`dotnet test` and the testhost it spawns) for its PEAK working set
    (WorkingSet64), and waits for all of them. Returns one object per shard:
    Index, Classes, Weight, ExitCode, Elapsed, PeakBytes, TrxPath, LogPath,
    TimedOut. The caller
    merges with Merge-HeadlessTrx.

    $ShardTimeout is a DEADLOCK DETECTOR for a hung testhost (the suite used to
    hang forever), never a budget: it is far above any run ever measured, and
    a shard it kills reports no TRX, so its classes are named as absent.
#>
function Invoke-HeadlessShards {
    param(
        [object[]]$Plan,
        [string[]]$DotnetTestArgs,
        [string]$ResultsDir,
        [string]$Label,
        [string]$Culture = '',
        [string]$WorkingDirectory = (Get-Location).Path,
        [TimeSpan]$ShardTimeout = [TimeSpan]::FromMinutes(60),
        [int]$SampleSeconds = 2
    )
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $runs = @()
    $previousCulture = $env:DOTCHARTER_HEADLESS_CULTURE
    if ($Culture) { $env:DOTCHARTER_HEADLESS_CULTURE = $Culture }
    try {
        foreach ($shard in $Plan) {
            $dir = Join-Path $ResultsDir ("{0}-shard{1}" -f $Label, $shard.Index)
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $trxName = "{0}-shard{1}.trx" -f $Label, $shard.Index
            $filter = @(); foreach ($c in $shard.Classes) { $filter += @('--filter-class', $c) }
            $argv = $DotnetTestArgs + $filter + @('--report-xunit-trx', '--report-xunit-trx-filename', $trxName, '--results-directory', $dir)
            $line = ($argv | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
            $out = Join-Path $dir 'stdout.log'; $err = Join-Path $dir 'stderr.log'
            $proc = Start-Process -FilePath 'dotnet' -ArgumentList $line -WorkingDirectory $WorkingDirectory `
                -RedirectStandardOutput $out -RedirectStandardError $err -NoNewWindow -PassThru
            $null = $proc.Handle  # Windows PowerShell 5.1 loses ExitCode unless the handle is cached now
            $runs += [pscustomobject]@{
                Index = $shard.Index; Classes = $shard.Classes; Weight = $shard.Weight; Process = $proc
                Started = Get-Date; Elapsed = [TimeSpan]::Zero; PeakBytes = [long]0; ExitCode = $null; TimedOut = $false
                TrxPath = Join-Path $dir $trxName; LogPath = Join-Path $dir 'shard.log'; StdOut = $out; StdErr = $err
            }
        }
    }
    finally { $env:DOTCHARTER_HEADLESS_CULTURE = $previousCulture }

    $peakSum = [long]0
    $finished = $false
    try {
    while (@($runs | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
        Start-Sleep -Seconds $SampleSeconds
        $snapshot = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction SilentlyContinue)
        $sum = [long]0
        foreach ($r in $runs) {
            if ($r.Process.HasExited) { continue }
            $bytes = [long]0
            foreach ($id in (Get-ProcessTreeIds $r.Process.Id $snapshot)) {
                try { $bytes += (Get-Process -Id $id -ErrorAction Stop).WorkingSet64 } catch { }
            }
            if ($bytes -gt $r.PeakBytes) { $r.PeakBytes = $bytes }
            $sum += $bytes
            if (((Get-Date) - $r.Started) -gt $ShardTimeout) {
                $r.TimedOut = $true
                Stop-ProcessTree $r.Process.Id
            }
        }
        if ($sum -gt $peakSum) { $peakSum = $sum }
    }
    $finished = $true
    }
    finally {
        # Ctrl+C or a throw while waiting: never leave a shard's testhost behind
        # holding the output DLLs (MSB3027 on the next build).
        if (-not $finished) { foreach ($r in $runs) { if (-not $r.Process.HasExited) { Stop-ProcessTree $r.Process.Id } } }
    }

    foreach ($r in $runs) {
        $r.Process.WaitForExit()
        $r.ExitCode = $r.Process.ExitCode
        if ($r.TimedOut -and ($null -eq $r.ExitCode -or $r.ExitCode -eq 0)) { $r.ExitCode = 124 }
        $r.Elapsed = $r.Process.ExitTime - $r.Process.StartTime
        $text = @()
        foreach ($f in @($r.StdOut, $r.StdErr)) { if (Test-Path -LiteralPath $f) { $text += Get-Content -LiteralPath $f } }
        if ($r.TimedOut) { $text += "[HeadlessShards] shard $($r.Index) passed the $($ShardTimeout.TotalMinutes)-minute deadlock detector and was KILLED." }
        Set-Content -LiteralPath $r.LogPath -Value $text
        if (-not (Test-Path -LiteralPath $r.TrxPath)) { $r.TrxPath = $null }
    }
    # The concurrent peak across all shards rides on every element so callers can report it.
    foreach ($r in $runs) { Add-Member -InputObject $r -NotePropertyName PeakSumBytes -NotePropertyValue $peakSum -Force }
    return $runs
}

<#
    Folds several xunit TRX files into one: UnitTestResult / UnitTest /
    TestEntry nodes are appended, every numeric Counters attribute is summed,
    the run window is the earliest start to the latest finish, and the outcome
    is Failed if any input failed. Inputs that are $null or missing are skipped
    - their cases are then simply ABSENT, which the callers' coverage checks
    report by class.
#>
function Merge-HeadlessTrx([string[]]$Paths, [string]$OutPath) {
    $ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    $existing = @($Paths | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($existing.Count -eq 0) { return $null }

    [xml]$base = Get-Content -LiteralPath $existing[0] -Raw
    $nsm = New-Object System.Xml.XmlNamespaceManager($base.NameTable)
    $nsm.AddNamespace('t', $ns)
    $root = $base.DocumentElement

    function Get-OrAdd([System.Xml.XmlNode]$Parent, [string]$Name) {
        $node = $Parent.SelectSingleNode("t:$Name", $nsm)
        if (-not $node) { $node = $Parent.AppendChild($base.CreateElement($Name, $ns)) }
        return $node
    }

    $counters = $base.SelectSingleNode('//t:ResultSummary/t:Counters', $nsm)
    $summary = $base.SelectSingleNode('//t:ResultSummary', $nsm)
    $times = $base.SelectSingleNode('t:Times', $nsm)

    for ($i = 1; $i -lt $existing.Count; $i++) {
        [xml]$doc = Get-Content -LiteralPath $existing[$i] -Raw
        $docNs = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
        $docNs.AddNamespace('t', $ns)
        foreach ($section in @('Results', 'TestDefinitions', 'TestEntries')) {
            $from = $doc.DocumentElement.SelectSingleNode("t:$section", $docNs)
            if (-not $from) { continue }
            $to = Get-OrAdd $root $section
            foreach ($child in @($from.ChildNodes)) { $null = $to.AppendChild($base.ImportNode($child, $true)) }
        }
        $c = $doc.SelectSingleNode('//t:ResultSummary/t:Counters', $docNs)
        if ($c -and $counters) {
            foreach ($a in $c.Attributes) {
                $n = 0L
                if ([long]::TryParse($a.Value, [ref]$n)) {
                    $cur = 0L; [void][long]::TryParse($counters.GetAttribute($a.Name), [ref]$cur)
                    $counters.SetAttribute($a.Name, [string]($cur + $n))
                }
            }
        }
        $s = $doc.SelectSingleNode('//t:ResultSummary', $docNs)
        if ($s -and $summary -and $s.GetAttribute('outcome') -eq 'Failed') { $summary.SetAttribute('outcome', 'Failed') }
        $t = $doc.DocumentElement.SelectSingleNode('t:Times', $docNs)
        if ($t -and $times) {
            foreach ($attr in @('creation', 'queuing', 'start')) {
                $mine = $times.GetAttribute($attr); $theirs = $t.GetAttribute($attr)
                if ($theirs -and (-not $mine -or [datetime]$theirs -lt [datetime]$mine)) { $times.SetAttribute($attr, $theirs) }
            }
            $mine = $times.GetAttribute('finish'); $theirs = $t.GetAttribute('finish')
            if ($theirs -and (-not $mine -or [datetime]$theirs -gt [datetime]$mine)) { $times.SetAttribute('finish', $theirs) }
        }
    }
    $dir = Split-Path -Parent $OutPath
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $base.Save($OutPath)
    return $OutPath
}

<#
    Rewrites the weights file from TRX results: class -> summed case duration in
    seconds. Run after a full headless run whose classes moved a lot; a stale
    file only costs balance, never coverage (an unknown class is estimated).
#>
function Write-HeadlessShardWeights([string[]]$TrxPaths, [string]$OutPath) {
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $seconds = @{}
    foreach ($path in $TrxPaths) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        [xml]$doc = Get-Content -LiteralPath $path -Raw
        foreach ($node in @((Select-Xml -Xml $doc -XPath '//t:UnitTestResult' -Namespace $ns) | ForEach-Object { $_.Node })) {
            $class = Get-HeadlessClassName ([string]$node.testName)
            $d = [TimeSpan]::Zero
            if ($node.duration) { [void][TimeSpan]::TryParse([string]$node.duration, [ref]$d) }
            if ($seconds.ContainsKey($class)) { $seconds[$class] += $d.TotalSeconds } else { $seconds[$class] = $d.TotalSeconds }
        }
    }
    $names = [string[]]@($seconds.Keys); [Array]::Sort($names, [StringComparer]::Ordinal)
    $lines = @()
    for ($i = 0; $i -lt $names.Count; $i++) {
        $sep = if ($i -lt $names.Count - 1) { ',' } else { '' }
        $lines += ('    "{0}": {1}{2}' -f $names[$i], [math]::Round($seconds[$names[$i]], 1).ToString([Globalization.CultureInfo]::InvariantCulture), $sep)
    }
    $header = @(
        '{',
        '  "_comment": "Measured seconds per headless-UI class (summed TRX case durations). Balances tools/ci/HeadlessShards.psm1; a class missing here is estimated from its case count. Regenerate: tools/agent/Invoke-Gate.ps1 -Headless -UpdateShardWeights",',
        '  "classes": {'
    )
    $text = (($header + $lines + @('  }', '}')) -join "`n") + "`n"
    [System.IO.File]::WriteAllText($OutPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

<#
    THE HARNESS-FAULT NEEDLES - the ONE list. A failed case whose message or
    stack contains one of these died inside Avalonia's headless session, before
    or after the test body, not on a product assertion:
      * "You've caused dispatcher loop" - Avalonia's per-test teardown drain
        overran its hard-coded 5 s (Dispatcher.ResetForUnitTests);
      * "EnsureIsolatedApplication" - the per-dispatch app setup threw, which is
        the setup race of #192 ("The calling thread cannot access this object"
        from ServerCompositor..ctor; DotCharter.Tests/Perf/HeadlessUiThreadClaim
        closes it).
    Invoke-Gate.ps1 -Headless (and so Merge-Train.ps1), tools/ci/Invoke-HeadlessUiCi.ps1
    and the release preflight in tools/release/New-Release.ps1 all read it from
    here; Infrastructure/CiWorkflowLintTests fails a script that keeps its own.
#>
$script:HeadlessHarnessFaults = @(
    "You've caused dispatcher loop",
    'EnsureIsolatedApplication'
)

function Get-HeadlessHarnessFaults { return , [string[]]@($script:HeadlessHarnessFaults) }

function Test-HeadlessHarnessFault([string]$Message, [string]$StackTrace) {
    $text = "$Message`n$StackTrace"
    foreach ($needle in $script:HeadlessHarnessFaults) { if ($text.Contains($needle)) { return $true } }
    return $false
}

# Every failed case in a TRX: Name, Class, Message (first non-empty line) and
# IsHarness (Test-HeadlessHarnessFault over the message and the stack).
function Read-HeadlessTrxFailures([string]$TrxPath) {
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    [xml]$doc = Get-Content -LiteralPath $TrxPath -Raw
    $nodes = @((Select-Xml -Xml $doc -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns) | ForEach-Object { $_.Node })
    foreach ($n in $nodes) {
        $message = ''; $stack = ''
        if ($n.Output -and $n.Output.ErrorInfo) {
            $message = ([string]$n.Output.ErrorInfo.Message).Trim()
            $stack = [string]$n.Output.ErrorInfo.StackTrace
        }
        [pscustomobject]@{
            Name      = [string]$n.testName
            Class     = Get-HeadlessClassName ([string]$n.testName)
            Message   = [string]($message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
            IsHarness = Test-HeadlessHarnessFault $message $stack
        }
    }
}

<#
    THE RACE RE-RUN DECISION, pure: what to do with a headless run's failures.
      * nothing failed and nothing is absent      -> Pass;
      * a discovered class reported NOTHING       -> Stop (a crashed shard is not the race);
      * any failure WITHOUT a harness-fault needle -> Stop (a product failure is never re-run away);
      * every failure carries a needle            -> Rerun, naming the classes to run
                                                     again, alone, in a fresh process.
    Returns Action ('Pass' | 'Rerun' | 'Stop'), Classes (sorted, unique) and a
    one-line Reason. Invoke-Gate.ps1 -Headless acts on it (Merge-Train.ps1 goes
    through Invoke-Gate); Infrastructure/HeadlessRaceRerunDecisionTests pins it.
#>
function Get-HeadlessRaceRerunDecision([object[]]$Failures, [string[]]$Absent) {
    $failures = @($Failures | Where-Object { $_ })
    $absent = [string[]]@($Absent | Where-Object { $_ })
    if ($failures.Count -eq 0 -and $absent.Count -eq 0) {
        return [pscustomobject]@{ Action = 'Pass'; Classes = [string[]]@(); Reason = 'no failure' }
    }
    if ($absent.Count -gt 0) {
        [Array]::Sort($absent, [StringComparer]::Ordinal)
        return [pscustomobject]@{ Action = 'Stop'; Classes = $absent
            Reason = "$($absent.Count) discovered class(es) reported nothing - not the setup race" }
    }
    $product = @($failures | Where-Object { -not $_.IsHarness })
    if ($product.Count -gt 0) {
        $names = [string[]]@($product | ForEach-Object { [string]$_.Name })
        return [pscustomobject]@{ Action = 'Stop'; Classes = [string[]]@($product | ForEach-Object { [string]$_.Class } | Sort-Object -Unique)
            Reason = "$($product.Count) failure(s) without the harness-fault signature: $($names -join ', ')" }
    }
    $classes = [string[]]@($failures | ForEach-Object { [string]$_.Class } | Sort-Object -Unique)
    [Array]::Sort($classes, [StringComparer]::Ordinal)
    return [pscustomobject]@{ Action = 'Rerun'; Classes = $classes
        Reason = "all $($failures.Count) failure(s) carry the harness-fault signature" }
}

<#
    FAILURE EVIDENCE (#55). A flaky headless case that fails once and leaves no
    message cannot be diagnosed, so every failed run keeps what it printed.

    Get-HeadlessFailureEvidenceRoot: $env:DOTCHARTER_HEADLESS_FAILURES_DIR (CI
    points it at a folder the workflow uploads), else
    %TEMP%\dotcharter-headless-failures.

    Save-HeadlessFailureEvidence copies the given TRX files and logs into a
    timestamped folder under the root, plus failures.txt: for every failed case
    its name, message, stack trace and captured stdout. It saves NOTHING (and
    returns $null) when no TRX holds a failed case, unless -Always is given (a
    crashed shard or a class that never reported leaves no failed case, only a
    log). -Folder adds a later capture (a re-run's) to the same run's folder, so
    the ORIGINAL failure's evidence survives a re-run that passes. A new folder
    prunes the root down to the newest -Keep (default 20) timestamped folders.
    Returns the folder path.
#>
function Get-HeadlessFailureEvidenceRoot {
    if ($env:DOTCHARTER_HEADLESS_FAILURES_DIR) { return [System.IO.Path]::GetFullPath($env:DOTCHARTER_HEADLESS_FAILURES_DIR) }
    return Join-Path ([System.IO.Path]::GetTempPath()) 'dotcharter-headless-failures'
}

function Remove-OldHeadlessFailureFolders([string]$Root, [int]$Keep = 20) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $folders = @(Get-ChildItem -LiteralPath $Root -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}' } |
        Sort-Object Name -Descending)
    if ($folders.Count -le $Keep) { return }
    foreach ($old in ($folders | Select-Object -Skip $Keep)) { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

function Save-HeadlessFailureEvidence {
    param(
        [string[]]$TrxPath,
        [string[]]$LogPath,
        [string]$Label = 'run',
        [string]$Root = '',
        [string]$Folder = '',
        [int]$Keep = 20,
        [switch]$Always
    )
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $trxs = @($TrxPath | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    $failed = @()
    foreach ($trx in $trxs) {
        [xml]$doc = Get-Content -LiteralPath $trx -Raw
        foreach ($n in @((Select-Xml -Xml $doc -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns) | ForEach-Object { $_.Node })) {
            $failed += [pscustomobject]@{ Trx = $trx; Node = $n }
        }
    }
    if ($failed.Count -eq 0 -and -not $Always) { return $null }

    $created = $false
    if (-not $Folder) {
        if (-not $Root) { $Root = Get-HeadlessFailureEvidenceRoot }
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $Folder = Join-Path $Root $stamp
        $n = 1
        while (Test-Path -LiteralPath $Folder) { $n++; $Folder = Join-Path $Root ("{0}-{1}" -f $stamp, $n) }
        $created = $true
    }
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null

    foreach ($trx in $trxs) { Copy-Item -LiteralPath $trx -Destination (Join-Path $Folder ("{0}-{1}" -f $Label, (Split-Path $trx -Leaf))) -Force }
    foreach ($log in @($LogPath | Where-Object { $_ -and (Test-Path -LiteralPath $_) })) {
        $parent = Split-Path (Split-Path $log -Parent) -Leaf
        Copy-Item -LiteralPath $log -Destination (Join-Path $Folder ("{0}-{1}-{2}" -f $Label, $parent, (Split-Path $log -Leaf))) -Force
    }

    $text = New-Object System.Text.StringBuilder
    [void]$text.AppendLine("[$Label] $($failed.Count) failed case(s)")
    foreach ($f in $failed) {
        $node = $f.Node
        $message = ''; $stack = ''; $stdout = ''
        if ($node.Output) {
            if ($node.Output.ErrorInfo) { $message = [string]$node.Output.ErrorInfo.Message; $stack = [string]$node.Output.ErrorInfo.StackTrace }
            if ($node.Output.StdOut) { $stdout = [string]$node.Output.StdOut }
        }
        [void]$text.AppendLine('')
        [void]$text.AppendLine("=== FAILED $($node.testName)  (from $(Split-Path $f.Trx -Leaf))")
        [void]$text.AppendLine('--- message'); [void]$text.AppendLine($message.Trim())
        [void]$text.AppendLine('--- stack trace'); [void]$text.AppendLine($stack.Trim())
        [void]$text.AppendLine('--- stdout'); [void]$text.AppendLine($stdout.Trim())
    }
    [System.IO.File]::AppendAllText((Join-Path $Folder 'failures.txt'), $text.ToString())

    if ($created) { Remove-OldHeadlessFailureFolders (Split-Path $Folder -Parent) $Keep }
    return $Folder
}

Export-ModuleMember -Function Get-HeadlessClassName, Read-HeadlessTestListing, Get-HeadlessDiscovery, `
    Read-HeadlessShardWeights, New-HeadlessShardPlan, Invoke-HeadlessShards, Merge-HeadlessTrx, `
    Write-HeadlessShardWeights, Stop-ProcessTree, ConvertTo-CommandLineArgument, `
    Get-HeadlessHarnessFaults, Test-HeadlessHarnessFault, Read-HeadlessTrxFailures, Get-HeadlessRaceRerunDecision, `
    Get-HeadlessFailureEvidenceRoot, Remove-OldHeadlessFailureFolders, Save-HeadlessFailureEvidence
